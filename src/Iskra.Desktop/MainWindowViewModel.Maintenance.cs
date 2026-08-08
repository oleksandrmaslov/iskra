using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using Avalonia.Media;
using Iskra.Application;
using Iskra.Core;

namespace Iskra.Desktop;

/// <summary>
/// GitHub sign-in, catalog/app update checks, cloud log shipping, and history
/// export. Each action is a thin frontend over an existing Core or Application
/// service; no trust, transport, or persistence policy lives here.
/// </summary>
public sealed partial class MainWindowViewModel
{
    // Windows DPAPI is the only encrypted token store today; elsewhere the
    // workflow is handed null and reports SecureStoreUnavailable rather than
    // offering a sign-in that could not be persisted safely.
    private readonly AuthWorkflow _authWorkflow =
        new(OperatingSystem.IsWindows() ? new TokenStore() : null);
    private readonly CloudLogWorkflow _cloudLogWorkflow = new();

    private AuthSnapshot? _authSnapshot;
    private string _authStatusText = string.Empty;
    private IBrush _authStatusBrush = StatusMutedBrush;
    private string _catalogUpdateStatusText = string.Empty;
    private IBrush _catalogUpdateStatusBrush = StatusMutedBrush;
    private string _catalogUpdateNoticeText = string.Empty;
    private string _appUpdateStatusText = string.Empty;
    private IBrush _appUpdateStatusBrush = StatusMutedBrush;
    private string? _latestReleaseUrl;
    private string _cloudStatusText = string.Empty;
    private string _cloudDetailText = string.Empty;
    private IBrush _cloudDetailBrush = StatusMutedBrush;
    private string _historyBatchSummaryText = string.Empty;
    private string _exportStatusText = string.Empty;
    private IBrush _exportStatusBrush = StatusMutedBrush;

    public AsyncRelayCommand SignInCommand { get; private set; } = null!;
    public RelayCommand SignOutCommand { get; private set; } = null!;
    public AsyncRelayCommand RefreshAuthCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckCatalogUpdateCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckAppUpdateCommand { get; private set; } = null!;
    public RelayCommand OpenReleasePageCommand { get; private set; } = null!;
    public AsyncRelayCommand ShipLogsNowCommand { get; private set; } = null!;
    public RelayCommand RefreshHistoryCommand { get; private set; } = null!;
    public AsyncRelayCommand ExportAllCommand { get; private set; } = null!;
    public AsyncRelayCommand ExportBatchCommand { get; private set; } = null!;

    private void InitializeMaintenanceSurface()
    {
        SignInCommand = new AsyncRelayCommand(SignInAsync, () => _authSnapshot?.CanSignIn ?? false);
        SignOutCommand = new RelayCommand(SignOut, () => _authSnapshot?.CanSignOut ?? false);
        RefreshAuthCommand = new AsyncRelayCommand(RefreshAuthTokenAsync);
        CheckCatalogUpdateCommand = new AsyncRelayCommand(CheckCatalogUpdateAsync);
        CheckAppUpdateCommand = new AsyncRelayCommand(CheckAppUpdateAsync);
        OpenReleasePageCommand = new RelayCommand(OpenReleasePage, () => _latestReleaseUrl is not null);
        ShipLogsNowCommand = new AsyncRelayCommand(ShipLogsNowAsync);
        RefreshHistoryCommand = new RelayCommand(RefreshHistory);
        ExportAllCommand = new AsyncRelayCommand(() => ExportAsync(HistoryExportScope.All));
        ExportBatchCommand = new AsyncRelayCommand(
            () => ExportAsync(HistoryExportScope.CurrentBatch),
            () => BatchesEnabled);
        ReloadCatalogCommand = new RelayCommand(() =>
        {
            CatalogUpdateNoticeText = string.Empty;
            RefreshReadiness();
        });
    }

    public RelayCommand ReloadCatalogCommand { get; private set; } = null!;

    public string AuthStatusText { get => _authStatusText; private set => SetProperty(ref _authStatusText, value); }
    public IBrush AuthStatusBrush { get => _authStatusBrush; private set => SetProperty(ref _authStatusBrush, value); }
    public string CatalogUpdateStatusText { get => _catalogUpdateStatusText; private set => SetProperty(ref _catalogUpdateStatusText, value); }

    /// <summary>Banner on the Catalog tab: a newer signed catalog is cached and waiting for a reload.</summary>
    public string CatalogUpdateNoticeText
    {
        get => _catalogUpdateNoticeText;
        private set
        {
            if (SetProperty(ref _catalogUpdateNoticeText, value))
                OnPropertyChanged(nameof(HasCatalogUpdateNotice));
        }
    }

    public bool HasCatalogUpdateNotice => _catalogUpdateNoticeText.Length > 0;
    public IBrush CatalogUpdateStatusBrush { get => _catalogUpdateStatusBrush; private set => SetProperty(ref _catalogUpdateStatusBrush, value); }
    public string AppUpdateStatusText { get => _appUpdateStatusText; private set => SetProperty(ref _appUpdateStatusText, value); }
    public IBrush AppUpdateStatusBrush { get => _appUpdateStatusBrush; private set => SetProperty(ref _appUpdateStatusBrush, value); }
    public string CloudStatusText { get => _cloudStatusText; private set => SetProperty(ref _cloudStatusText, value); }
    public string CloudDetailText { get => _cloudDetailText; private set => SetProperty(ref _cloudDetailText, value); }
    public IBrush CloudDetailBrush { get => _cloudDetailBrush; private set => SetProperty(ref _cloudDetailBrush, value); }
    public string HistoryBatchSummaryText { get => _historyBatchSummaryText; private set => SetProperty(ref _historyBatchSummaryText, value); }
    public string ExportStatusText { get => _exportStatusText; private set => SetProperty(ref _exportStatusText, value); }
    public IBrush ExportStatusBrush { get => _exportStatusBrush; private set => SetProperty(ref _exportStatusBrush, value); }

    public string AppVersionText =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Device Flow needs an encrypted token store, which currently exists only
    /// on Windows. Elsewhere the whole section is shown disabled instead of
    /// offering a sign-in that could not be persisted safely.
    /// </summary>
    public bool IsAuthSupported => OperatingSystem.IsWindows();

    // ============================================================
    // GitHub sign-in
    // ============================================================

    /// <summary>
    /// Classification lives in the shared <see cref="AuthWorkflow"/>; this method
    /// only renders it. WPF renders the same snapshot, so both frontends cannot
    /// drift on what "signed in" means.
    /// </summary>
    private void RefreshAuthStatus()
    {
        _authSnapshot = _authWorkflow.Evaluate();
        var (text, brush) = _authSnapshot.Status switch
        {
            AuthStatus.SecureStoreUnavailable => (Text.AuthUnsupportedPlatform, StatusWarnBrush),
            AuthStatus.ClientNotConfigured => (Text.AuthClientMissing, StatusErrorBrush),
            AuthStatus.TokenStoreCorrupt =>
                (Text.AuthTokenCorrupt(_authSnapshot.Diagnostic ?? string.Empty), StatusErrorBrush),
            AuthStatus.NotSignedIn => (Text.AuthNotSignedIn, StatusWarnBrush),
            AuthStatus.SessionExpired => (Text.AuthSessionExpired, StatusErrorBrush),
            _ => (Text.AuthSignedIn(
                    _authSnapshot.AccessTokenFresh ? Text.AuthAccessValid : Text.AuthAccessRefresh,
                    _authSnapshot.AccessTokenExpiresAtUtc?.ToLocalTime().ToString("g", Text.Culture) ?? "?"),
                StatusOkBrush),
        };

        AuthStatusText = text;
        AuthStatusBrush = brush;
        SignInCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
        RefreshRemoteFirmwareHint();
    }

    private void RefreshRemoteFirmwareHint() =>
        ShowAuthHint = SelectedRelease?.IsRemote == true
            && !(_authSnapshot?.CanFetchRemoteFirmware ?? false);

    private async Task SignInAsync()
    {
        if (!IsAuthSupported || !GitHubAppConfig.IsConfigured) return;
        if (Dialogs is null)
        {
            AuthStatusText = Text.DialogUnavailable;
            AuthStatusBrush = StatusErrorBrush;
            return;
        }

        try
        {
            using var http = new HttpClient();
            var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);

            DeviceCodeResponse code;
            try
            {
                code = await flow.RequestDeviceCodeAsync();
            }
            catch (Exception ex)
            {
                await Dialogs.ShowMessageAsync(Text.AuthSignIn, Text.AuthDeviceCodeFailed(ex.Message));
                return;
            }

            var outcome = await Dialogs.RunDeviceFlowAsync(flow, code, Text);
            if (!outcome.IsSuccess)
            {
                if (!string.IsNullOrEmpty(outcome.ErrorMessage))
                    await Dialogs.ShowMessageAsync(Text.AuthSignIn, outcome.ErrorMessage);
                return;
            }

            try
            {
                SaveTokens(StoredTokens.From(outcome.Token!, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                await Dialogs.ShowMessageAsync(Text.AuthSignIn, Text.AuthSaveFailed(ex.Message));
            }
        }
        finally
        {
            RefreshAuthStatus();
        }
    }

    private static void SaveTokens(StoredTokens tokens)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("no encrypted token store on this platform");
        new TokenStore().Save(tokens);
    }

    private void SignOut()
    {
        var after = _authWorkflow.SignOut();
        if (after.Status == AuthStatus.TokenStoreCorrupt && after.Diagnostic is { } diagnostic)
        {
            _authSnapshot = after;
            AuthStatusText = Text.AuthDeleteFailed(diagnostic);
            AuthStatusBrush = StatusErrorBrush;
            return;
        }

        RefreshAuthStatus();
    }

    private async Task RefreshAuthTokenAsync()
    {
        RefreshAuthStatus();
        // Explicit OS check rather than IsAuthSupported: the platform analyzer
        // only recognises OperatingSystem.IsWindows() as a guard for TokenStore.
        if (!OperatingSystem.IsWindows() || !GitHubAppConfig.IsConfigured) return;

        if (!_authWorkflow.Evaluate().CanSignOut) return;

        try
        {
            using var http = new HttpClient();
            var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);
            var provider = new AccessTokenProvider(new TokenStore(), flow);
            await provider.GetFreshAccessTokenAsync();
            RefreshAuthStatus();
        }
        catch (RefreshTokenExpiredException)
        {
            // The provider deletes the rejected blob; re-read the new state.
            RefreshAuthStatus();
        }
        catch (Exception ex)
        {
            AuthStatusText = Text.AuthRefreshFailed(ex.Message);
            AuthStatusBrush = StatusErrorBrush;
        }
    }

    // ============================================================
    // Catalog / app updates
    // ============================================================

    private async Task CheckCatalogUpdateAsync()
    {
        SetCatalogUpdate(Text.UpdateChecking, StatusMutedBrush);
        try
        {
            using var http = new HttpClient();
            var client = new RemoteCatalogClient(http);
            var result = await client.FetchAsync();
            var (message, brush) = result.Status switch
            {
                RemoteCatalogStatus.Updated => (Text.CatalogUpdated(result.TagName), StatusOkBrush),
                RemoteCatalogStatus.AlreadyUpToDate => (Text.CatalogUpToDate(result.TagName), StatusOkBrush),
                RemoteCatalogStatus.NoRelease => (Text.CatalogNoRelease, StatusWarnBrush),
                RemoteCatalogStatus.NetworkError => (Text.CatalogNetworkError, StatusWarnBrush),
                RemoteCatalogStatus.BadSignature => (Text.CatalogBadSignature, StatusErrorBrush),
                RemoteCatalogStatus.AssetsMissing => (Text.CatalogAssetsMissing, StatusErrorBrush),
                RemoteCatalogStatus.ParseError => (Text.CatalogParseError, StatusErrorBrush),
                RemoteCatalogStatus.SourceNotAllowed => (Text.CatalogSourceNotAllowed, StatusErrorBrush),
                RemoteCatalogStatus.RollbackRejected => (Text.CatalogRollbackRejected, StatusErrorBrush),
                _ => (result.Message ?? Text.CatalogError, StatusErrorBrush),
            };
            SetCatalogUpdate(message, brush);

            // A newly committed catalog only takes effect once the session
            // re-resolves its source, so refresh rather than leaving the
            // operator on the previous product list.
            if (result.Status == RemoteCatalogStatus.Updated) RefreshReadiness();
        }
        catch (Exception ex)
        {
            SetCatalogUpdate($"✗ {ex.Message}", StatusErrorBrush);
        }
    }

    private async Task CheckAppUpdateAsync()
    {
        SetAppUpdate(Text.UpdateChecking, StatusMutedBrush);
        try
        {
            using var http = new HttpClient();
            var client = new AppUpdateClient(
                http,
                GitHubAppConfig.AppUpdatesRepoOwner,
                GitHubAppConfig.AppUpdatesRepoName);
            var result = await client.CheckLatestAsync(AppVersionText);
            _latestReleaseUrl = result.ReleaseUrl;
            OpenReleasePageCommand.RaiseCanExecuteChanged();

            var (message, brush) = result.Status switch
            {
                AppUpdateStatus.UpdateAvailable =>
                    (Text.AppUpdateAvailable(result.LatestVersion ?? "?"), StatusOkBrush),
                AppUpdateStatus.UpToDate => (Text.AppUpToDate, StatusOkBrush),
                AppUpdateStatus.NoRelease => (Text.AppNoRelease, StatusWarnBrush),
                AppUpdateStatus.NetworkError => (Text.CatalogNetworkError, StatusWarnBrush),
                _ => (result.Message ?? Text.AppUpdateParseError, StatusErrorBrush),
            };
            SetAppUpdate(message, brush);
        }
        catch (Exception ex)
        {
            SetAppUpdate($"✗ {ex.Message}", StatusErrorBrush);
        }
    }

    private void OpenReleasePage()
    {
        if (_latestReleaseUrl is null) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _latestReleaseUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetAppUpdate($"✗ {ex.Message}", StatusErrorBrush);
        }
    }

    private void SetCatalogUpdate(string text, IBrush brush)
    {
        CatalogUpdateStatusText = text;
        CatalogUpdateStatusBrush = brush;
    }

    /// <summary>
    /// Non-blocking startup check against the locked catalog source, mirroring
    /// WPF. It deliberately does not swap the catalog under a station that may
    /// be mid-batch: on a new tag it only raises a notice, and the operator
    /// chooses when to reload.
    /// </summary>
    public async Task BackgroundFetchCatalogAsync()
    {
        if (!_settings.CatalogAutoUpdate) return;

        try
        {
            using var http = new HttpClient();
            var result = await new RemoteCatalogClient(http).FetchAsync().ConfigureAwait(true);
            if (result.Status == RemoteCatalogStatus.Updated && result.ChangedFromCached)
            {
                CatalogUpdateNoticeText = Text.CatalogUpdateAvailable(result.TagName);
                SetCatalogUpdate(Text.CatalogUpdated(result.TagName), StatusOkBrush);
            }
        }
        catch (Exception ex)
        {
            // Startup convenience only: a station with no network must still
            // reach a usable Flash tab.
            SetCatalogUpdate($"✗ {ex.Message}", StatusWarnBrush);
        }
    }

    private void SetAppUpdate(string text, IBrush brush)
    {
        AppUpdateStatusText = text;
        AppUpdateStatusBrush = brush;
    }

    // ============================================================
    // Cloud log mirror
    // ============================================================

    private void RefreshCloudStatus()
    {
        var snapshot = _cloudLogWorkflow.Inspect(_settings);
        var (status, detail, brush) = snapshot.Status switch
        {
            CloudLogStatus.Disabled => (Text.CloudDisabledShort, Text.CloudDisabledDetail, StatusMutedBrush),
            CloudLogStatus.NotConfigured =>
                (Text.CloudUnconfiguredShort, Text.CloudUnconfiguredDetail, StatusWarnBrush),
            CloudLogStatus.NoDatabase => (Text.CloudEmptyShort, Text.CloudEmptyDetail, StatusMutedBrush),
            CloudLogStatus.Synced => (Text.CloudSyncedShort, Text.CloudUploadedAll, StatusOkBrush),
            CloudLogStatus.Pending => (
                Text.CloudQueuedShort(snapshot.PendingRows),
                Text.CloudRowsWaiting(snapshot.PendingRows),
                StatusWarnBrush),
            _ => (Text.CloudErrorShort, $"✗ {snapshot.Diagnostic}", StatusErrorBrush),
        };

        SetCloud(status, detail, brush);
    }

    private void SetCloud(string status, string detail, IBrush brush)
    {
        CloudStatusText = status;
        CloudDetailText = detail;
        CloudDetailBrush = brush;
    }

    private async Task ShipLogsNowAsync()
    {
        SetCloud(CloudStatusText, Text.CloudUploading, StatusMutedBrush);
        // Pass the edited key path rather than the saved one: the operator may
        // be pointing at a freshly delivered .pem before saving Settings.
        var result = await _cloudLogWorkflow.ShipAsync(_settings, _logShipperKeyInput);

        var (detail, brush) = result.Status switch
        {
            CloudShipStatus.Shipped => (
                Text.CloudUploadReport(
                    result.RowsPushed, result.FilesCreated, result.FilesUpdated, result.RowsLeftover),
                StatusOkBrush),
            CloudShipStatus.Disabled => (Text.CloudEnableFirst, StatusWarnBrush),
            CloudShipStatus.NotConfigured => (Text.CloudUnconfiguredDetail, StatusErrorBrush),
            CloudShipStatus.KeyMissing => (Text.CloudKeyMissing(result.KeyPath ?? string.Empty), StatusErrorBrush),
            CloudShipStatus.NoDatabase => (Text.CloudEmptyDetail, StatusMutedBrush),
            _ => ($"✗ {result.Diagnostic}", StatusErrorBrush),
        };

        RefreshCloudStatus();
        SetCloud(CloudStatusText, detail, brush);
    }

    // ============================================================
    // History export
    // ============================================================

    private async Task ExportAsync(HistoryExportScope scope)
    {
        if (Dialogs is null)
        {
            SetExportStatus(Text.DialogUnavailable, StatusErrorBrush);
            return;
        }

        var suggested = scope == HistoryExportScope.CurrentBatch && !string.IsNullOrWhiteSpace(_batchId)
            ? $"iskra-{Sanitize(_batchId)}.csv"
            : $"iskra-{DateTime.Now:yyyy-MM-dd}.csv";

        var target = await Dialogs.SaveFileAsync(
            Text.DialogExportTitle, Text.DialogFilterCsv, ["*.csv"], suggested);
        if (target is null) return;

        var result = _historyWorkflow.Export(_settings, _batchId, scope, target);
        var (message, brush) = result.Status switch
        {
            HistoryExportStatus.Exported => (Text.ExportDone(result.RowsWritten, target), StatusOkBrush),
            HistoryExportStatus.BatchesDisabled => (Text.ExportBatchesDisabled, StatusWarnBrush),
            HistoryExportStatus.BatchRequired => (Text.ExportBatchRequired, StatusWarnBrush),
            HistoryExportStatus.DatabaseMissing => (Text.ExportNoDatabase, StatusWarnBrush),
            _ => ($"✗ {result.Diagnostic ?? string.Empty}", StatusErrorBrush),
        };
        SetExportStatus(message, brush);
    }

    private void SetExportStatus(string text, IBrush brush)
    {
        ExportStatusText = text;
        ExportStatusBrush = brush;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. value.Trim().Select(c => invalid.Contains(c) ? '_' : c)]);
    }
}
