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
    private static readonly TimeSpan AuthRefreshSkew = TimeSpan.FromMinutes(5);

    private string _authStatusText = string.Empty;
    private IBrush _authStatusBrush = StatusMutedBrush;
    private bool _canSignIn;
    private bool _canSignOut;
    private string _catalogUpdateStatusText = string.Empty;
    private IBrush _catalogUpdateStatusBrush = StatusMutedBrush;
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
        SignInCommand = new AsyncRelayCommand(SignInAsync, () => _canSignIn);
        SignOutCommand = new RelayCommand(SignOut, () => _canSignOut);
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
    }

    public string AuthStatusText { get => _authStatusText; private set => SetProperty(ref _authStatusText, value); }
    public IBrush AuthStatusBrush { get => _authStatusBrush; private set => SetProperty(ref _authStatusBrush, value); }
    public string CatalogUpdateStatusText { get => _catalogUpdateStatusText; private set => SetProperty(ref _catalogUpdateStatusText, value); }
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

    private void RefreshAuthStatus()
    {
        if (!IsAuthSupported)
        {
            SetAuth(Text.AuthUnsupportedPlatform, StatusWarnBrush, canSignIn: false, canSignOut: false);
            return;
        }

        if (!GitHubAppConfig.IsConfigured)
        {
            SetAuth(Text.AuthClientMissing, StatusErrorBrush, canSignIn: false, canSignOut: false);
            return;
        }

        StoredTokens? stored;
        try
        {
            stored = LoadTokens();
        }
        catch (TokenStoreException ex)
        {
            SetAuth(Text.AuthTokenCorrupt(ex.Message), StatusErrorBrush, canSignIn: true, canSignOut: true);
            return;
        }

        if (stored is null)
        {
            SetAuth(Text.AuthNotSignedIn, StatusWarnBrush, canSignIn: true, canSignOut: false);
            return;
        }

        var now = DateTime.UtcNow;
        if (stored.RefreshTokenIsExpired(now))
        {
            SetAuth(Text.AuthSessionExpired, StatusErrorBrush, canSignIn: true, canSignOut: true);
            return;
        }

        var access = stored.AccessTokenIsFresh(now, AuthRefreshSkew)
            ? Text.AuthAccessValid
            : Text.AuthAccessRefresh;
        SetAuth(
            Text.AuthSignedIn(access, stored.AccessTokenExpiresAtUtc.ToLocalTime().ToString("g", Text.Culture)),
            StatusOkBrush,
            canSignIn: true,
            canSignOut: true);
    }

    private void SetAuth(string text, IBrush brush, bool canSignIn, bool canSignOut)
    {
        AuthStatusText = text;
        AuthStatusBrush = brush;
        _canSignIn = canSignIn;
        _canSignOut = canSignOut;
        SignInCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
        RefreshRemoteFirmwareHint();
    }

    private void RefreshRemoteFirmwareHint() =>
        ShowAuthHint = SelectedRelease?.IsRemote == true
            && !DesktopRemoteFirmwareProvider.CanFetchRemoteFirmware();

    private static StoredTokens? LoadTokens() =>
        OperatingSystem.IsWindows() ? new TokenStore().Load() : null;

    private async Task SignInAsync()
    {
        if (!IsAuthSupported || !GitHubAppConfig.IsConfigured) return;
        if (Dialogs is null) { SetAuth(Text.DialogUnavailable, StatusErrorBrush, _canSignIn, _canSignOut); return; }

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
        try
        {
            if (OperatingSystem.IsWindows()) new TokenStore().Delete();
        }
        catch (Exception ex)
        {
            SetAuth(Text.AuthDeleteFailed(ex.Message), StatusErrorBrush, _canSignIn, _canSignOut);
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

        StoredTokens? stored;
        try { stored = LoadTokens(); } catch { stored = null; }
        if (stored is null) return;

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
            SetAuth(Text.AuthRefreshFailed(ex.Message), StatusErrorBrush, _canSignIn, _canSignOut);
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
        if (!_settings.LogShippingEnabled)
        {
            SetCloud(Text.CloudDisabledShort, Text.CloudDisabledDetail, StatusMutedBrush);
            return;
        }

        if (!GitHubAppConfig.IsLogShipperConfigured)
        {
            SetCloud(Text.CloudUnconfiguredShort, Text.CloudUnconfiguredDetail, StatusWarnBrush);
            return;
        }

        try
        {
            var dbPath = ApplicationPaths.ResolveDatabasePath(_settings);
            if (!File.Exists(dbPath))
            {
                SetCloud(Text.CloudEmptyShort, Text.CloudEmptyDetail, StatusMutedBrush);
                return;
            }

            using var store = new SqliteLogStore(dbPath);
            var pending = store.CountUnsynced();
            SetCloud(
                pending == 0 ? Text.CloudSyncedShort : Text.CloudQueuedShort(pending),
                pending == 0 ? Text.CloudUploadedAll : Text.CloudRowsWaiting(pending),
                pending == 0 ? StatusOkBrush : StatusWarnBrush);
        }
        catch (Exception ex)
        {
            SetCloud(Text.CloudErrorShort, $"✗ {ex.Message}", StatusErrorBrush);
        }
    }

    private void SetCloud(string status, string detail, IBrush brush)
    {
        CloudStatusText = status;
        CloudDetailText = detail;
        CloudDetailBrush = brush;
    }

    private async Task ShipLogsNowAsync()
    {
        if (!_settings.LogShippingEnabled)
        {
            SetCloud(CloudStatusText, Text.CloudEnableFirst, StatusWarnBrush);
            return;
        }

        if (!GitHubAppConfig.IsLogShipperConfigured)
        {
            SetCloud(CloudStatusText, Text.CloudUnconfiguredDetail, StatusErrorBrush);
            return;
        }

        var keyPath = string.IsNullOrWhiteSpace(_logShipperKeyInput)
            ? _settings.LogShipperPrivateKeyPath
            : _logShipperKeyInput.Trim();
        if (!File.Exists(keyPath))
        {
            SetCloud(CloudStatusText, Text.CloudKeyMissing(keyPath), StatusErrorBrush);
            return;
        }

        SetCloud(CloudStatusText, Text.CloudUploading, StatusMutedBrush);
        try
        {
            ShipReport report;
            using (var store = new SqliteLogStore(ApplicationPaths.ResolveDatabasePath(_settings)))
            using (var http = new HttpClient())
            {
                var tokens = new GitHubAppInstallationTokenProvider(
                    http,
                    GitHubAppConfig.LogShipperAppId,
                    GitHubAppConfig.LogShipperInstallationId,
                    () => GitHubAppInstallationTokenProvider.LoadPemKey(keyPath));
                var shipper = new LogShipper(
                    store, tokens, http,
                    GitHubAppConfig.LogsRepoOwner,
                    GitHubAppConfig.LogsRepoName);
                report = await shipper.ShipPendingAsync();
            }

            SetCloud(
                CloudStatusText,
                Text.CloudUploadReport(report.RowsPushed, report.FilesCreated, report.FilesUpdated, report.RowsLeftover),
                StatusOkBrush);
        }
        catch (Exception ex)
        {
            SetCloud(CloudStatusText, $"✗ {ex.Message}", StatusErrorBrush);
        }
        finally
        {
            RefreshCloudStatus();
        }
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
