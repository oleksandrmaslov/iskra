using System.Collections.ObjectModel;
using System.ComponentModel;
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

    private readonly AppSettings _settings;
    private readonly StationReadinessService _readinessService;
    private string _readinessSummary = "Перевірка станції…";
    private string _readinessDetail = "Очікуйте завершення локальної перевірки.";
    private IBrush _readinessBrush = AttentionBrush;
    private string _lastCheckedText = "Ще не перевірено";
    private string _probeStatusText = "Перевірка…";
    private string _probeDetailText = "Пошук GDB-інтерфейсу BMP";
    private string _gdbStatusText = "Перевірка…";
    private string _gdbDetailText = "Пошук arm-none-eabi-gdb";
    private string _catalogStatusText = "Перевірка…";
    private string _catalogDetailText = "Пошук локального підписаного каталогу";
    private string _catalogOverviewText = "Каталог ще не завантажено.";
    private string _historyStatusText = "Журнал ще не створено";

    public MainWindowViewModel()
    {
        _settings = AppSettingsStore.Load();
        _readinessService = new StationReadinessService(new CatalogSession());
        RefreshReadinessCommand = new RelayCommand(RefreshReadiness);
        RefreshReadiness();
    }

    public ICommand RefreshReadinessCommand { get; }
    public ObservableCollection<ProductSummaryViewModel> Products { get; } = [];
    public ObservableCollection<string> ProductNames { get; } = [];

    public string PlatformLabel => $"{RuntimeInformation.OSDescription.Trim()} · {RuntimeInformation.ProcessArchitecture}";
    public string SettingsPath => AppSettingsStore.DefaultPath;
    public string StationId => string.IsNullOrWhiteSpace(_settings.StationId) ? Environment.MachineName : _settings.StationId;
    public string OperatorName => _settings.LastOperator ?? string.Empty;
    public string CatalogSource => $"{CatalogTrust.OfficialCatalogSource.Owner}/{CatalogTrust.OfficialCatalogSource.Repo}";
    public string LogKeyPath => _settings.LogShipperPrivateKeyPath;
    public string LogShippingStatusText => _settings.LogShippingEnabled ? "Увімкнено в конфігурації" : "Вимкнено в конфігурації";
    public string BatchModeStatusText => BatchPolicy.Resolve(_settings, null).BatchesEnabled
        ? "Увімкнено — ідентифікатор буде обов’язковим"
        : "Вимкнено — локальне блокування партій не застосовується";
    public string DatabasePath => ResolveDatabasePath();
    public string MigrationSafetyNotice =>
        "Це перший безпечний зріз перенесення інтерфейсу. Він уже перевіряє BMP, GDB і каталог через Iskra.Core, але навмисно не запускає прошивання, доки кросплатформний процес не матиме тестового та апаратного паритету з WPF.";

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

    private void RefreshReadiness()
    {
        var snapshot = _readinessService.Evaluate(_settings);
        var issues = new List<string>();
        Products.Clear();
        ProductNames.Clear();

        if (snapshot.Probe.Status == ProbeReadinessStatus.Ready)
        {
            var probe = snapshot.Probe.Selected!;
            ProbeStatusText = "Підключено";
            ProbeDetailText = string.IsNullOrWhiteSpace(probe.SerialNumber)
                ? probe.PortName
                : $"{probe.PortName} · серійний № {probe.SerialNumber}";
        }
        else if (snapshot.Probe.Status == ProbeReadinessStatus.MultipleFound)
        {
            ProbeStatusText = $"Заблоковано: {snapshot.Probe.Discovered.Count} зонди";
            ProbeDetailText = "Залиште підключеним рівно один BMP: " + string.Join(
                "; ",
                snapshot.Probe.Discovered.Select(p => string.IsNullOrWhiteSpace(p.SerialNumber)
                    ? p.PortName
                    : $"{p.PortName} (серійний № {p.SerialNumber})"));
            issues.Add("кілька BMP");
        }
        else
        {
            ProbeStatusText = snapshot.Probe.Status == ProbeReadinessStatus.DiscoveryFailed
                ? "Помилка пошуку"
                : "Не знайдено";
            ProbeDetailText = snapshot.Probe.Diagnostic
                ?? (OperatingSystem.IsMacOS()
                ? "Автопошук macOS ще мігрує; явний /dev/cu.usbmodem… підтримується Core."
                : "Підключіть BMP і перевірте USB-кабель та права доступу до порту.");
            issues.Add("BMP");
        }

        if (snapshot.Gdb.IsReady)
        {
            GdbStatusText = "Знайдено";
            GdbDetailText = snapshot.Gdb.Path!;
        }
        else
        {
            GdbStatusText = snapshot.Gdb.Status == GdbReadinessStatus.DiscoveryFailed
                ? "Помилка пошуку"
                : "Не знайдено";
            GdbDetailText = snapshot.Gdb.Diagnostic
                ?? "Встановіть Arm GNU Toolchain або вкажіть чинний шлях у налаштуваннях.";
            issues.Add("ARM GDB");
        }

        if (snapshot.Catalog.IsReady)
        {
            var catalog = snapshot.Catalog.Catalog!;
            foreach (var product in catalog.Products)
            {
                Products.Add(new ProductSummaryViewModel(product));
                ProductNames.Add(product.DisplayName);
            }

            CatalogStatusText = snapshot.Catalog.TrustResult == CatalogTrustResult.Verified
                ? "Підпис перевірено"
                : "Лабораторний режим";
            CatalogDetailText = $"{catalog.Products.Count} виробів · {snapshot.Catalog.SourcePath}";
            CatalogOverviewText = $"Каталог згенеровано {catalog.GeneratedAt.ToLocalTime():g}; виробів: {catalog.Products.Count}; відкликань: {catalog.Revoked?.Count ?? 0}.";
        }
        else
        {
            issues.Add("каталог");
            CatalogStatusText = snapshot.Catalog.Status switch
            {
                CatalogSessionStatus.NotFound or CatalogSessionStatus.ExplicitPathMissing => "Не знайдено",
                CatalogSessionStatus.TrustRejected or CatalogSessionStatus.SideloadRequiresLabMode => "Відхилено",
                _ => "Помилка каталогу",
            };
            CatalogDetailText = snapshot.Catalog.Diagnostic ?? "Каталог не готовий.";
            CatalogOverviewText = CatalogDetailText;
        }

        var databasePath = ResolveDatabasePath();
        HistoryStatusText = File.Exists(databasePath)
            ? $"Знайдено · {FormatBytes(new FileInfo(databasePath).Length)}"
            : "Файл буде створено після першої спроби прошивання";

        var readyChecks = (snapshot.Probe.IsReady ? 1 : 0)
            + (snapshot.Gdb.IsReady ? 1 : 0)
            + (snapshot.Catalog.IsReady ? 1 : 0);
        var allReady = snapshot.IsReady;
        ReadinessSummary = allReady ? "Станція готова за базовими перевірками" : $"Готовність станції: {readyChecks}/3";
        ReadinessDetail = allReady
            ? "BMP, ARM GDB і підписаний каталог доступні. Саме прошивання в Avalonia залишено заблокованим до HIL-паритету."
            : $"Потрібна увага: {string.Join(", ", issues)}.";
        ReadinessBrush = allReady ? ReadyBrush : AttentionBrush;
        LastCheckedText = $"Перевірено {DateTime.Now:HH:mm:ss}";
    }

    private string ResolveDatabasePath() => string.IsNullOrWhiteSpace(_settings.DbPath)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Iskra", "flash_log.db")
        : Path.GetFullPath(_settings.DbPath);

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:F1} МБ",
        >= 1024 => $"{bytes / 1024d:F1} КБ",
        _ => $"{bytes} Б",
    };
}

public sealed record ProductSummaryViewModel(string ProductId, string DisplayName, string TargetText, string ReleaseText)
{
    public ProductSummaryViewModel(Product product)
        : this(
            product.ProductId,
            product.DisplayName,
            $"{product.Target.PartNumber} · {product.Target.FlashKb} КБ",
            $"v{product.DefaultRelease} · {product.Releases.Count} релізів")
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
