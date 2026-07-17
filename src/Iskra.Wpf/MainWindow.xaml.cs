using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Iskra.Application;
using Iskra.Core;
using Microsoft.Win32;

namespace Iskra.Wpf;

public partial class MainWindow : Window
{
    private static string T(string key, params object?[] args) => UiText.Get(key, args);

    private AppSettings _settings = new();
    private Catalog? _catalog;
    private string? _catalogPath;
    private string? _catalogDir;
    private string? _gdbExe;
    private string? _port;
    private string? _probeSerial;
    private string? _lastAppUpdateUrl;
    private readonly ICatalogSession _catalogSession = new CatalogSession();
    private readonly FlashWorkflow _flashWorkflow = new(new GitHubRemoteFirmwareProvider());
    private readonly HistoryWorkflow _historyWorkflow = new();
    private readonly SettingsWorkflow _settingsWorkflow = new();
    private bool _isLoaded;
    private bool _flashInProgress;
    private bool _suppressTabSelectionChanged;
    private bool _isApplyingSettings;
    private TabItem? _previousMainTab;
    private readonly string _activeLanguageCode =
        IskraLanguages.NormalizeOrDefault(CultureInfo.CurrentUICulture.Name);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        // Window-level KeyDown so the operator hotkey works from anywhere on
        // the Flash tab — even with focus on the Operator/Batch boxes (Enter
        // is what barcode scanners emit as a line terminator).
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        MainTabs.SelectionChanged += MainTabs_SelectionChanged;
        Closing += MainWindow_Closing;
        SettingsTab.AddHandler(
            TextBox.TextChangedEvent,
            new TextChangedEventHandler(SettingsTextChanged));
        SettingsTab.AddHandler(
            ToggleButton.CheckedEvent,
            new RoutedEventHandler(SettingsControlChanged));
        SettingsTab.AddHandler(
            ToggleButton.UncheckedEvent,
            new RoutedEventHandler(SettingsControlChanged));
        SettingsTab.AddHandler(
            Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler(SettingsSelectionChanged));
        _previousMainTab = MainTabs.SelectedItem as TabItem;
    }

    // ============================================================
    // Startup
    // ============================================================

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsWorkflow.Load();
        ApplySettingsToUI();

        if (!string.IsNullOrEmpty(_settings.LastOperator))
            OperatorBox.Text = _settings.LastOperator;
        if (_settings.BatchesEnabled && !string.IsNullOrEmpty(_settings.LastBatch))
            BatchBox.Text = _settings.LastBatch;

        DiscoverGdb();
        DiscoverProbe();
        LoadCatalog();
        RefreshHistory();
        RefreshAuthStatus();
        RefreshCatalogCacheStatus();
        RefreshAppUpdateStatus();
        RefreshCloudSyncStatus();
        ApplyBatchModeToUI();

        _isLoaded = true;
        _previousMainTab = MainTabs.SelectedItem as TabItem;
        RefreshFlashReadiness(updateBanner: true);

        // Sprint 3.5: kick off a non-blocking background fetch of the remote
        // catalog. On success we cache to disk; user clicks "Перезавантажити"
        // on the Catalog tab to pick up the new content mid-session.
        if (_settings.CatalogAutoUpdate)
            _ = BackgroundFetchCatalogAsync();
    }

    private void DiscoverGdb()
    {
        _gdbExe = GdbDiscovery.Find(_settings.GdbPath);
        StatusGdb.Text = _gdbExe is null
            ? T("Gdb.NotFound.Status")
            : $"gdb: {Path.GetFileName(_gdbExe)}";
        StatusGdb.Foreground = _gdbExe is null
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0xB3))
            : new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0x9C));
    }

    private void DiscoverProbe()
    {
        var probes = ProbeDiscovery.FindGdbPorts();
        if (probes.Count == 1)
        {
            _port = probes[0].PortName;
            _probeSerial = probes[0].SerialNumber;
            StatusPort.Text = T("Probe.Port", _port,
                string.IsNullOrWhiteSpace(_probeSerial) ? "" : $" · SN {_probeSerial}");
            StatusPort.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0x9C));
        }
        else if (probes.Count == 0)
        {
            _port = null;
            _probeSerial = null;
            StatusPort.Text = T("Probe.NotFound.Status");
            StatusPort.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0xB3));
        }
        else
        {
            _port = null;
            _probeSerial = null;
            StatusPort.Text = T("Probe.Multiple.Status", probes.Count);
            StatusPort.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x80));
        }
    }

    private void ProbeRefresh_Click(object sender, RoutedEventArgs e)
    {
        ProbeRefreshButton.IsEnabled = false;
        try
        {
            DiscoverProbe();
            RefreshFlashReadiness(updateBanner: true);
        }
        finally
        {
            ProbeRefreshButton.IsEnabled = true;
        }
    }

    private void RefreshFlashReadiness(bool updateBanner)
    {
        var operatorMissing = string.IsNullOrWhiteSpace(OperatorBox.Text);
        var batchMissing = _settings.BatchesEnabled && string.IsNullOrWhiteSpace(BatchBox.Text);
        var selectionMissing = ProductCombo.SelectedItem is not string
            || VersionCombo.SelectedItem is not FirmwareRelease;

        var ready = _gdbExe is not null
            && _port is not null
            && _catalog is not null
            && !operatorMissing
            && !batchMissing
            && !selectionMissing;
        FlashButton.IsEnabled = !_flashInProgress && ready;

        if (!updateBanner || _flashInProgress)
            return;

        if (_port is null)
        {
            SetBannerNeutral(
                T("Ready.Probe.Title"),
                warning: true,
                T("Ready.Probe.Detail"));
        }
        else if (_gdbExe is null)
        {
            SetBannerNeutral(
                T("Ready.Gdb.Title"),
                warning: true,
                T("Ready.Gdb.Detail"));
        }
        else if (_catalog is null)
        {
            SetBannerNeutral(
                T("Ready.Catalog.Title"),
                warning: true,
                T("Ready.Catalog.Detail"));
        }
        else if (operatorMissing)
        {
            SetBannerNeutral(T("Ready.Operator.Title"), warning: false, T("Ready.Operator.Detail"));
        }
        else if (batchMissing)
        {
            SetBannerNeutral(T("Ready.Batch.Title"), warning: false, T("Ready.Batch.Detail"));
        }
        else if (selectionMissing)
        {
            SetBannerNeutral(T("Ready.Selection.Title"), warning: false);
        }
        else
        {
            var detail = _settings.BatchesEnabled
                ? T("Ready.BatchEnabled.Detail")
                : T("Ready.BatchDisabled.Detail");
            SetBannerNeutral(T("Flash.Ready.Title"), warning: false, detail);
        }
    }

    private void SetFlashInProgress(bool value)
    {
        _flashInProgress = value;
        // Keep the inputs that feed the durable attempt record stable across
        // every await in the flash workflow. The current operation already
        // snapshots product/release/options locally; blocking Settings and BMP
        // refresh prevents station, database, batch-mode, port, or probe serial
        // from changing before the final log append.
        SettingsTab.IsEnabled = !value;
        ProbeRefreshButton.IsEnabled = !value;
        if (value)
            FlashButton.IsEnabled = false;
        else
            RefreshFlashReadiness(updateBanner: false);
    }

    private void LoadCatalog()
    {
        _catalog = null;
        _catalogPath = null;
        _catalogDir = null;
        ProductCombo.Items.Clear();
        VersionCombo.ItemsSource = null;

        // The shared application layer owns source precedence and trust. An
        // explicit path is authoritative, and the first existing fallback is
        // authoritative: a missing, malformed, or untrusted source never
        // silently downgrades to a different catalog.
        var session = _catalogSession.Load(_settings);
        if (!session.IsReady)
        {
            var message = CatalogFailureMessage(session);
            StatusCatalog.Text = T("Catalog.Status", message);
            CatalogHeader.Text = T("Catalog.Unavailable", message);
            CatalogProductsList.ItemsSource = null;
            return;
        }

        _catalog = session.Catalog;
        _catalogPath = session.SourcePath;
        _catalogDir = session.SourceDirectory;
        var trustText = session.IsSideload
            ? T("Catalog.Trust.Sideload")
            : session.TrustResult switch
            {
                CatalogTrustResult.Verified => "✓ Ed25519",
                CatalogTrustResult.UnsignedAllowed => T("Catalog.Trust.Unsigned"),
                _ => session.TrustResult?.ToString() ?? T("Catalog.Trust.Unknown"),
            };

        StatusCatalog.Text = T("Catalog.Loaded.Status", _catalog!.Products.Count,
            trustText, Path.GetFileName(_catalogPath));

        foreach (var product in _catalog.Products)
            ProductCombo.Items.Add(product.ProductId);
        if (ProductCombo.Items.Count > 0)
            ProductCombo.SelectedIndex = 0;

        var revokedCount = _catalog.Revoked?.Count ?? 0;
        var revokedSuffix = revokedCount > 0 ? T("Catalog.Revoked.Suffix", revokedCount) : "";
        CatalogHeader.Text = T("Catalog.Header", Path.GetFileName(_catalogPath),
            _catalog.Products.Count, trustText, revokedSuffix);
        CatalogProductsList.ItemsSource = _catalog.Products;
    }

    private static string CatalogFailureMessage(CatalogSessionResult session)
    {
        return session.Status switch
        {
            CatalogSessionStatus.NotFound => T("Catalog.Fail.NotFound"),
            CatalogSessionStatus.ExplicitPathMissing =>
                T("Catalog.Fail.PathMissing", session.SourcePath),
            CatalogSessionStatus.SideloadRequiresLabMode =>
                T("Catalog.Fail.SideloadLab"),
            CatalogSessionStatus.TrustRejected => session.TrustResult switch
            {
                CatalogTrustResult.UnsignedRejected => T("Catalog.Fail.Unsigned"),
                CatalogTrustResult.BadSignature => T("Catalog.Fail.BadSignature"),
                CatalogTrustResult.NoPublicKeyConfigured => T("Catalog.Fail.NoKey"),
                CatalogTrustResult.IoError => T("Catalog.Fail.SignatureIo"),
                _ => T("Catalog.Fail.Trust"),
            },
            CatalogSessionStatus.ParseError => T("Catalog.Fail.Parse", session.Diagnostic),
            CatalogSessionStatus.IoError => T("Catalog.Fail.Read", session.Diagnostic),
            _ => T("Catalog.Fail.Generic", session.Diagnostic),
        };
    }

    private void CatalogReload_Click(object sender, RoutedEventArgs e)
    {
        LoadCatalog();
        RefreshBatchLockStatus();
        RefreshFlashReadiness(updateBanner: true);
    }

    private void ProductCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        VersionCombo.ItemsSource = null;
        if (_catalog is null || ProductCombo.SelectedItem is not string id)
        {
            RefreshAuthBanner(null);
            RefreshFlashReadiness(updateBanner: true);
            return;
        }
        var product = _catalog.FindProduct(id);
        if (product is null)
        {
            RefreshAuthBanner(null);
            RefreshFlashReadiness(updateBanner: true);
            return;
        }

        VersionCombo.ItemsSource = product.Releases;
        // Pre-select the default release; setting SelectedItem fires
        // VersionCombo_SelectionChanged which refreshes the auth banner.
        VersionCombo.SelectedItem = product.Default() ?? product.Releases.FirstOrDefault();
    }

    private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshAuthBanner(VersionCombo.SelectedItem as FirmwareRelease);
        RefreshFlashReadiness(updateBanner: true);
    }

    // ============================================================
    // Flash
    // ============================================================

    private async void FlashButton_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog is null) { Beep(); return; }
        if (ProductCombo.SelectedItem is not string productId) { Beep(); return; }

        // Refresh the physical endpoint immediately before the shared workflow
        // snapshots its request. WPF remains a supported Windows frontend; it
        // now delegates transaction policy rather than duplicating it.
        DiscoverProbe();
        RefreshFlashReadiness(updateBanner: false);

        var product = _catalog.FindProduct(productId);
        var release = VersionCombo.SelectedItem as FirmwareRelease ?? product?.Default();
        if (product is null || release is null) { Beep(); return; }

        SetFlashInProgress(true);
        GdbOutput.Clear();
        try
        {
            var progress = new Progress<FlashWorkflowProgress>(update =>
            {
                if (update.Stage == FlashWorkflowStage.AcquiringFirmware && release.IsRemote)
                    SetBannerNeutral(T("Flash.Downloading"), warning: false);
                else if (update.Stage is FlashWorkflowStage.ValidatingFirmware or FlashWorkflowStage.Flashing)
                    SetBannerNeutral(T("Flash.Running"), warning: false);
            });
            var request = new FlashWorkflowRequest(
                Catalog: _catalog,
                CatalogDirectory: _catalogDir,
                ProductId: product.ProductId,
                FirmwareVersion: release.Version,
                Settings: _settings.Clone(),
                Operator: OperatorBox.Text,
                EnteredBatchId: BatchBox.Text,
                GdbPath: _gdbExe,
                Port: _port,
                ProbeSerial: _probeSerial);
            var result = await _flashWorkflow.ExecuteAsync(
                request,
                progress,
                line => Dispatcher.Invoke(() => GdbOutput.AppendText(line.Text + "\n")));

            if (result.IsBlocked)
            {
                ShowWorkflowBlocked(result);
            }
            else if (result.IsPass)
            {
                ShowPass(result.Outcome.Duration);
            }
            else
            {
                ShowFail(result.Outcome.ErrorCode ?? "E_INTERNAL",
                    result.Outcome.ErrorMessage ?? string.Empty);
            }

            // Remember successful acquisition choices. Settings remain a WPF
            // concern until Sprint 8 extracts a separate settings service.
            if (result.FirmwarePath is not null)
            {
                var remembered = _settingsWorkflow.RememberOperatorSelection(
                    _settings,
                    OperatorBox.Text,
                    result.EffectiveBatchId);
                if (remembered.IsSaved)
                    _settings = remembered.Settings!;
            }

            RefreshHistory();
            RefreshBatchLockStatus();
            RefreshCloudSyncStatus();
            if (result.Outcome.ErrorCode is "E_NOT_SIGNED_IN" or "E_AUTH_EXPIRED")
                RefreshAuthStatus();
        }
        catch (Exception ex)
        {
            ShowFail("E_INTERNAL", ex.Message);
        }
        finally
        {
            SetFlashInProgress(false);
        }
    }

    private void ShowWorkflowBlocked(FlashWorkflowResult result)
    {
        switch (result.Outcome.ErrorCode)
        {
            case "E_OPERATOR_REQUIRED":
                SetBannerNeutral(T("Flash.EnterOperator"), warning: true);
                break;
            case BatchPolicy.RequiredErrorCode:
                SetBannerNeutral(T("Flash.EnterBatch"), warning: true,
                    T("Flash.DisableBatchHint"));
                break;
            case "E_GDB_NOT_FOUND":
                SetBannerNeutral(T("Flash.GdbMissing"), warning: true);
                break;
            case "E_PROBE_NOT_FOUND":
                SetBannerNeutral(T("Flash.ProbeMissing"), warning: true,
                    T("Ready.Probe.Detail"));
                break;
            default:
                ShowFail(result.Outcome.ErrorCode ?? "E_INTERNAL",
                    result.Outcome.ErrorMessage ?? string.Empty);
                break;
        }
    }

    private void ShowPass(TimeSpan duration)
    {
        ResultBanner.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
        ResultText.Foreground = Brushes.White;
        ResultDetail.Foreground = Brushes.White;
        ResultText.Text = T("Flash.Success");
        ResultDetail.Text = T("Flash.Duration", duration.TotalMilliseconds);
    }

    private void ShowFail(string code, string detail)
    {
        ResultBanner.Background = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
        ResultText.Foreground = Brushes.White;
        ResultDetail.Foreground = Brushes.White;
        ResultText.Text = $"✗ {code}";
        ResultDetail.Text = UiText.ErrorHint(code);
        if (!string.IsNullOrEmpty(detail))
            GdbOutput.AppendText($"\n[{T("Flash.ErrorDetails")}]\n{detail}\n");
    }

    private void SetBannerNeutral(string msg, bool warning, string? detail = null)
    {
        ResultBanner.Background = warning
            ? new SolidColorBrush(Color.FromRgb(0xF2, 0xC1, 0x4E))
            : new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        ResultDetail.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        ResultText.Text = msg;
        ResultDetail.Text = detail ?? "";
    }

    private string ResolveDbPath()
    {
        return ApplicationPaths.ResolveDatabasePath(_settings, ensureDirectory: true);
    }

    // ============================================================
    // History tab
    // ============================================================

    private void HistoryRefresh_Click(object sender, RoutedEventArgs e) => RefreshHistory();

    private void OperatorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoaded) return;
        RefreshFlashReadiness(updateBanner: true);
    }

    private void BatchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isLoaded) return;
        RefreshBatchLockStatus();
        RefreshFlashReadiness(updateBanner: true);
    }

    private void RefreshBatchLockStatus()
    {
        var snapshot = _historyWorkflow.LookupBatchLock(_settings, BatchBox.Text);
        switch (snapshot.Status)
        {
            case BatchLockLookupStatus.BatchesDisabled:
            case BatchLockLookupStatus.BatchRequired:
            case BatchLockLookupStatus.DatabaseMissing:
                BatchLockLabel.Text = "";
                break;
            case BatchLockLookupStatus.Reserved:
                var l = snapshot.Lock!;
                BatchLockLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
                BatchLockLabel.Text = T("Batch.Locked", l.ProductId, l.FirmwareVersion, ShortSha(l.FirmwareSha256));
                break;
            case BatchLockLookupStatus.NotReserved:
                BatchLockLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
                BatchLockLabel.Text = T("Batch.New");
                break;
            default:
                BatchLockLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                BatchLockLabel.Text = T("Batch.CheckFailed", snapshot.Diagnostic ?? T("Common.Unknown"));
                break;
        }
    }

    private static string ShortSha(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? T("Common.Unknown")
            : value[..Math.Min(12, value.Length)].ToLowerInvariant();

    private void ExportCsvBatch_Click(object sender, RoutedEventArgs e)
        => ExportCsv(batchOnly: true);

    private void ExportCsvAll_Click(object sender, RoutedEventArgs e)
        => ExportCsv(batchOnly: false);

    private void ExportCsv(bool batchOnly)
    {
        if (batchOnly && !_settings.BatchesEnabled)
        {
            BatchSummary.Text = T("History.BatchesDisabled");
            return;
        }

        var availability = _historyWorkflow.Load(_settings, BatchBox.Text, limit: 1);
        if (availability.Status == HistoryLoadStatus.DatabaseMissing)
        {
            BatchSummary.Text = T("History.NothingToExport");
            return;
        }
        if (availability.Status == HistoryLoadStatus.Failed)
        {
            BatchSummary.Text = T("History.ExportError", availability.Diagnostic ?? T("Common.Unknown"));
            return;
        }

        string? batch = batchOnly ? BatchBox.Text?.Trim() : null;
        if (batchOnly && string.IsNullOrEmpty(batch))
        {
            BatchSummary.Text = T("History.EnterBatch");
            return;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var defaultName = batchOnly
            ? $"flash_log_{batch}_{stamp}.csv"
            : $"flash_log_all_{stamp}.csv";

        var dlg = new SaveFileDialog
        {
            Filter = T("Dialog.Filter.Csv"),
            Title = batchOnly ? T("History.ExportBatch.Title", batch) : T("History.ExportAll.Title"),
            FileName = defaultName,
        };
        if (dlg.ShowDialog() != true) return;

        var export = _historyWorkflow.Export(
            _settings,
            BatchBox.Text,
            batchOnly ? HistoryExportScope.CurrentBatch : HistoryExportScope.All,
            dlg.FileName);
        if (export.Status == HistoryExportStatus.Exported)
        {
            BatchSummary.Text = T("History.Exported", export.RowsWritten, Path.GetFileName(dlg.FileName));
        }
        else
        {
            BatchSummary.Text = T("History.ExportError", export.Diagnostic ?? export.Status.ToString());
        }
    }

    private void RefreshHistory()
    {
        var snapshot = _historyWorkflow.Load(_settings, BatchBox.Text);
        if (snapshot.Status == HistoryLoadStatus.DatabaseMissing)
        {
            HistoryGrid.ItemsSource = null;
            BatchSummary.Text = T("History.NoLog");
            return;
        }
        if (snapshot.Status == HistoryLoadStatus.Failed)
        {
            BatchSummary.Text = T("History.ReadError", snapshot.Diagnostic ?? T("Common.Unknown"));
            HistoryGrid.ItemsSource = null;
            return;
        }

        HistoryGrid.ItemsSource = snapshot.Rows;
        if (snapshot.BatchId is not null && snapshot.BatchCounts is { } counts)
        {
            if (counts.Total > 0)
            {
                BatchSummary.Text = T("History.BatchSummary", snapshot.BatchId,
                    counts.Pass, counts.Fail, counts.PassRate);
            }
            else
            {
                BatchSummary.Text = T("History.BatchEmpty", snapshot.BatchId);
            }
        }
        else
        {
            BatchSummary.Text = snapshot.BatchesEnabled
                ? T("History.RecentNeedBatch", snapshot.Rows.Count)
                : T("History.RecentNoBatches", snapshot.Rows.Count);
        }
    }

    // ============================================================
    // Settings tab
    // ============================================================

    private void ApplySettingsToUI()
    {
        _isApplyingSettings = true;
        try
        {
        SettingsCatalogPath.Text   = _settings.CatalogPath ?? "";
        SettingsRequireSigned.IsChecked = _settings.RequireSignedCatalog;
        SettingsRequireSigned.IsEnabled = CatalogTrust.IsUnsignedLabModeEnabled();
        SettingsRequireSigned.ToolTip = SettingsRequireSigned.IsEnabled
            ? T("Settings.LabAllowed")
            : T("Settings.SignatureMandatory");
        SettingsCatalogAutoUpdate.IsChecked = _settings.CatalogAutoUpdate;
        // Sprint 6: catalog source is hard-locked. Display the official source
        // read-only — AppSettings.Load already clamped any settings.json values
        // back to CatalogTrust.OfficialCatalogSource on construction.
        var src = CatalogTrust.OfficialCatalogSource;
        SettingsCatalogSourceLocked.Text = $"{src.Owner}/{src.Repo}";
        SettingsGdbPath.Text       = _settings.GdbPath ?? "";
        SettingsBmpFreq.Text       = _settings.BmpFrequencyHz.ToString(CultureInfo.InvariantCulture);
        SettingsPowerExternal.IsChecked = _settings.Power == PowerMode.External;
        SettingsPowerProbe.IsChecked    = _settings.Power == PowerMode.Probe;
        SettingsConnectReset.IsChecked  = _settings.ConnectUnderReset;
        SettingsTimeout.Text       = _settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        SettingsDbPath.Text        = _settings.DbPath ?? "";
        SettingsStationId.Text     = _settings.StationId;
        SettingsBatchesEnabled.IsChecked = _settings.BatchesEnabled;
        SelectLanguageComboItem(_settings.LanguageCode);
        SelectHotkeyComboItem(_settings.FlashHotkey);
        RefreshFlashHotkeyHint();

        // Sprint 5: log shipper settings.
        SettingsLogShippingEnabled.IsChecked = _settings.LogShippingEnabled;
        SettingsLogShipInterval.Text         = _settings.LogShipIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        SettingsLogShipperKey.Text           = _settings.LogShipperPrivateKeyPath;
        SettingsLogsSourceLocked.Text        = $"{GitHubAppConfig.LogsRepoOwner}/{GitHubAppConfig.LogsRepoName} {T("Settings.ReadOnlySuffix")}";
        AppUpdateSourceLocked.Text           = $"{GitHubAppConfig.AppUpdatesRepoOwner}/{GitHubAppConfig.AppUpdatesRepoName}";
        ApplyBatchModeToUI();
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void ApplyBatchModeToUI()
    {
        var enabled = _settings.BatchesEnabled;
        BatchLabel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        BatchBox.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        BatchBox.IsEnabled = enabled;
        Grid.SetColumnSpan(OperatorBox, enabled ? 1 : 4);
        BatchLockLabel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        ExportCsvBatchButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(BatchBox.Text)
                && !string.IsNullOrWhiteSpace(_settings.LastBatch))
                BatchBox.Text = _settings.LastBatch;
            RefreshBatchLockStatus();
        }
        else
        {
            BatchBox.Text = "";
            BatchLockLabel.Text = "";
        }
    }

    private void SelectHotkeyComboItem(FlashHotkey hk)
    {
        foreach (var obj in SettingsFlashHotkey.Items)
        {
            if (obj is ComboBoxItem item && item.Tag is string tag
                && string.Equals(tag, hk.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                SettingsFlashHotkey.SelectedItem = item;
                return;
            }
        }
        SettingsFlashHotkey.SelectedIndex = 0;
    }

    private void SelectLanguageComboItem(string? languageCode)
    {
        var normalized = IskraLanguages.NormalizeOrDefault(languageCode);
        foreach (var obj in SettingsLanguage.Items)
        {
            if (obj is ComboBoxItem item && item.Tag is string tag
                && string.Equals(tag, normalized, StringComparison.OrdinalIgnoreCase))
            {
                SettingsLanguage.SelectedItem = item;
                return;
            }
        }
        SettingsLanguage.SelectedIndex = 0;
    }

    private string ReadLanguageComboSelection()
    {
        if (SettingsLanguage.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return IskraLanguages.NormalizeOrDefault(tag);
        return IskraLanguages.Ukrainian;
    }

    private FlashHotkey ReadHotkeyComboSelection()
    {
        if (SettingsFlashHotkey.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && Enum.TryParse<FlashHotkey>(tag, ignoreCase: true, out var hk))
            return hk;
        return FlashHotkey.None;
    }

    /// <summary>
    /// Refreshes the small subtitle under the giant FLASH button and its tooltip
    /// so the operator can see at a glance which key fires the flash.
    /// </summary>
    private void RefreshFlashHotkeyHint()
    {
        var (label, _) = HotkeyDisplay(_settings.FlashHotkey);
        if (_settings.FlashHotkey == FlashHotkey.None)
        {
            FlashHotkeyHint.Text = "";
            FlashButtonTooltipText.Text = T("Flash.Tooltip");
        }
        else
        {
            FlashHotkeyHint.Text = T("Flash.HotkeyHint", label);
            FlashButtonTooltipText.Text = T("Flash.HotkeyTooltip", label);
        }
    }

    private static (string Label, Key Key) HotkeyDisplay(FlashHotkey hk) => hk switch
    {
        FlashHotkey.Space => (T("Hotkey.Space"), Key.Space),
        FlashHotkey.Enter => ("Enter",  Key.Enter),
        FlashHotkey.F2    => ("F2",     Key.F2),
        FlashHotkey.F5    => ("F5",     Key.F5),
        _                 => ("",       Key.None),
    };

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_settings.FlashHotkey == FlashHotkey.None) return;
        var (_, key) = HotkeyDisplay(_settings.FlashHotkey);
        if (e.Key != key) return;

        // Only fire when the Flash tab is the active one — otherwise the
        // operator could land on a TextBox in another tab and unintentionally
        // trigger a flash on Enter.
        if (!ReferenceEquals(MainTabs.SelectedItem, FlashTab)) return;

        // Don't fire when the button is disabled (mid-flash) — and don't double-fire
        // for keys repeated while held down.
        if (!FlashButton.IsEnabled || e.IsRepeat) return;

        // Space inside a TextBox should still type a space, not flash. Enter and
        // the F-keys are safe to capture even with focus on a single-line TextBox.
        if (_settings.FlashHotkey == FlashHotkey.Space
            && Keyboard.FocusedElement is TextBox)
            return;

        e.Handled = true;
        FlashButton_Click(FlashButton, new RoutedEventArgs());
    }

    private void SettingsTextChanged(object sender, TextChangedEventArgs e) => MarkSettingsDirty();

    private void SettingsControlChanged(object sender, RoutedEventArgs e) => MarkSettingsDirty();

    private void SettingsSelectionChanged(object sender, SelectionChangedEventArgs e) => MarkSettingsDirty();

    private void MarkSettingsDirty()
    {
        if (!_isLoaded || _isApplyingSettings)
            return;

        SettingsStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x62, 0x00));
        SettingsStatus.Text = T("Settings.Dirty.Detail");
        StatusSave.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x80));
        StatusSave.Text = T("Settings.Dirty.Short");
    }


    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isLoaded || _suppressTabSelectionChanged || !ReferenceEquals(e.OriginalSource, MainTabs))
            return;

        var selected = MainTabs.SelectedItem as TabItem;
        if (ReferenceEquals(_previousMainTab, SettingsTab)
            && !ReferenceEquals(selected, SettingsTab)
            && !TrySaveSettings(automatic: true, showDialogOnError: true))
        {
            _suppressTabSelectionChanged = true;
            try
            {
                MainTabs.SelectedItem = SettingsTab;
                _previousMainTab = SettingsTab;
            }
            finally
            {
                _suppressTabSelectionChanged = false;
            }
            return;
        }

        _previousMainTab = selected;
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isLoaded)
            return;

        if (_flashInProgress)
        {
            e.Cancel = true;
            MessageBox.Show(
                this,
                T("Settings.CloseDuringFlash"),
                T("Settings.FlashRunning.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!TrySaveSettings(automatic: true, showDialogOnError: true))
        {
            e.Cancel = true;
            _suppressTabSelectionChanged = true;
            try
            {
                MainTabs.SelectedItem = SettingsTab;
                _previousMainTab = SettingsTab;
            }
            finally
            {
                _suppressTabSelectionChanged = false;
            }
        }
    }

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        TrySaveSettings(automatic: false, showDialogOnError: false);
    }

    private bool TrySaveSettings(bool automatic, bool showDialogOnError)
    {
        try
        {
            var draft = new SettingsDraft(
                LanguageCode: ReadLanguageComboSelection(),
                CatalogPath: SettingsCatalogPath.Text,
                RequireSignedCatalog: SettingsRequireSigned.IsChecked == true,
                CatalogAutoUpdate: SettingsCatalogAutoUpdate.IsChecked == true,
                GdbPath: SettingsGdbPath.Text,
                BmpFrequencyHz: SettingsBmpFreq.Text,
                Power: SettingsPowerProbe.IsChecked == true
                    ? PowerMode.Probe
                    : PowerMode.External,
                ConnectUnderReset: SettingsConnectReset.IsChecked == true,
                TimeoutSeconds: SettingsTimeout.Text,
                DbPath: SettingsDbPath.Text,
                StationId: SettingsStationId.Text,
                BatchesEnabled: SettingsBatchesEnabled.IsChecked == true,
                LastOperator: OperatorBox.Text,
                LastBatch: BatchBox.Text,
                FlashHotkey: ReadHotkeyComboSelection(),
                LogShippingEnabled: SettingsLogShippingEnabled.IsChecked == true,
                LogShipIntervalMinutes: SettingsLogShipInterval.Text,
                LogShipperPrivateKeyPath: SettingsLogShipperKey.Text);
            var save = _settingsWorkflow.Save(_settings, draft);
            if (!save.IsSaved)
            {
                var message = save.InvalidField switch
                {
                    SettingsField.BmpFrequencyHz => T("Settings.FrequencyInvalid"),
                    SettingsField.TimeoutSeconds => T("Settings.TimeoutInvalid"),
                    SettingsField.LogShipIntervalMinutes => T("Settings.IntervalInvalid"),
                    _ => save.Diagnostic ?? T("Common.Unknown"),
                };
                throw new InvalidOperationException(message);
            }
            var candidate = save.Settings!;
            _settings = candidate;
            ApplyBatchModeToUI();
            RefreshFlashHotkeyHint();

            // Re-discover with new settings in case catalog/gdb paths changed.
            DiscoverGdb();
            LoadCatalog();
            RefreshHistory();
            RefreshCloudSyncStatus();
            RefreshFlashReadiness(updateBanner: true);

            SettingsStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
            SettingsStatus.Text = automatic
                ? T("Settings.AutoSaved", DateTime.Now)
                : T("Settings.Saved", DateTime.Now);
            StatusSave.Foreground = new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0x9C));
            StatusSave.Text = automatic ? T("Settings.AutoSaved.Short") : T("Settings.Saved.Short");
            if (!string.Equals(candidate.LanguageCode, _activeLanguageCode, StringComparison.Ordinal))
            {
                SettingsStatus.Text = T("Settings.RestartNotice");
                StatusSave.Text = T("Settings.RestartNotice.Short");
            }
            return true;
        }
        catch (Exception ex)
        {
            SettingsStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            SettingsStatus.Text = T("Settings.NotSaved.Detail", ex.Message);
            StatusSave.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0xB3));
            StatusSave.Text = T("Settings.NotSaved.Short");
            if (showDialogOnError)
            {
                MessageBox.Show(
                    this,
                    T("Settings.SaveError.Body", ex.Message),
                    T("Settings.SaveError.Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return false;
        }
    }

    private void SettingsReset_Click(object sender, RoutedEventArgs e)
    {
        _settings = _settingsWorkflow.Defaults();
        ApplySettingsToUI();
        SettingsStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        SettingsStatus.Text = T("Settings.ResetNotice");
        StatusSave.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x80));
        StatusSave.Text = T("Settings.Dirty.Short");
    }

    private void PickCatalogPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = T("Dialog.Filter.Catalog"),
            Title  = T("Dialog.Catalog.Title"),
        };
        if (dlg.ShowDialog() == true) SettingsCatalogPath.Text = dlg.FileName;
    }

    private void PickGdbPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = T("Dialog.Filter.Gdb"),
            Title  = T("Dialog.Gdb.Title"),
        };
        if (dlg.ShowDialog() == true) SettingsGdbPath.Text = dlg.FileName;
    }

    private void PickDbPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = T("Dialog.Filter.Sqlite"),
            Title  = T("Dialog.Log.Title"),
            FileName = "flash_log.db",
        };
        if (dlg.ShowDialog() == true) SettingsDbPath.Text = dlg.FileName;
    }

    private void PickLogKeyPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = T("Dialog.Filter.Pem"),
            Title  = T("Dialog.Pem.Title"),
        };
        if (dlg.ShowDialog() == true) SettingsLogShipperKey.Text = dlg.FileName;
    }

    /// <summary>
    /// Refreshes the cloud-sync status strip widget and the Settings-tab label
    /// to reflect: (a) whether the log shipper is configured at all, (b) how
    /// many rows are pending upload. Cheap — just opens the SQLite to count.
    /// </summary>
    private void RefreshCloudSyncStatus()
    {
        if (!_settings.LogShippingEnabled)
        {
            StatusCloud.Text = T("Cloud.Disabled.Status");
            LogShipStatus.Text = T("Cloud.Disabled.Detail");
            return;
        }
        if (!GitHubAppConfig.IsLogShipperConfigured)
        {
            StatusCloud.Text = T("Cloud.Unconfigured.Status");
            LogShipStatus.Text = T("Cloud.Unconfigured.Detail");
            return;
        }
        try
        {
            var dbPath = ResolveDbPath();
            if (!File.Exists(dbPath))
            {
                StatusCloud.Text = T("Cloud.Empty.Status");
                LogShipStatus.Text = T("Cloud.Empty.Detail");
                return;
            }
            using var store = new SqliteLogStore(dbPath);
            var pending = store.CountUnsynced();
            StatusCloud.Text = pending == 0 ? T("Cloud.Synced.Status") : T("Cloud.Queued.Status", pending);
            LogShipStatus.Text = pending == 0
                ? T("Cloud.UploadedAll")
                : T("Cloud.RowsWaiting", pending);
        }
        catch (Exception ex)
        {
            StatusCloud.Text = T("Cloud.Error.Status");
            LogShipStatus.Text = $"✗ {ex.Message}";
        }
    }

    private async void LogShipNow_Click(object sender, RoutedEventArgs e)
    {
        if (!_settings.LogShippingEnabled)
        {
            LogShipStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
            LogShipStatus.Text = T("Cloud.EnableFirst");
            return;
        }
        if (!GitHubAppConfig.IsLogShipperConfigured)
        {
            LogShipStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            LogShipStatus.Text = T("Cloud.Unconfigured.Detail");
            return;
        }

        var keyPath = string.IsNullOrWhiteSpace(SettingsLogShipperKey.Text)
            ? _settings.LogShipperPrivateKeyPath
            : SettingsLogShipperKey.Text.Trim();
        if (!File.Exists(keyPath))
        {
            LogShipStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            LogShipStatus.Text = T("Cloud.KeyMissing", keyPath);
            return;
        }

        LogShipNowButton.IsEnabled = false;
        LogShipStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        LogShipStatus.Text = T("Cloud.Uploading");
        try
        {
            ShipReport report;
            using (var store = new SqliteLogStore(ResolveDbPath()))
            using (var http  = new System.Net.Http.HttpClient())
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
            LogShipStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
            LogShipStatus.Text = T("Cloud.UploadReport", report.RowsPushed,
                report.FilesCreated, report.FilesUpdated,
                report.RowsLeftover > 0 ? T("Cloud.Leftover", report.RowsLeftover) : ".");
        }
        catch (Exception ex)
        {
            LogShipStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            LogShipStatus.Text = $"✗ {ex.Message}";
        }
        finally
        {
            LogShipNowButton.IsEnabled = true;
            RefreshCloudSyncStatus();
        }
    }

    private static void Beep() => System.Media.SystemSounds.Beep.Play();

    // ============================================================
    // GitHub auth
    // ============================================================

    private static readonly TimeSpan AuthRefreshSkew = TimeSpan.FromMinutes(5);

    private void RefreshAuthStatus()
    {
        var store = new TokenStore();
        StoredTokens? stored;
        try { stored = store.Load(); }
        catch (TokenStoreException ex)
        {
            AuthStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            AuthStatusLabel.Text = T("Auth.TokenCorrupt", ex.Message);
            AuthLoginButton.IsEnabled  = GitHubAppConfig.IsConfigured;
            AuthLogoutButton.IsEnabled = true;
            RefreshAuthBanner(CurrentSelectedRelease());
            return;
        }

        if (!GitHubAppConfig.IsConfigured)
        {
            AuthStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            AuthStatusLabel.Text = T("Auth.ClientMissing.Short");
            AuthLoginButton.IsEnabled = false;
            AuthLogoutButton.IsEnabled = stored is not null;
            RefreshAuthBanner(CurrentSelectedRelease());
            return;
        }

        if (stored is null)
        {
            AuthStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x66, 0x00));
            AuthStatusLabel.Text = T("Auth.NotSignedIn");
            AuthLoginButton.IsEnabled = true;
            AuthLogoutButton.IsEnabled = false;
            RefreshAuthBanner(CurrentSelectedRelease());
            return;
        }

        var now = DateTime.UtcNow;
        if (stored.RefreshTokenIsExpired(now))
        {
            AuthStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            AuthStatusLabel.Text = T("Auth.SessionExpired");
            AuthLoginButton.IsEnabled = true;
            AuthLogoutButton.IsEnabled = true;
            RefreshAuthBanner(CurrentSelectedRelease());
            return;
        }

        var freshAccess = stored.AccessTokenIsFresh(now, AuthRefreshSkew);
        AuthStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
        AuthStatusLabel.Text = T("Auth.SignedIn",
            freshAccess ? T("Auth.Access.Valid") : T("Auth.Access.Refresh"),
            stored.AccessTokenExpiresAtUtc, stored.RefreshTokenExpiresAtUtc);
        AuthLoginButton.IsEnabled = true;
        AuthLogoutButton.IsEnabled = true;
        RefreshAuthBanner(CurrentSelectedRelease());
    }

    private FirmwareRelease? CurrentSelectedRelease()
    {
        if (VersionCombo.SelectedItem is FirmwareRelease r) return r;
        if (_catalog is null || ProductCombo.SelectedItem is not string id) return null;
        return _catalog.FindProduct(id)?.Default();
    }

    private void RefreshAuthBanner(FirmwareRelease? release)
    {
        if (release is null || !release.IsRemote)
        {
            AuthHintBanner.Visibility = Visibility.Collapsed;
            return;
        }

        var store = new TokenStore();
        StoredTokens? stored;
        try { stored = store.Load(); } catch (TokenStoreException) { stored = null; }

        bool ok = stored is not null
                  && !stored.RefreshTokenIsExpired(DateTime.UtcNow)
                  && GitHubAppConfig.IsConfigured;

        if (ok)
        {
            AuthHintBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            AuthHintText.Text = !GitHubAppConfig.IsConfigured
                ? T("Auth.ProductClientMissing")
                : (stored is null
                    ? T("Auth.ProductNeedsSignIn", release.Version)
                    : T("Auth.ProductSessionExpired"));
            AuthHintLoginButton.IsEnabled = GitHubAppConfig.IsConfigured;
            AuthHintBanner.Visibility = Visibility.Visible;
        }
    }

    private async void AuthLogin_Click(object sender, RoutedEventArgs e) => await DoLoginAsync();

    private async void AuthHintLogin_Click(object sender, RoutedEventArgs e) => await DoLoginAsync();

    private void AuthLogout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            new TokenStore().Delete();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, T("Auth.DeleteFailed", ex.Message),
                T("Auth.SignOut.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshAuthStatus();
    }

    private async void AuthRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshAuthStatus();
        if (!GitHubAppConfig.IsConfigured) return;

        var store = new TokenStore();
        StoredTokens? stored;
        try { stored = store.Load(); } catch { stored = null; }
        if (stored is null) return;

        AuthRefreshButton.IsEnabled = false;
        try
        {
            using var http = new HttpClient();
            var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);
            var provider = new AccessTokenProvider(store, flow);
            await provider.GetFreshAccessTokenAsync();
            RefreshAuthStatus();
        }
        catch (RefreshTokenExpiredException)
        {
            RefreshAuthStatus(); // store was deleted by provider
        }
        catch (Exception ex)
        {
            AuthStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            AuthStatusLabel.Text = T("Auth.RefreshFailed", ex.Message);
        }
        finally
        {
            AuthRefreshButton.IsEnabled = true;
        }
    }

    private async Task DoLoginAsync()
    {
        if (!GitHubAppConfig.IsConfigured)
        {
            MessageBox.Show(this,
                T("Auth.ClientMissing"),
                T("Auth.SignIn.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        AuthLoginButton.IsEnabled = false;
        AuthHintLoginButton.IsEnabled = false;
        try
        {
            using var http = new HttpClient();
            var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);
            DeviceCodeResponse code;
            try { code = await flow.RequestDeviceCodeAsync(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, T("Auth.DeviceCodeFailed", ex.Message),
                    T("Auth.SignIn.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dlg = new DeviceFlowDialog(flow, code) { Owner = this };
            var ok = dlg.ShowDialog();

            if (ok != true)
            {
                if (!string.IsNullOrEmpty(dlg.ErrorMessage))
                    MessageBox.Show(this, dlg.ErrorMessage, T("Auth.SignIn.Title"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                new TokenStore().Save(StoredTokens.From(dlg.Token!, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, T("Auth.SaveFailed", ex.Message),
                    T("Auth.SignIn.Title"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        finally
        {
            RefreshAuthStatus();
        }
    }

    // ============================================================
    // App update check
    // ============================================================

    private static string CurrentAppVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(MainWindow).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            info = asm.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(info))
            return "0.0.0";

        var plus = info.IndexOf('+');
        return (plus >= 0 ? info[..plus] : info).Trim();
    }

    private void RefreshAppUpdateStatus()
    {
        _lastAppUpdateUrl = null;
        AppUpdateCurrentVersion.Text = CurrentAppVersion();
        AppUpdateSourceLocked.Text = $"{GitHubAppConfig.AppUpdatesRepoOwner}/{GitHubAppConfig.AppUpdatesRepoName}";
        AppUpdateOpenButton.IsEnabled = false;
        AppUpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        AppUpdateStatusLabel.Text = T("Update.Prompt");
    }

    private async void AppUpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        AppUpdateCheckButton.IsEnabled = false;
        AppUpdateOpenButton.IsEnabled = false;
        _lastAppUpdateUrl = null;
        AppUpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        AppUpdateStatusLabel.Text = T("Update.Checking");

        try
        {
            using var http = new HttpClient();
            var client = new AppUpdateClient(
                http,
                GitHubAppConfig.AppUpdatesRepoOwner,
                GitHubAppConfig.AppUpdatesRepoName);
            var result = await client.CheckLatestAsync(CurrentAppVersion());

            _lastAppUpdateUrl = result.ReleaseUrl ?? result.SetupDownloadUrl ?? result.MsiDownloadUrl;
            AppUpdateOpenButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastAppUpdateUrl);

            switch (result.Status)
            {
                case AppUpdateStatus.UpdateAvailable:
                    AppUpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x66, 0x00));
                    AppUpdateStatusLabel.Text = result.SetupDownloadUrl is not null
                        ? T("Update.Available.Setup", result.LatestVersion, result.TagName)
                        : T("Update.Available.Download", result.LatestVersion, result.TagName);
                    break;
                case AppUpdateStatus.UpToDate:
                    AppUpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
                    AppUpdateStatusLabel.Text = T("Update.Current", result.CurrentVersion);
                    break;
                case AppUpdateStatus.NoRelease:
                    AppUpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x66, 0x00));
                    AppUpdateStatusLabel.Text = T("Update.NoRelease");
                    break;
                case AppUpdateStatus.NetworkError:
                    AppUpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    AppUpdateStatusLabel.Text = T("Update.NetworkError", result.Message);
                    break;
                case AppUpdateStatus.ParseError:
                    AppUpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    AppUpdateStatusLabel.Text = T("Update.ParseError", result.Message);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppUpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            AppUpdateStatusLabel.Text = $"✗ {ex.Message}";
        }
        finally
        {
            AppUpdateCheckButton.IsEnabled = true;
        }
    }

    private void AppUpdateOpen_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastAppUpdateUrl))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(_lastAppUpdateUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppUpdateStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            AppUpdateStatusLabel.Text = T("Browser.OpenFailed", ex.Message);
        }
    }

    // ============================================================
    // Remote catalog auto-update (Sprint 3.5)
    // ============================================================

    private RemoteCatalogClient NewRemoteCatalogClient(HttpClient http) => new(
        http,
        owner: string.IsNullOrWhiteSpace(_settings.CatalogOwner) ? "oleksandrmaslov" : _settings.CatalogOwner,
        repo:  string.IsNullOrWhiteSpace(_settings.CatalogRepo)  ? "iskra-catalog"   : _settings.CatalogRepo);

    private void RefreshCatalogCacheStatus()
    {
        using var http = new HttpClient(); // never sent — just to construct the client
        var client = NewRemoteCatalogClient(http);
        var tag = client.CachedTag();
        var cached = client.LoadCached();
        if (tag is null && cached is null)
        {
            CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x66, 0x00));
            CatalogCacheStatus.Text = T("CatalogCache.Empty");
        }
        else if (cached is null)
        {
            CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            CatalogCacheStatus.Text = T("CatalogCache.Bad", tag);
        }
        else
        {
            CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
            CatalogCacheStatus.Text = T("CatalogCache.Ready", tag, cached.Products.Count, cached.GeneratedAt);
        }
    }

    private async Task BackgroundFetchCatalogAsync()
    {
        try
        {
            using var http = new HttpClient();
            var client = NewRemoteCatalogClient(http);
            var r = await client.FetchAsync().ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                RefreshCatalogCacheStatus();
                if (r.Status == RemoteCatalogStatus.Updated && r.ChangedFromCached)
                {
                    StatusCatalog.Text = T("CatalogCache.BackgroundUpdated", r.TagName);
                }
            });
        }
        catch
        {
            // Background fetch failures must never crash the UI. Status is
            // shown via the explicit refresh button.
        }
    }

    private async void CatalogUpdate_Click(object sender, RoutedEventArgs e)
    {
        CatalogUpdateButton.IsEnabled = false;
        CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        CatalogCacheStatus.Text = T("CatalogCache.Checking");

        // Sprint 6: source is hard-locked; no user-editable owner/repo to read.

        try
        {
            using var http = new HttpClient();
            var client = NewRemoteCatalogClient(http);
            var r = await client.FetchAsync();

            switch (r.Status)
            {
                case RemoteCatalogStatus.Updated:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
                    CatalogCacheStatus.Text = r.ChangedFromCached
                        ? T("CatalogCache.Updated", r.TagName)
                        : T("CatalogCache.Unchanged", r.TagName);
                    break;
                case RemoteCatalogStatus.AlreadyUpToDate:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
                    CatalogCacheStatus.Text = T("CatalogCache.Current", r.TagName);
                    break;
                case RemoteCatalogStatus.NoRelease:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x66, 0x00));
                    CatalogCacheStatus.Text = T("CatalogCache.NoRelease", _settings.CatalogOwner, _settings.CatalogRepo);
                    break;
                case RemoteCatalogStatus.BadSignature:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    CatalogCacheStatus.Text = T("CatalogCache.BadSignature");
                    break;
                case RemoteCatalogStatus.AssetsMissing:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    CatalogCacheStatus.Text = T("CatalogCache.AssetsMissing", r.Message);
                    break;
                case RemoteCatalogStatus.ParseError:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    CatalogCacheStatus.Text = T("CatalogCache.ParseError", r.Message);
                    break;
                default:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    CatalogCacheStatus.Text = T("CatalogCache.Error", r.Status, r.Message);
                    break;
            }
        }
        catch (Exception ex)
        {
            CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            CatalogCacheStatus.Text = $"✗ {ex.Message}";
        }
        finally
        {
            CatalogUpdateButton.IsEnabled = true;
        }
    }
}
