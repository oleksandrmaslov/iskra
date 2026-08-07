using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Iskra.Application;
using Iskra.Application.Localization;
using Iskra.Core;

namespace Iskra.Desktop;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    private static readonly IBrush ReadyBrush = new SolidColorBrush(Color.Parse("#2E8B57"));
    private static readonly IBrush AttentionBrush = new SolidColorBrush(Color.Parse("#D97706"));

    // Same banner palette as the shipping WPF station so an operator moving
    // between the two frontends reads the identical PASS/FAIL signal.
    private static readonly IBrush BannerPassBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
    private static readonly IBrush BannerFailBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly IBrush BannerWarnBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xC1, 0x4E));
    private static readonly IBrush BannerIdleBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
    private static readonly IBrush BannerLightText = Brushes.White;
    private static readonly IBrush BannerDarkTitle = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly IBrush BannerDarkDetail = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

    // The verdict is meant to be readable across the bench; guidance prompts are
    // full sentences and read better one step down.
    private const double VerdictTitleSize = 56;
    private const double NeutralTitleSize = 34;

    private AppSettings _settings;
    private readonly StationReadinessService _readinessService;
    private readonly HistoryWorkflow _historyWorkflow;
    private readonly SettingsWorkflow _settingsWorkflow;
    private readonly FlashWorkflow _flashWorkflow;
    private readonly StringBuilder _gdbLog = new();

    private DesktopText _text = DesktopLocalization.For(DesktopLocalization.DefaultLanguageCode);
    private LanguageOption _selectedLanguage = DesktopLocalization.Languages[0];
    private string _languageSaveStatus = string.Empty;
    private string _readinessSummary = string.Empty;
    private string _readinessDetail = string.Empty;
    private IBrush _readinessBrush = AttentionBrush;
    private string _lastCheckedText = string.Empty;
    private string _probeStatusText = string.Empty;
    private string _probeDetailText = string.Empty;
    private string _gdbStatusText = string.Empty;
    private string _gdbDetailText = string.Empty;
    private string _catalogStatusText = string.Empty;
    private string _catalogDetailText = string.Empty;
    private string _catalogOverviewText = string.Empty;
    private string _historyStatusText = string.Empty;
    private string _historySummaryText = string.Empty;

    // Flash-tab state.
    private Catalog? _catalog;
    private string? _catalogDirectory;
    private string? _gdbPath;
    private string? _port;
    private string? _probeSerial;
    private bool _suppressSelectionReload;
    private string _operatorName = string.Empty;
    private string _batchId = string.Empty;
    private ProductOption? _selectedProduct;
    private ReleaseOption? _selectedRelease;
    private bool _isFlashing;
    private bool _canFlash;
    private string _bannerTitle = string.Empty;
    private string _bannerDetail = string.Empty;
    private IBrush _bannerBackground = BannerIdleBrush;
    private IBrush _bannerTitleBrush = BannerDarkTitle;
    private IBrush _bannerDetailBrush = BannerDarkDetail;
    private double _bannerTitleSize = NeutralTitleSize;
    private string _gdbLogText = string.Empty;
    private string _batchLockText = string.Empty;
    private bool _showAuthHint;

    public MainWindowViewModel()
        : this(
            new SettingsWorkflow(),
            new HistoryWorkflow(),
            new StationReadinessService(new CatalogSession()),
            new FlashWorkflow(new DesktopRemoteFirmwareProvider()))
    {
    }

    public MainWindowViewModel(
        SettingsWorkflow settingsWorkflow,
        HistoryWorkflow historyWorkflow,
        StationReadinessService readinessService,
        FlashWorkflow flashWorkflow)
    {
        _settingsWorkflow = settingsWorkflow ?? throw new ArgumentNullException(nameof(settingsWorkflow));
        _historyWorkflow = historyWorkflow ?? throw new ArgumentNullException(nameof(historyWorkflow));
        _readinessService = readinessService ?? throw new ArgumentNullException(nameof(readinessService));
        _flashWorkflow = flashWorkflow ?? throw new ArgumentNullException(nameof(flashWorkflow));
        _settings = _settingsWorkflow.Load();
        _operatorName = _settings.LastOperator ?? string.Empty;
        _batchId = _settings.BatchesEnabled ? _settings.LastBatch ?? string.Empty : string.Empty;
        RefreshReadinessCommand = new RelayCommand(RefreshReadiness, () => !_isFlashing);
        FlashCommand = new AsyncRelayCommand(FlashAsync, () => _canFlash && !_isFlashing);
        // Commands must exist before ApplyLanguage: it rebuilds the hotkey list
        // and re-renders the auth/cloud status, all of which touch them.
        InitializeSettingsSurface();
        InitializeMaintenanceSurface();
        ApplyLanguage(_settings.LanguageCode, persist: false);
        RefreshReadiness();
    }

    /// <summary>
    /// Set by the window once it exists. Commands that need a picker or a modal
    /// degrade to an inline status message rather than throwing when unset.
    /// </summary>
    public IDesktopDialogs? Dialogs { get; set; }

    public RelayCommand RefreshReadinessCommand { get; }
    public AsyncRelayCommand FlashCommand { get; }
    public ObservableCollection<ProductSummaryViewModel> Products { get; } = [];
    public ObservableCollection<ProductOption> ProductOptions { get; } = [];
    public ObservableCollection<ReleaseOption> ReleaseOptions { get; } = [];
    public ObservableCollection<HistoryRowViewModel> HistoryRows { get; } = [];
    public IReadOnlyList<LanguageOption> LanguageOptions => DesktopLocalization.Languages;

    public DesktopText Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || !SetProperty(ref _selectedLanguage, value))
            {
                return;
            }

            ApplyLanguage(value.Code, persist: true);
            RefreshReadiness();
        }
    }

    public string LanguageSaveStatus
    {
        get => _languageSaveStatus;
        private set => SetProperty(ref _languageSaveStatus, value);
    }

    public string PlatformLabel => $"{RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.ProcessArchitecture}";
    public string WindowTitle => $"{Text.WindowTitle} — Avalonia";
    public string PreviewBadge => Text.LanguageCode switch
    {
        IskraLanguages.English => "AVALONIA · HIL ACCEPTANCE PENDING",
        IskraLanguages.German => "AVALONIA · HIL-ABNAHME AUSSTEHEND",
        _ => "AVALONIA · ОЧІКУЄ HIL-ПРИЙМАННЯ",
    };
    public string HistoryAlphaNotice => Text.HistoryMigration;
    public string SettingsPath => AppSettingsStore.DefaultPath;
    public string StationId => string.IsNullOrWhiteSpace(_settings.StationId) ? Environment.MachineName : _settings.StationId;
    public string CatalogSource => $"{CatalogTrust.OfficialCatalogSource.Owner}/{CatalogTrust.OfficialCatalogSource.Repo}";
    public string LogKeyPath => _settings.LogShipperPrivateKeyPath;
    public string LogShippingStatusText => _settings.LogShippingEnabled ? Text.LogShippingEnabled : Text.LogShippingDisabled;
    public bool BatchesEnabled => BatchPolicy.Resolve(_settings, null).BatchesEnabled;
    public string BatchModeStatusText => BatchesEnabled ? Text.BatchEnabled : Text.BatchDisabled;
    public string DatabasePath => ResolveDatabasePath();

    public string ReadinessSummary { get => _readinessSummary; private set => SetProperty(ref _readinessSummary, value); }
    public string ReadinessDetail { get => _readinessDetail; private set => SetProperty(ref _readinessDetail, value); }
    public IBrush ReadinessBrush { get => _readinessBrush; private set => SetProperty(ref _readinessBrush, value); }
    public string LastCheckedText { get => _lastCheckedText; private set => SetProperty(ref _lastCheckedText, value); }
    public string ProbeStatusText { get => _probeStatusText; private set => SetProperty(ref _probeStatusText, value); }
    public string ProbeDetailText { get => _probeDetailText; private set => SetProperty(ref _probeDetailText, value); }
    public string GdbStatusText { get => _gdbStatusText; private set => SetProperty(ref _gdbStatusText, value); }
    public string GdbDetailText { get => _gdbDetailText; private set => SetProperty(ref _gdbDetailText, value); }
    public string CatalogStatusText { get => _catalogStatusText; private set => SetProperty(ref _catalogStatusText, value); }
    public string CatalogDetailText { get => _catalogDetailText; private set => SetProperty(ref _catalogDetailText, value); }
    public string CatalogOverviewText { get => _catalogOverviewText; private set => SetProperty(ref _catalogOverviewText, value); }
    public string HistoryStatusText { get => _historyStatusText; private set => SetProperty(ref _historyStatusText, value); }
    public string HistorySummaryText { get => _historySummaryText; private set => SetProperty(ref _historySummaryText, value); }

    // ============================================================
    // Flash tab
    // ============================================================

    public string OperatorName
    {
        get => _operatorName;
        set
        {
            if (!SetProperty(ref _operatorName, value ?? string.Empty)) return;
            RefreshFlashReadiness(updateBanner: true);
        }
    }

    public string BatchId
    {
        get => _batchId;
        set
        {
            if (!SetProperty(ref _batchId, value ?? string.Empty)) return;
            RefreshBatchLockText();
            RefreshFlashReadiness(updateBanner: true);
        }
    }

    public ProductOption? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (!SetProperty(ref _selectedProduct, value)) return;
            if (_suppressSelectionReload) return;
            ReloadReleaseOptions(preferredVersion: null);
            RefreshFlashReadiness(updateBanner: true);
        }
    }

    public ReleaseOption? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (!SetProperty(ref _selectedRelease, value)) return;
            RefreshRemoteFirmwareHint();
            RefreshFlashReadiness(updateBanner: true);
        }
    }

    public bool IsFlashing
    {
        get => _isFlashing;
        private set
        {
            if (!SetProperty(ref _isFlashing, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            FlashCommand.RaiseCanExecuteChanged();
            RefreshReadinessCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsIdle => !_isFlashing;

    public bool CanFlash
    {
        get => _canFlash;
        private set
        {
            if (!SetProperty(ref _canFlash, value)) return;
            FlashCommand.RaiseCanExecuteChanged();
        }
    }

    public string BannerTitle { get => _bannerTitle; private set => SetProperty(ref _bannerTitle, value); }
    public string BannerDetail { get => _bannerDetail; private set => SetProperty(ref _bannerDetail, value); }
    public IBrush BannerBackground { get => _bannerBackground; private set => SetProperty(ref _bannerBackground, value); }
    public IBrush BannerTitleBrush { get => _bannerTitleBrush; private set => SetProperty(ref _bannerTitleBrush, value); }
    public IBrush BannerDetailBrush { get => _bannerDetailBrush; private set => SetProperty(ref _bannerDetailBrush, value); }
    public double BannerTitleSize { get => _bannerTitleSize; private set => SetProperty(ref _bannerTitleSize, value); }
    public string GdbLogText { get => _gdbLogText; private set => SetProperty(ref _gdbLogText, value); }
    public string BatchLockText { get => _batchLockText; private set => SetProperty(ref _batchLockText, value); }
    public bool HasBatchLockText => !string.IsNullOrEmpty(_batchLockText);

    public bool ShowAuthHint
    {
        get => _showAuthHint;
        private set => SetProperty(ref _showAuthHint, value);
    }

    public FlashHotkey FlashHotkey => _settings.FlashHotkey;

    public string HotkeyLabel => _settings.FlashHotkey switch
    {
        FlashHotkey.Space => Text.HotkeySpace,
        FlashHotkey.Enter => "Enter",
        FlashHotkey.F2 => "F2",
        FlashHotkey.F5 => "F5",
        _ => string.Empty,
    };

    public string HotkeyHintText => _settings.FlashHotkey == FlashHotkey.None
        ? string.Empty
        : Text.HotkeyHint(HotkeyLabel);

    public string FlashTooltipText => _settings.FlashHotkey == FlashHotkey.None
        ? Text.FlashAction
        : Text.HotkeyTooltip(HotkeyLabel);

    private async Task FlashAsync()
    {
        if (_isFlashing) return;

        // Re-run discovery immediately before the request snapshot, exactly as
        // WPF does, so a probe unplugged between readiness refresh and the
        // button press cannot be flashed against a stale port.
        RefreshReadiness();
        if (!CanFlash || _catalog is null || _selectedProduct is null || _selectedRelease is null)
        {
            return;
        }

        var productId = _selectedProduct.ProductId;
        var version = _selectedRelease.Version;
        var isRemote = _selectedRelease.IsRemote;

        IsFlashing = true;
        ClearGdbLog();
        try
        {
            var progress = new Progress<FlashWorkflowProgress>(update =>
            {
                if (update.Stage == FlashWorkflowStage.AcquiringFirmware && isRemote)
                    SetBannerNeutral(Text.FlashDownloading, warning: false);
                else if (update.Stage is FlashWorkflowStage.ValidatingFirmware or FlashWorkflowStage.Flashing)
                    SetBannerNeutral(Text.FlashRunning, warning: false);
            });

            var request = new FlashWorkflowRequest(
                Catalog: _catalog,
                CatalogDirectory: _catalogDirectory,
                ProductId: productId,
                FirmwareVersion: version,
                Settings: _settings.Clone(),
                Operator: _operatorName,
                EnteredBatchId: _batchId,
                GdbPath: _gdbPath,
                Port: _port,
                ProbeSerial: _probeSerial);

            var result = await _flashWorkflow.ExecuteAsync(
                request,
                progress,
                line => Dispatcher.UIThread.Post(() => AppendGdbLine(line.Text)));

            if (result.IsBlocked)
                ShowBlocked(result);
            else if (result.IsPass)
                ShowPass(result.Outcome.Duration);
            else
                ShowFail(result.Outcome.ErrorCode ?? "E_INTERNAL", result.Outcome.ErrorMessage);

            if (result.FirmwarePath is not null)
            {
                var remembered = _settingsWorkflow.RememberOperatorSelection(
                    _settings,
                    _operatorName,
                    result.EffectiveBatchId);
                if (remembered.IsSaved) _settings = remembered.Settings!;
            }

            RefreshHistory();
            RefreshBatchLockText();
            RefreshCloudStatus();
            if (result.Outcome.ErrorCode is "E_NOT_SIGNED_IN" or "E_AUTH_EXPIRED")
                RefreshAuthStatus();
        }
        catch (Exception ex)
        {
            ShowFail("E_INTERNAL", ex.Message);
        }
        finally
        {
            IsFlashing = false;
            RefreshFlashReadiness(updateBanner: false);
        }
    }

    private void ShowBlocked(FlashWorkflowResult result)
    {
        switch (result.Outcome.ErrorCode)
        {
            case "E_OPERATOR_REQUIRED":
                SetBannerNeutral(Text.ReadyOperator, warning: true, Text.ReadyOperatorDetail);
                break;
            case BatchPolicy.RequiredErrorCode:
                SetBannerNeutral(Text.ReadyBatch, warning: true, Text.ReadyBatchDetail);
                break;
            case "E_GDB_NOT_FOUND":
                SetBannerNeutral(Text.ReadyGdb, warning: true, Text.ReadyGdbDetail);
                break;
            case "E_PROBE_NOT_FOUND":
                SetBannerNeutral(Text.ReadyProbe, warning: true, Text.ReadyProbeDetail);
                break;
            default:
                ShowFail(result.Outcome.ErrorCode ?? "E_INTERNAL", result.Outcome.ErrorMessage);
                break;
        }
    }

    private void ShowPass(TimeSpan duration)
    {
        BannerBackground = BannerPassBrush;
        BannerTitleBrush = BannerLightText;
        BannerDetailBrush = BannerLightText;
        BannerTitleSize = VerdictTitleSize;
        BannerTitle = Text.FlashPass;
        BannerDetail = Text.FlashDuration(duration.TotalMilliseconds);
    }

    private void ShowFail(string code, string? detail)
    {
        BannerBackground = BannerFailBrush;
        BannerTitleBrush = BannerLightText;
        BannerDetailBrush = BannerLightText;
        BannerTitleSize = VerdictTitleSize;
        BannerTitle = $"✗ {code}";
        BannerDetail = OperatorText.ErrorHint(code, Text.LanguageCode);
        if (!string.IsNullOrEmpty(detail))
            AppendGdbLine($"{Environment.NewLine}[{Text.ErrorDetails}]{Environment.NewLine}{detail}");
    }

    private void SetBannerNeutral(string title, bool warning, string? detail = null)
    {
        BannerBackground = warning ? BannerWarnBrush : BannerIdleBrush;
        BannerTitleBrush = BannerDarkTitle;
        BannerDetailBrush = BannerDarkDetail;
        BannerTitleSize = NeutralTitleSize;
        BannerTitle = title;
        BannerDetail = detail ?? string.Empty;
    }

    private void ClearGdbLog()
    {
        _gdbLog.Clear();
        GdbLogText = string.Empty;
    }

    private void AppendGdbLine(string text)
    {
        _gdbLog.AppendLine(text);
        GdbLogText = _gdbLog.ToString();
    }

    /// <summary>
    /// Mirrors the WPF gate: gdb, exactly one probe, a trusted catalog, an
    /// operator, a batch when batch mode is on, and a resolved product/release.
    /// </summary>
    private void RefreshFlashReadiness(bool updateBanner)
    {
        var operatorMissing = string.IsNullOrWhiteSpace(_operatorName);
        var batchMissing = BatchesEnabled && string.IsNullOrWhiteSpace(_batchId);
        var selectionMissing = _selectedProduct is null || _selectedRelease is null;

        CanFlash = _gdbPath is not null
            && _port is not null
            && _catalog is not null
            && !operatorMissing
            && !batchMissing
            && !selectionMissing;

        if (!updateBanner || _isFlashing) return;

        if (_port is null)
            SetBannerNeutral(Text.ReadyProbe, warning: true, Text.ReadyProbeDetail);
        else if (_gdbPath is null)
            SetBannerNeutral(Text.ReadyGdb, warning: true, Text.ReadyGdbDetail);
        else if (_catalog is null)
            SetBannerNeutral(Text.ReadyCatalog, warning: true, Text.ReadyCatalogDetail);
        else if (operatorMissing)
            SetBannerNeutral(Text.ReadyOperator, warning: false, Text.ReadyOperatorDetail);
        else if (batchMissing)
            SetBannerNeutral(Text.ReadyBatch, warning: false, Text.ReadyBatchDetail);
        else if (selectionMissing)
            SetBannerNeutral(Text.ReadySelection, warning: false);
        else
            SetBannerNeutral(
                Text.FlashReady,
                warning: false,
                BatchesEnabled ? Text.FlashReadyBatchOn : Text.FlashReadyBatchOff);
    }

    private void RefreshBatchLockText()
    {
        var snapshot = _historyWorkflow.LookupBatchLock(_settings, _batchId);
        BatchLockText = snapshot.Status == BatchLockLookupStatus.Reserved && snapshot.Lock is { } locked
            ? Text.BatchLocked(locked.ProductId, locked.FirmwareVersion, ShortSha(locked.FirmwareSha256))
            : string.Empty;
        OnPropertyChanged(nameof(HasBatchLockText));
    }

    private static string ShortSha(string value) => value.Length <= 12 ? value : value[..12];

    // ============================================================
    // Readiness / catalog / history
    // ============================================================

    private void RefreshReadiness()
    {
        var previousProductId = _selectedProduct?.ProductId;
        var previousVersion = _selectedRelease?.Version;

        var snapshot = _readinessService.Evaluate(_settings);
        var issues = new List<string>();
        Products.Clear();

        if (snapshot.Probe.Status == ProbeReadinessStatus.Ready)
        {
            var probe = snapshot.Probe.Selected!;
            _port = probe.PortName;
            _probeSerial = probe.SerialNumber;
            ProbeStatusText = Text.Connected;
            ProbeDetailText = string.IsNullOrWhiteSpace(probe.SerialNumber)
                ? probe.PortName
                : Text.SerialNumber(probe.PortName, probe.SerialNumber);
        }
        else if (snapshot.Probe.Status == ProbeReadinessStatus.MultipleFound)
        {
            _port = null;
            _probeSerial = null;
            ProbeStatusText = Text.BlockedProbes(snapshot.Probe.Discovered.Count);
            ProbeDetailText = Text.LeaveOneBmp(string.Join(
                "; ", snapshot.Probe.Discovered.Select(p => string.IsNullOrWhiteSpace(p.SerialNumber)
                    ? p.PortName
                    : Text.PortWithSerial(p.PortName, p.SerialNumber))));
            issues.Add(Text.MultipleBmpIssue);
        }
        else
        {
            _port = null;
            _probeSerial = null;
            ProbeStatusText = snapshot.Probe.Status == ProbeReadinessStatus.DiscoveryFailed
                ? Text.SearchError
                : Text.NotFound;
            ProbeDetailText = snapshot.Probe.Diagnostic
                ?? (OperatingSystem.IsMacOS()
                ? Text.MacAutoDiscovery
                : Text.BmpHelp);
            issues.Add(Text.BmpIssue);
        }

        if (snapshot.Gdb.IsReady)
        {
            _gdbPath = snapshot.Gdb.Path;
            GdbStatusText = Text.Found;
            GdbDetailText = snapshot.Gdb.Path!;
        }
        else
        {
            _gdbPath = null;
            GdbStatusText = snapshot.Gdb.Status == GdbReadinessStatus.DiscoveryFailed
                ? Text.SearchError
                : Text.NotFound;
            GdbDetailText = snapshot.Gdb.Diagnostic
                ?? Text.GdbHelp;
            issues.Add(Text.GdbIssue);
        }

        if (snapshot.Catalog.IsReady)
        {
            var catalog = snapshot.Catalog.Catalog!;
            _catalog = catalog;
            _catalogDirectory = snapshot.Catalog.SourceDirectory;
            foreach (var product in catalog.Products)
                Products.Add(new ProductSummaryViewModel(product, Text));

            CatalogStatusText = snapshot.Catalog.TrustResult == CatalogTrustResult.Verified
                ? Text.SignatureVerified
                : Text.LabMode;
            CatalogDetailText = Text.CatalogProductDetail(catalog.Products.Count, snapshot.Catalog.SourcePath!);
            CatalogOverviewText = Text.CatalogOverview(
                catalog.GeneratedAt.ToLocalTime().ToString("g", Text.Culture),
                catalog.Products.Count,
                catalog.Revoked?.Count ?? 0);
        }
        else
        {
            _catalog = null;
            _catalogDirectory = null;
            issues.Add(Text.CatalogIssue);
            CatalogStatusText = snapshot.Catalog.Status switch
            {
                CatalogSessionStatus.NotFound or CatalogSessionStatus.ExplicitPathMissing => Text.NotFound,
                CatalogSessionStatus.TrustRejected or CatalogSessionStatus.SideloadRequiresLabMode => Text.CatalogRejected,
                _ => Text.CatalogError,
            };
            CatalogDetailText = snapshot.Catalog.Diagnostic ?? Text.CatalogNotReady;
            CatalogOverviewText = CatalogDetailText;
        }

        ReloadProductOptions(previousProductId, previousVersion);
        RefreshHistory();
        RefreshBatchLockText();

        var readyChecks = (snapshot.Probe.IsReady ? 1 : 0)
            + (snapshot.Gdb.IsReady ? 1 : 0)
            + (snapshot.Catalog.IsReady ? 1 : 0);
        var allReady = snapshot.IsReady;
        ReadinessSummary = allReady ? Text.StationReady : Text.StationPartial(readyChecks);
        ReadinessDetail = allReady
            ? Text.StationReadyDetail
            : Text.Attention(string.Join(", ", issues));
        ReadinessBrush = allReady ? ReadyBrush : AttentionBrush;
        LastCheckedText = Text.CheckedAt(DateTime.Now);

        RefreshFlashReadiness(updateBanner: true);
    }

    private void ReloadProductOptions(string? preferredProductId, string? preferredVersion)
    {
        _suppressSelectionReload = true;
        try
        {
            ProductOptions.Clear();
            if (_catalog is not null)
            {
                foreach (var product in _catalog.Products)
                    ProductOptions.Add(new ProductOption(product.ProductId, product.DisplayName));
            }

            SelectedProduct = ProductOptions.FirstOrDefault(p => p.ProductId == preferredProductId)
                ?? ProductOptions.FirstOrDefault();
        }
        finally
        {
            _suppressSelectionReload = false;
        }

        ReloadReleaseOptions(preferredVersion);
    }

    private void ReloadReleaseOptions(string? preferredVersion)
    {
        ReleaseOptions.Clear();
        var product = _selectedProduct is null ? null : _catalog?.FindProduct(_selectedProduct.ProductId);
        if (product is not null)
        {
            foreach (var release in product.Releases)
                ReleaseOptions.Add(new ReleaseOption(release.Version, release.IsRemote));
        }

        var fallback = product?.Default()?.Version;
        SelectedRelease = ReleaseOptions.FirstOrDefault(r => r.Version == preferredVersion)
            ?? ReleaseOptions.FirstOrDefault(r => r.Version == fallback)
            ?? ReleaseOptions.FirstOrDefault();
    }

    private void ApplyLanguage(string? code, bool persist)
    {
        var normalized = DesktopLocalization.Normalize(code);
        var text = DesktopLocalization.For(normalized);
        CultureInfo.CurrentUICulture = text.Culture;
        Text = text;
        _selectedLanguage = DesktopLocalization.Languages.First(option => option.Code == normalized);
        OnPropertyChanged(nameof(SelectedLanguage));
        OnPropertyChanged(nameof(LogShippingStatusText));
        OnPropertyChanged(nameof(BatchModeStatusText));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(PreviewBadge));
        OnPropertyChanged(nameof(HistoryAlphaNotice));
        OnPropertyChanged(nameof(HotkeyLabel));
        OnPropertyChanged(nameof(HotkeyHintText));
        OnPropertyChanged(nameof(FlashTooltipText));
        RebuildHotkeyOptions();
        RefreshAuthStatus();
        RefreshCloudStatus();

        if (!persist)
        {
            return;
        }

        var save = _settingsWorkflow.UpdateLanguage(normalized);
        if (save.IsSaved)
        {
            _settings = save.Settings!;
            LanguageSaveStatus = string.Empty;
        }
        else
        {
            LanguageSaveStatus = $"{Text.LanguageSaveFailed}: {save.Diagnostic ?? save.Status.ToString()}";
        }
    }

    private void RefreshHistory()
    {
        var snapshot = _historyWorkflow.Load(_settings, _batchId, limit: 50);
        HistoryRows.Clear();
        foreach (var row in snapshot.Rows)
            HistoryRows.Add(new HistoryRowViewModel(row, Text.Culture));

        HistoryStatusText = snapshot.Status switch
        {
            HistoryLoadStatus.Loaded when File.Exists(snapshot.DatabasePath) =>
                Text.FileFound(FormatBytes(new FileInfo(snapshot.DatabasePath).Length)),
            HistoryLoadStatus.DatabaseMissing => Text.FileCreateLater,
            _ => snapshot.Diagnostic ?? Text.LogNotCreated,
        };
        HistorySummaryText = Text.RecentAttempts(snapshot.Rows.Count);
        HistoryBatchSummaryText = snapshot.BatchCounts is { } counts
            ? (counts.Total == 0
                ? Text.HistoryBatchEmpty(snapshot.BatchId ?? string.Empty)
                : Text.HistoryBatchCounts(
                    snapshot.BatchId ?? string.Empty, counts.Total, counts.Pass, counts.Fail, counts.PassRate))
            : (snapshot.BatchesEnabled
                ? Text.HistoryNeedBatch(snapshot.Rows.Count)
                : Text.HistoryNoBatches(snapshot.Rows.Count));
    }

    private string ResolveDatabasePath() => ApplicationPaths.ResolveDatabasePath(_settings);

    private string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => Text.Megabytes(bytes / 1024d / 1024d),
        >= 1024 => Text.Kilobytes(bytes / 1024d),
        _ => Text.Bytes(bytes),
    };
}

public sealed record ProductOption(string ProductId, string DisplayName)
{
    public string Label => ProductId == DisplayName ? ProductId : $"{ProductId} — {DisplayName}";
}

public sealed record ReleaseOption(string Version, bool IsRemote)
{
    // Plain ASCII: the operator stations run the default UI font, where a cloud
    // glyph renders as tofu.
    public string Label => IsRemote ? $"v{Version} (GitHub)" : $"v{Version}";
}

public sealed record HistoryRowViewModel(
    string TimestampText,
    string Result,
    string ProductVersion,
    string OperatorBatch,
    string Error)
{
    public HistoryRowViewModel(FlashAttemptRow row, CultureInfo culture)
        : this(
            row.TsUtc.ToLocalTime().ToString("g", culture),
            row.Result,
            $"{row.ProductId} v{row.FirmwareVersion}",
            string.IsNullOrWhiteSpace(row.BatchId)
                ? row.Operator
                : $"{row.Operator} · {row.BatchId}",
            row.ErrorCode ?? string.Empty)
    {
    }
}

public sealed record ProductSummaryViewModel(
    string ProductId,
    string DisplayName,
    string TargetLabel,
    string TargetText,
    string ReleaseLabel,
    string ReleaseText)
{
    public ProductSummaryViewModel(Product product, DesktopText text)
        : this(
            product.ProductId,
            product.DisplayName,
            text.Target,
            text.TargetSummary(product.Target.PartNumber, product.Target.FlashKb),
            text.DefaultRelease,
            text.ReleaseSummary(product.DefaultRelease, product.Releases.Count))
    {
    }
}

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Single-flight async command. A second invocation while the first is running
/// is dropped rather than queued — the operator must never be able to start two
/// gdb sessions against one probe.
/// </summary>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute();
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
