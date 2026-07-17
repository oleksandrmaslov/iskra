using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Avalonia.Media;
using Iskra.Application;
using Iskra.Core;

namespace Iskra.Desktop;

public sealed class MainWindowViewModel : ViewModelBase
{
    private static readonly IBrush ReadyBrush = new SolidColorBrush(Color.Parse("#2E8B57"));
    private static readonly IBrush AttentionBrush = new SolidColorBrush(Color.Parse("#D97706"));

    private AppSettings _settings;
    private readonly StationReadinessService _readinessService;
    private readonly HistoryWorkflow _historyWorkflow;
    private readonly SettingsWorkflow _settingsWorkflow;
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

    public MainWindowViewModel()
        : this(
            new SettingsWorkflow(),
            new HistoryWorkflow(),
            new StationReadinessService(new CatalogSession()))
    {
    }

    public MainWindowViewModel(
        SettingsWorkflow settingsWorkflow,
        HistoryWorkflow historyWorkflow,
        StationReadinessService readinessService)
    {
        _settingsWorkflow = settingsWorkflow ?? throw new ArgumentNullException(nameof(settingsWorkflow));
        _historyWorkflow = historyWorkflow ?? throw new ArgumentNullException(nameof(historyWorkflow));
        _readinessService = readinessService ?? throw new ArgumentNullException(nameof(readinessService));
        _settings = _settingsWorkflow.Load();
        ApplyLanguage(_settings.LanguageCode, persist: false);
        RefreshReadinessCommand = new RelayCommand(RefreshReadiness);
        RefreshReadiness();
    }

    public ICommand RefreshReadinessCommand { get; }
    public ObservableCollection<ProductSummaryViewModel> Products { get; } = [];
    public ObservableCollection<string> ProductNames { get; } = [];
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
    public string AlphaWindowTitle => $"{Text.WindowTitle} — Avalonia Alpha";
    public string AlphaBadge => Text.LanguageCode switch
    {
        IskraLanguages.English => "AVALONIA ALPHA · READ-ONLY · FLASH DISABLED",
        IskraLanguages.German => "AVALONIA ALPHA · SCHREIBGESCHÜTZT · FLASHEN GESPERRT",
        _ => "AVALONIA ALPHA · ЛИШЕ ЧИТАННЯ · ПРОШИВАННЯ ЗАБЛОКОВАНО",
    };
    public string HistoryAlphaNotice => Text.LanguageCode switch
    {
        IskraLanguages.English => "Recent SQLite attempts are read through the shared history workflow. Export and flashing remain disabled in this alpha.",
        IskraLanguages.German => "Letzte SQLite-Versuche werden über den gemeinsamen Verlauf geladen. Export und Flashen bleiben in dieser Alpha gesperrt.",
        _ => "Останні спроби SQLite читаються через спільний процес історії. Експорт і прошивання в цій альфа-версії залишаються вимкненими.",
    };
    public string SettingsPath => AppSettingsStore.DefaultPath;
    public string StationId => string.IsNullOrWhiteSpace(_settings.StationId) ? Environment.MachineName : _settings.StationId;
    public string OperatorName => _settings.LastOperator ?? string.Empty;
    public string CatalogSource => $"{CatalogTrust.OfficialCatalogSource.Owner}/{CatalogTrust.OfficialCatalogSource.Repo}";
    public string LogKeyPath => _settings.LogShipperPrivateKeyPath;
    public string LogShippingStatusText => _settings.LogShippingEnabled ? Text.LogShippingEnabled : Text.LogShippingDisabled;
    public string BatchModeStatusText => BatchPolicy.Resolve(_settings, null).BatchesEnabled
        ? Text.BatchEnabled
        : Text.BatchDisabled;
    public string DatabasePath => ResolveDatabasePath();
    public string MigrationSafetyNotice => Text.MigrationSafetyNotice;

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

    private void RefreshReadiness()
    {
        var snapshot = _readinessService.Evaluate(_settings);
        var issues = new List<string>();
        Products.Clear();
        ProductNames.Clear();

        if (snapshot.Probe.Status == ProbeReadinessStatus.Ready)
        {
            var probe = snapshot.Probe.Selected!;
            ProbeStatusText = Text.Connected;
            ProbeDetailText = string.IsNullOrWhiteSpace(probe.SerialNumber)
                ? probe.PortName
                : Text.SerialNumber(probe.PortName, probe.SerialNumber);
        }
        else if (snapshot.Probe.Status == ProbeReadinessStatus.MultipleFound)
        {
            ProbeStatusText = Text.BlockedProbes(snapshot.Probe.Discovered.Count);
            ProbeDetailText = Text.LeaveOneBmp(string.Join(
                "; ", snapshot.Probe.Discovered.Select(p => string.IsNullOrWhiteSpace(p.SerialNumber)
                    ? p.PortName
                    : Text.PortWithSerial(p.PortName, p.SerialNumber))));
            issues.Add(Text.MultipleBmpIssue);
        }
        else
        {
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
            GdbStatusText = Text.Found;
            GdbDetailText = snapshot.Gdb.Path!;
        }
        else
        {
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
            foreach (var product in catalog.Products)
            {
                Products.Add(new ProductSummaryViewModel(product, Text));
                ProductNames.Add(product.DisplayName);
            }

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

        RefreshHistory();

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
        OnPropertyChanged(nameof(MigrationSafetyNotice));
        OnPropertyChanged(nameof(AlphaWindowTitle));
        OnPropertyChanged(nameof(AlphaBadge));
        OnPropertyChanged(nameof(HistoryAlphaNotice));

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
        var snapshot = _historyWorkflow.Load(_settings, _settings.LastBatch, limit: 50);
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
        HistorySummaryText = Text.LanguageCode switch
        {
            IskraLanguages.English => $"Recent attempts: {snapshot.Rows.Count}",
            IskraLanguages.German => $"Letzte Versuche: {snapshot.Rows.Count}",
            _ => $"Останні спроби: {snapshot.Rows.Count}",
        };
    }

    private string ResolveDatabasePath() => ApplicationPaths.ResolveDatabasePath(_settings);

    private string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => Text.Megabytes(bytes / 1024d / 1024d),
        >= 1024 => Text.Kilobytes(bytes / 1024d),
        _ => Text.Bytes(bytes),
    };
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

internal sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute();
}
