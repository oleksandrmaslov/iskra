using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Iskra.Core;
using Microsoft.Win32;

namespace Iskra.Wpf;

public partial class MainWindow : Window
{
    private AppSettings _settings = new();
    private Catalog? _catalog;
    private string? _catalogPath;
    private string? _catalogDir;
    private string? _gdbExe;
    private string? _port;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        // Window-level KeyDown so the operator hotkey works from anywhere on
        // the Flash tab — even with focus on the Operator/Batch boxes (Enter
        // is what barcode scanners emit as a line terminator).
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    // ============================================================
    // Startup
    // ============================================================

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = AppSettingsStore.Load();
        ApplySettingsToUI();

        if (!string.IsNullOrEmpty(_settings.LastOperator))
            OperatorBox.Text = _settings.LastOperator;
        if (!string.IsNullOrEmpty(_settings.LastBatch))
            BatchBox.Text = _settings.LastBatch;

        DiscoverGdb();
        DiscoverProbe();
        LoadCatalog();
        RefreshHistory();
        RefreshAuthStatus();
        RefreshCatalogCacheStatus();

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
            ? "gdb: НЕ ЗНАЙДЕНО"
            : $"gdb: {Path.GetFileName(_gdbExe)}";
    }

    private void DiscoverProbe()
    {
        var probes = ProbeDiscovery.FindGdbPorts();
        if (probes.Count == 1)
        {
            _port = probes[0].PortName;
            StatusPort.Text = $"Порт: {_port}";
        }
        else if (probes.Count == 0)
        {
            _port = null;
            StatusPort.Text = "Порт: BMP не знайдено";
        }
        else
        {
            _port = null;
            StatusPort.Text = $"Порт: знайдено {probes.Count} BMP (потрібно один)";
        }
    }

    private void LoadCatalog()
    {
        _catalog = null;
        _catalogPath = null;
        _catalogDir = null;
        ProductCombo.Items.Clear();
        VersionCombo.ItemsSource = null;

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(_settings.CatalogPath))
            candidates.Add(_settings.CatalogPath);
        // Sprint 3.5: auto-updated catalog cache. Wins over the bundled fallback
        // but not over an explicit CatalogPath setting.
        if (_settings.CatalogAutoUpdate)
        {
            var cached = Path.Combine(RemoteCatalogClient.DefaultCacheDir(), RemoteCatalogClient.CatalogFileName);
            if (File.Exists(cached)) candidates.Add(cached);
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "examples", "catalog.json"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "catalog.json"));
        candidates.Add("examples/catalog.json");
        candidates.Add("catalog.json");

        foreach (var c in candidates)
        {
            if (File.Exists(c)) { _catalogPath = Path.GetFullPath(c); break; }
        }
        if (_catalogPath is null)
        {
            StatusCatalog.Text = "Каталог: не знайдено (вкажіть шлях у Налаштуваннях)";
            return;
        }

        try
        {
            _catalog = CatalogJson.ParseFile(_catalogPath);
            _catalogDir = Path.GetDirectoryName(_catalogPath);

            var trust = CatalogTrust.VerifyCatalogFile(_catalogPath, _settings.RequireSignedCatalog);
            var trustText = trust switch
            {
                CatalogTrustResult.Verified         => "✓ Ed25519",
                CatalogTrustResult.UnsignedAllowed  => "без підпису",
                CatalogTrustResult.UnsignedRejected => "✗ потрібен підпис",
                CatalogTrustResult.BadSignature     => "✗ невірний підпис",
                CatalogTrustResult.NoPublicKeyConfigured => "✗ немає ключа",
                _                                   => trust.ToString(),
            };
            StatusCatalog.Text =
                $"Каталог: {_catalog.Products.Count} продукт(ів) · {trustText} · {Path.GetFileName(_catalogPath)}";

            if (trust is CatalogTrustResult.UnsignedRejected
                       or CatalogTrustResult.BadSignature
                       or CatalogTrustResult.NoPublicKeyConfigured)
            {
                // Catalog parsed but trust failed; disable flashing until user acks via Settings.
                _catalog = null;
                return;
            }

            foreach (var p in _catalog.Products)
                ProductCombo.Items.Add(p.ProductId);
            if (ProductCombo.Items.Count > 0)
                ProductCombo.SelectedIndex = 0;

            // Catalog browser tab
            CatalogHeader.Text =
                $"{Path.GetFileName(_catalogPath)} · {_catalog.Products.Count} продукт(ів) · {trustText}";
            CatalogProductsList.ItemsSource = _catalog.Products;
        }
        catch (CatalogParseException ex)
        {
            _catalog = null;
            StatusCatalog.Text = $"Каталог: помилка — {ex.Message}";
            CatalogHeader.Text = $"Помилка читання каталогу: {ex.Message}";
            CatalogProductsList.ItemsSource = null;
        }
    }

    private void CatalogReload_Click(object sender, RoutedEventArgs e)
    {
        LoadCatalog();
        RefreshBatchLockStatus();
    }

    private void ProductCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        VersionCombo.ItemsSource = null;
        if (_catalog is null || ProductCombo.SelectedItem is not string id)
        {
            RefreshAuthBanner(null);
            return;
        }
        var product = _catalog.FindProduct(id);
        if (product is null)
        {
            RefreshAuthBanner(null);
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
    }

    // ============================================================
    // Flash
    // ============================================================

    private async void FlashButton_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog is null) { Beep(); return; }
        if (ProductCombo.SelectedItem is not string productId) { Beep(); return; }

        var op = OperatorBox.Text?.Trim() ?? "";
        var batch = BatchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(op) || string.IsNullOrEmpty(batch))
        {
            SetBannerNeutral("Заповніть Оператор і Партія", warning: true);
            return;
        }
        if (_gdbExe is null)
        {
            SetBannerNeutral("gdb не знайдено. Повторно запустіть інсталятор Iskra.", warning: true);
            return;
        }
        if (_port is null)
        {
            SetBannerNeutral("Black Magic Probe не знайдено", warning: true);
            return;
        }

        var product = _catalog.FindProduct(productId);
        var release = VersionCombo.SelectedItem as FirmwareRelease ?? product?.Default();
        if (product is null || release is null) { Beep(); return; }

        // Batch lock check — refuse if this batch was started with a different
        // product/version. Operator must finish the batch or pick a new ID.
        try
        {
            using var lockStore = new SqliteLogStore(ResolveDbPath());
            var batchLock = lockStore.GetBatchLock(batch);
            if (batchLock is { } locked
                && (!string.Equals(locked.ProductId, product.ProductId, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(locked.FirmwareVersion, release.Version, StringComparison.OrdinalIgnoreCase)))
            {
                var msg = $"locked to {locked.ProductId} v{locked.FirmwareVersion}, attempted {product.ProductId} v{release.Version}";
                ShowFail("E_BATCH_LOCKED", msg);
                LogAttempt(op, batch, product, release, FlashResult.Fail,
                    "E_BATCH_LOCKED", msg, 0, null, null);
                RefreshHistory();
                RefreshBatchLockStatus();
                return;
            }
        }
        catch
        {
            // If lock check itself fails (eg. db unreadable), fall through and let
            // the normal flash flow surface the underlying error.
        }

        FlashButton.IsEnabled = false;
        GdbOutput.Clear();

        string elfPath;
        if (release.IsRemote)
        {
            SetBannerNeutral("Завантаження прошивки з GitHub…", warning: false);
            try
            {
                elfPath = await DownloadRemoteFirmwareAsync(release);
            }
            catch (NotSignedInException)
            {
                ShowFail("E_NOT_SIGNED_IN", "Відкрийте Налаштування → Авторизація GitHub → Увійти.");
                LogAttempt(op, batch, product, release, FlashResult.Fail,
                    "E_NOT_SIGNED_IN", "remote firmware requires GitHub sign-in", 0, null, null);
                RefreshHistory();
                FlashButton.IsEnabled = true;
                RefreshAuthStatus();
                return;
            }
            catch (RefreshTokenExpiredException)
            {
                ShowFail("E_AUTH_EXPIRED", "Сесію GitHub потрібно поновити (>6 міс без оновлення).");
                LogAttempt(op, batch, product, release, FlashResult.Fail,
                    "E_AUTH_EXPIRED", "github refresh token expired", 0, null, null);
                RefreshHistory();
                FlashButton.IsEnabled = true;
                RefreshAuthStatus();
                return;
            }
            catch (GitHubAssetNotFoundException ex)
            {
                ShowFail("E_ASSET_NOT_FOUND", ex.Message);
                LogAttempt(op, batch, product, release, FlashResult.Fail,
                    "E_ASSET_NOT_FOUND", ex.Message, 0, null, null);
                RefreshHistory();
                FlashButton.IsEnabled = true;
                return;
            }
            catch (Exception ex)
            {
                ShowFail("E_FW_DOWNLOAD_FAILED", ex.Message);
                LogAttempt(op, batch, product, release, FlashResult.Fail,
                    "E_FW_DOWNLOAD_FAILED", ex.Message, 0, null, null);
                RefreshHistory();
                FlashButton.IsEnabled = true;
                return;
            }
        }
        else
        {
            elfPath = Path.IsPathRooted(release.ElfFilename)
                ? release.ElfFilename
                : Path.Combine(_catalogDir!, release.ElfFilename);

            if (!File.Exists(elfPath))
            {
                ShowFail("E_ELF_NOT_FOUND", $"ELF не знайдено: {elfPath}");
                LogAttempt(op, batch, product, release, FlashResult.Fail,
                    "E_ELF_NOT_FOUND", elfPath, 0, null, null);
                RefreshHistory();
                FlashButton.IsEnabled = true;
                return;
            }
        }

        SetBannerNeutral("Виконується…", warning: false);

        // Remember the operator + batch for next launch.
        _settings.LastOperator = op;
        _settings.LastBatch = batch;
        try { AppSettingsStore.Save(_settings); } catch { /* non-fatal */ }

        try
        {
            var sha = FirmwareIntegrity.ComputeSha256Hex(elfPath);
            if (!FirmwareIntegrity.HashesMatch(sha, release.ElfSha256))
            {
                var msg = $"computed {sha}, expected {release.ElfSha256.ToLowerInvariant()}";
                ShowFail("E_FW_HASH_MISMATCH", msg);
                LogAttempt(op, batch, product, release, FlashResult.Fail,
                    "E_FW_HASH_MISMATCH", msg, 0, null, null);
                RefreshHistory();
                return;
            }

            var opts = new FlashOptions(
                ElfPath:            elfPath,
                Port:               _port,
                Power:              _settings.Power,
                BmpFrequencyHz:     _settings.BmpFrequencyHz,
                ConnectUnderReset:  _settings.ConnectUnderReset,
                Product:            product.ProductId,
                Operator:           op,
                Batch:              batch,
                StationId:          _settings.StationId,
                TargetBmpMatch:     product.Target.BmpMatch,
                TargetFlashKb:      product.Target.FlashKb,
                FirmwareVersion:    release.Version,
                FirmwareSha256:     release.ElfSha256,
                GdbPath:            _gdbExe,
                DbPath:             _settings.DbPath);

            var gdb = new GdbProcess(_gdbExe);
            var outcome = await FlashStateMachine.RunAsync(
                gdb,
                opts,
                timeout: TimeSpan.FromSeconds(Math.Max(1, _settings.TimeoutSeconds)),
                onLine: line => Dispatcher.Invoke(() => GdbOutput.AppendText(line.Text + "\n")));

            if (outcome.IsPass)
                ShowPass(outcome.Duration);
            else
                ShowFail(outcome.ErrorCode!, outcome.ErrorMessage ?? "");

            LogAttempt(op, batch, product, release, outcome.Result,
                outcome.ErrorCode, outcome.ErrorMessage,
                (long)outcome.Duration.TotalMilliseconds, outcome.GdbTail, outcome.DetectedTarget);
            RefreshHistory();
        }
        catch (Exception ex)
        {
            ShowFail("E_INTERNAL", ex.Message);
        }
        finally
        {
            FlashButton.IsEnabled = true;
        }
    }

    private void ShowPass(TimeSpan duration)
    {
        ResultBanner.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
        ResultText.Foreground = Brushes.White;
        ResultDetail.Foreground = Brushes.White;
        ResultText.Text = "✓ ПРОШИВКА УСПІШНА";
        ResultDetail.Text = $"{duration.TotalMilliseconds:F0} мс";
    }

    private void ShowFail(string code, string detail)
    {
        ResultBanner.Background = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
        ResultText.Foreground = Brushes.White;
        ResultDetail.Foreground = Brushes.White;
        ResultText.Text = $"✗ {code}";
        ResultDetail.Text = ErrorHints.For(code);
        if (!string.IsNullOrEmpty(detail))
            GdbOutput.AppendText($"\n[Деталі помилки]\n{detail}\n");
    }

    private void SetBannerNeutral(string msg, bool warning)
    {
        ResultBanner.Background = warning
            ? new SolidColorBrush(Color.FromRgb(0xF2, 0xC1, 0x4E))
            : new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8));
        ResultText.Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        ResultDetail.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        ResultText.Text = msg;
        ResultDetail.Text = "";
    }

    private void LogAttempt(string op, string batch, Product product, FirmwareRelease release,
        FlashResult result, string? errCode, string? errMsg, long durMs,
        string? gdbTail, string? detected)
    {
        try
        {
            using var log = new SqliteLogStore(ResolveDbPath());
            log.Append(new FlashAttemptRecord(
                TsUtc:            DateTime.UtcNow,
                Operator:         op,
                StationId:        _settings.StationId,
                BatchId:          batch,
                ProductId:        product.ProductId,
                FirmwareVersion:  release.Version,
                FirmwareSha256:   release.ElfSha256,
                TargetBmpMatch:   product.Target.BmpMatch,
                TargetDetected:   detected,
                TargetFlashKb:    product.Target.FlashKb,
                ComPort:          _port ?? "",
                ProbeSerial:      null,
                Power:            _settings.Power,
                ConnectRst:       _settings.ConnectUnderReset,
                BmpFrequencyHz:   _settings.BmpFrequencyHz,
                Result:           result,
                ErrorCode:        errCode,
                ErrorMessage:     errMsg,
                DurationMs:       durMs,
                GdbTail:          gdbTail));
        }
        catch { /* never let log failures crash the UI */ }
    }

    private string ResolveDbPath()
    {
        if (!string.IsNullOrEmpty(_settings.DbPath)) return _settings.DbPath;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Iskra");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "flash_log.db");
    }

    // ============================================================
    // History tab
    // ============================================================

    private void HistoryRefresh_Click(object sender, RoutedEventArgs e) => RefreshHistory();

    private void BatchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshBatchLockStatus();
    }

    private void RefreshBatchLockStatus()
    {
        var batch = BatchBox.Text?.Trim();
        if (string.IsNullOrEmpty(batch))
        {
            BatchLockLabel.Text = "";
            return;
        }
        try
        {
            var dbPath = ResolveDbPath();
            if (!File.Exists(dbPath))
            {
                BatchLockLabel.Text = "";
                return;
            }
            using var store = new SqliteLogStore(dbPath);
            var locked = store.GetBatchLock(batch);
            BatchLockLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
            if (locked is { } l)
                BatchLockLabel.Text = $"🔒 Партію заблоковано на {l.ProductId} v{l.FirmwareVersion}";
            else
                BatchLockLabel.Text = "Партія нова — перша прошивка визначить продукт + версію.";
        }
        catch
        {
            BatchLockLabel.Text = "";
        }
    }

    private void ExportCsvBatch_Click(object sender, RoutedEventArgs e)
        => ExportCsv(batchOnly: true);

    private void ExportCsvAll_Click(object sender, RoutedEventArgs e)
        => ExportCsv(batchOnly: false);

    private void ExportCsv(bool batchOnly)
    {
        var dbPath = ResolveDbPath();
        if (!File.Exists(dbPath))
        {
            BatchSummary.Text = "Журнал ще не створено — нічого експортувати.";
            return;
        }

        string? batch = batchOnly ? BatchBox.Text?.Trim() : null;
        if (batchOnly && string.IsNullOrEmpty(batch))
        {
            BatchSummary.Text = "Введіть Партію на вкладці Прошивка, щоб експортувати тільки її.";
            return;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var defaultName = batchOnly
            ? $"flash_log_{batch}_{stamp}.csv"
            : $"flash_log_all_{stamp}.csv";

        var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            Title = batchOnly ? $"Експорт партії {batch}" : "Експорт усього журналу",
            FileName = defaultName,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var store = new SqliteLogStore(dbPath);
            var rows = store.ExportCsv(dlg.FileName, batch);
            BatchSummary.Text = $"✓ Експортовано {rows} рядків у {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            BatchSummary.Text = $"Помилка експорту: {ex.Message}";
        }
    }

    private void RefreshHistory()
    {
        try
        {
            var dbPath = ResolveDbPath();
            if (!File.Exists(dbPath))
            {
                HistoryGrid.ItemsSource = null;
                BatchSummary.Text = "Журнал ще не створено.";
                return;
            }
            using var store = new SqliteLogStore(dbPath);
            var rows = store.QueryRecent(200);
            HistoryGrid.ItemsSource = rows;

            var currentBatch = BatchBox.Text?.Trim();
            if (!string.IsNullOrEmpty(currentBatch))
            {
                var (total, pass, fail) = store.CountsForBatch(currentBatch);
                if (total > 0)
                {
                    var rate = (double)pass / total;
                    BatchSummary.Text =
                        $"Партія «{currentBatch}»: {pass} PASS / {fail} FAIL  ({rate:P0} успіх)";
                }
                else
                {
                    BatchSummary.Text = $"Партія «{currentBatch}»: ще немає записів.";
                }
            }
            else
            {
                BatchSummary.Text = $"Останні {rows.Count} записів (вкажіть Партію для зведення).";
            }
        }
        catch (Exception ex)
        {
            BatchSummary.Text = $"Помилка читання журналу: {ex.Message}";
            HistoryGrid.ItemsSource = null;
        }
    }

    // ============================================================
    // Settings tab
    // ============================================================

    private void ApplySettingsToUI()
    {
        SettingsCatalogPath.Text   = _settings.CatalogPath ?? "";
        SettingsRequireSigned.IsChecked = _settings.RequireSignedCatalog;
        SettingsCatalogAutoUpdate.IsChecked = _settings.CatalogAutoUpdate;
        SettingsCatalogOwner.Text  = _settings.CatalogOwner;
        SettingsCatalogRepo.Text   = _settings.CatalogRepo;
        SettingsGdbPath.Text       = _settings.GdbPath ?? "";
        SettingsBmpFreq.Text       = _settings.BmpFrequencyHz.ToString(CultureInfo.InvariantCulture);
        SettingsPowerExternal.IsChecked = _settings.Power == PowerMode.External;
        SettingsPowerProbe.IsChecked    = _settings.Power == PowerMode.Probe;
        SettingsConnectReset.IsChecked  = _settings.ConnectUnderReset;
        SettingsTimeout.Text       = _settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        SettingsDbPath.Text        = _settings.DbPath ?? "";
        SettingsStationId.Text     = _settings.StationId;
        SelectHotkeyComboItem(_settings.FlashHotkey);
        RefreshFlashHotkeyHint();
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
            FlashButtonTooltipText.Text = "Запустити прошивку поточної плати";
        }
        else
        {
            FlashHotkeyHint.Text = $"(або натисніть {label})";
            FlashButtonTooltipText.Text = $"Запустити прошивку. Гаряча клавіша: {label}";
        }
    }

    private static (string Label, Key Key) HotkeyDisplay(FlashHotkey hk) => hk switch
    {
        FlashHotkey.Space => ("Пробіл", Key.Space),
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
        if ((MainTabs.SelectedItem as TabItem)?.Header as string is not "Прошивка") return;

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


    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.CatalogPath          = NullIfEmpty(SettingsCatalogPath.Text);
            _settings.RequireSignedCatalog = SettingsRequireSigned.IsChecked == true;
            _settings.CatalogAutoUpdate    = SettingsCatalogAutoUpdate.IsChecked == true;
            _settings.CatalogOwner         = string.IsNullOrWhiteSpace(SettingsCatalogOwner.Text)
                                              ? "oleksandrmaslov" : SettingsCatalogOwner.Text.Trim();
            _settings.CatalogRepo          = string.IsNullOrWhiteSpace(SettingsCatalogRepo.Text)
                                              ? "iskra-catalog" : SettingsCatalogRepo.Text.Trim();
            _settings.GdbPath              = NullIfEmpty(SettingsGdbPath.Text);
            _settings.Power                = SettingsPowerProbe.IsChecked == true
                                              ? PowerMode.Probe : PowerMode.External;
            _settings.ConnectUnderReset    = SettingsConnectReset.IsChecked == true;
            _settings.DbPath               = NullIfEmpty(SettingsDbPath.Text);
            _settings.StationId            = string.IsNullOrWhiteSpace(SettingsStationId.Text)
                                              ? Environment.MachineName
                                              : SettingsStationId.Text.Trim();

            if (!int.TryParse(SettingsBmpFreq.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) || f <= 0)
                throw new FormatException("Частота повинна бути додатнім цілим числом (Hz).");
            _settings.BmpFrequencyHz = f;

            if (!int.TryParse(SettingsTimeout.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) || t <= 0)
                throw new FormatException("Тайм-аут повинен бути додатнім цілим (секунди).");
            _settings.TimeoutSeconds = t;

            _settings.FlashHotkey = ReadHotkeyComboSelection();

            AppSettingsStore.Save(_settings);
            RefreshFlashHotkeyHint();

            // Re-discover with new settings in case catalog/gdb paths changed.
            DiscoverGdb();
            LoadCatalog();
            RefreshHistory();

            SettingsStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
            SettingsStatus.Text = $"✓ Збережено о {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SettingsStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            SettingsStatus.Text = $"✗ {ex.Message}";
        }
    }

    private void SettingsReset_Click(object sender, RoutedEventArgs e)
    {
        _settings = new AppSettings();
        ApplySettingsToUI();
        SettingsStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        SettingsStatus.Text = "Значення скинуто. Натисніть «Зберегти», щоб застосувати.";
    }

    private void PickCatalogPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Catalog files (*.json)|*.json|All files (*.*)|*.*",
            Title  = "Виберіть catalog.json",
        };
        if (dlg.ShowDialog() == true) SettingsCatalogPath.Text = dlg.FileName;
    }

    private void PickGdbPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "arm-none-eabi-gdb.exe|arm-none-eabi-gdb.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
            Title  = "Виберіть arm-none-eabi-gdb.exe",
        };
        if (dlg.ShowDialog() == true) SettingsGdbPath.Text = dlg.FileName;
    }

    private void PickDbPath_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "SQLite databases (*.db)|*.db|All files (*.*)|*.*",
            Title  = "Файл журналу",
            FileName = "flash_log.db",
        };
        if (dlg.ShowDialog() == true) SettingsDbPath.Text = dlg.FileName;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
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
            AuthStatusLabel.Text = $"✗ Файл токенів пошкоджено: {ex.Message}";
            AuthLoginButton.IsEnabled  = GitHubAppConfig.IsConfigured;
            AuthLogoutButton.IsEnabled = true;
            RefreshAuthBanner(CurrentSelectedRelease());
            return;
        }

        if (!GitHubAppConfig.IsConfigured)
        {
            AuthStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            AuthStatusLabel.Text = "✗ GitHub Client ID не налаштовано в збірці.";
            AuthLoginButton.IsEnabled = false;
            AuthLogoutButton.IsEnabled = stored is not null;
            RefreshAuthBanner(CurrentSelectedRelease());
            return;
        }

        if (stored is null)
        {
            AuthStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x66, 0x00));
            AuthStatusLabel.Text = "Не авторизовано.";
            AuthLoginButton.IsEnabled = true;
            AuthLogoutButton.IsEnabled = false;
            RefreshAuthBanner(CurrentSelectedRelease());
            return;
        }

        var now = DateTime.UtcNow;
        if (stored.RefreshTokenIsExpired(now))
        {
            AuthStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            AuthStatusLabel.Text = "✗ Сесія застаріла. Увійдіть знову.";
            AuthLoginButton.IsEnabled = true;
            AuthLogoutButton.IsEnabled = true;
            RefreshAuthBanner(CurrentSelectedRelease());
            return;
        }

        var freshAccess = stored.AccessTokenIsFresh(now, AuthRefreshSkew);
        AuthStatusLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
        AuthStatusLabel.Text =
            $"✓ Авторизовано. Access {(freshAccess ? "дійсний" : "оновиться")} до {stored.AccessTokenExpiresAtUtc:yyyy-MM-dd HH:mm} UTC · " +
            $"Refresh до {stored.RefreshTokenExpiresAtUtc:yyyy-MM-dd} UTC";
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
                ? "Цей продукт потребує завантаження з GitHub, але Client ID не налаштовано в збірці."
                : (stored is null
                    ? $"Прошивка «{release.Version}» завантажується з GitHub. Потрібен вхід."
                    : "Сесія GitHub застаріла. Увійдіть знову для завантаження прошивки.");
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
            MessageBox.Show(this, $"Не вдалося видалити токени: {ex.Message}",
                "Iskra — вихід", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            AuthStatusLabel.Text = $"✗ Не вдалося перевірити: {ex.Message}";
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
                "GitHub Client ID не налаштовано в збірці. Зверніться до розробника.",
                "Iskra — вхід", MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show(this, $"Не вдалося запросити код пристрою: {ex.Message}",
                    "Iskra — вхід", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dlg = new DeviceFlowDialog(flow, code) { Owner = this };
            var ok = dlg.ShowDialog();

            if (ok != true)
            {
                if (!string.IsNullOrEmpty(dlg.ErrorMessage))
                    MessageBox.Show(this, dlg.ErrorMessage, "Iskra — вхід",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                new TokenStore().Save(StoredTokens.From(dlg.Token!, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Не вдалося зберегти токени у %PROGRAMDATA%\\Iskra: {ex.Message}\n\n" +
                    "Можливо, потрібно запустити програму від імені адміністратора (один раз).",
                    "Iskra — вхід", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        finally
        {
            RefreshAuthStatus();
        }
    }

    private async Task<string> DownloadRemoteFirmwareAsync(FirmwareRelease release)
    {
        if (release.ElfSource is null)
            throw new InvalidOperationException("release.ElfSource is null but IsRemote is true");

        using var http = new HttpClient();
        var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);
        var store = new TokenStore();
        var provider = new AccessTokenProvider(store, flow);
        var api = new GitHubReleaseAssetClient(http);
        var cache = new FirmwareCache(api, provider.GetFreshAccessTokenAsync);
        return await cache.GetOrDownloadAsync(release.ElfSource, release.ElfSha256);
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
            CatalogCacheStatus.Text = "Кеш порожній — натисніть «Перевірити оновлення».";
        }
        else if (cached is null)
        {
            CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
            CatalogCacheStatus.Text = $"✗ Кешований {tag}, але підпис не пройшов перевірку.";
        }
        else
        {
            CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
            CatalogCacheStatus.Text = $"✓ {tag} · {cached.Products.Count} продукт(ів) · згенеровано {cached.GeneratedAt:yyyy-MM-dd HH:mm} UTC";
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
                    StatusCatalog.Text =
                        $"Каталог: оновлено до {r.TagName} — натисніть «Перезавантажити» на вкладці Каталог.";
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
        CatalogCacheStatus.Text = "Перевірка…";

        // Apply any pending owner/repo edits BEFORE the fetch so the user
        // doesn't have to click Save first.
        _settings.CatalogOwner = string.IsNullOrWhiteSpace(SettingsCatalogOwner.Text)
            ? "oleksandrmaslov" : SettingsCatalogOwner.Text.Trim();
        _settings.CatalogRepo  = string.IsNullOrWhiteSpace(SettingsCatalogRepo.Text)
            ? "iskra-catalog" : SettingsCatalogRepo.Text.Trim();

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
                        ? $"✓ Оновлено до {r.TagName}. Перезавантажте каталог щоб застосувати."
                        : $"✓ {r.TagName} (без змін з попередньої перевірки).";
                    break;
                case RemoteCatalogStatus.AlreadyUpToDate:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x8A, 0x1B));
                    CatalogCacheStatus.Text = $"✓ Вже актуально: {r.TagName}.";
                    break;
                case RemoteCatalogStatus.NoRelease:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x66, 0x00));
                    CatalogCacheStatus.Text = $"⚠ {_settings.CatalogOwner}/{_settings.CatalogRepo} поки що не має жодного релізу.";
                    break;
                case RemoteCatalogStatus.BadSignature:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    CatalogCacheStatus.Text = "✗ Підпис каталогу не співпадає з ключем у застосунку (можлива атака; кеш не змінено).";
                    break;
                case RemoteCatalogStatus.AssetsMissing:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    CatalogCacheStatus.Text = $"✗ Реліз без catalog.json або catalog.json.sig — {r.Message}";
                    break;
                case RemoteCatalogStatus.ParseError:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    CatalogCacheStatus.Text = $"✗ Завантажений каталог не вдалось розпарсити: {r.Message}";
                    break;
                default:
                    CatalogCacheStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    CatalogCacheStatus.Text = $"✗ Помилка: {r.Status} — {r.Message}";
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
