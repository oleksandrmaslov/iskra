using System.Globalization;
using Iskra.Core;

namespace Iskra.Desktop;

public sealed record LanguageOption(string Code, string DisplayName);

public static partial class DesktopLocalization
{
    public const string DefaultLanguageCode = IskraLanguages.Ukrainian;

    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(IskraLanguages.Ukrainian, "Українська"),
        new(IskraLanguages.English, "English"),
        new(IskraLanguages.German, "Deutsch"),
    ];

    // Built lazily and from two parts per language: the operator-facing core in
    // this file, plus the Settings/auth/maintenance strings in the sibling
    // partial. Lazy construction also guarantees both static arrays are
    // initialized regardless of which partial the compiler emits first.
    private static readonly Lazy<IReadOnlyDictionary<string, DesktopText>> Catalogs = new(BuildCatalogs);

    private static IReadOnlyDictionary<string, DesktopText> BuildCatalogs() =>
        new Dictionary<string, DesktopText>(StringComparer.OrdinalIgnoreCase)
        {
            [IskraLanguages.Ukrainian] =
                new(IskraLanguages.Ukrainian, CreateCatalog([.. UkrainianCore, .. UkrainianExtended])),
            [IskraLanguages.English] =
                new(IskraLanguages.English, CreateCatalog([.. EnglishCore, .. EnglishExtended])),
            [IskraLanguages.German] =
                new(IskraLanguages.German, CreateCatalog([.. GermanCore, .. GermanExtended])),
        };

    public static string Normalize(string? code) => IskraLanguages.NormalizeOrDefault(code);

    public static DesktopText For(string? code) => Catalogs.Value[Normalize(code)];

    public static CultureInfo CultureFor(string? code) => IskraLanguages.CultureFor(code);

    private static readonly (DesktopTextKey Key, string Value)[] UkrainianCore =
    [
        (DesktopTextKey.WindowTitle, "Iskra — станція прошивання"),
        (DesktopTextKey.Tagline, "Безпечне прошивання та перевірка пристроїв"),
        (DesktopTextKey.TabFlash, "Прошивка"),
        (DesktopTextKey.Refresh, "Перевірити ще раз"),
        (DesktopTextKey.SignedCatalog, "ПІДПИСАНИЙ КАТАЛОГ"),
        (DesktopTextKey.OperatorChange, "Зміна оператора"),
        (DesktopTextKey.Operator, "ОПЕРАТОР"),
        (DesktopTextKey.OperatorPlaceholder, "Ім’я оператора"),
        (DesktopTextKey.Product, "ВИРІБ"),
        (DesktopTextKey.Batch, "ПАРТІЯ"),
        (DesktopTextKey.BatchPlaceholder, "ID партії"),
        (DesktopTextKey.Version, "ВЕРСІЯ"),
        (DesktopTextKey.FlashAction, "ПРОШИТИ"),
        (DesktopTextKey.GdbDetails, "Деталі gdb"),
        (DesktopTextKey.GdbLogEmpty, "Вивід gdb з’явиться тут після запуску прошивання."),
        (DesktopTextKey.FlashReady, "Готово до прошивання"),
        (DesktopTextKey.FlashReadyBatchOn, "Натисніть ПРОШИТИ. Партію буде зафіксовано за першою спробою."),
        (DesktopTextKey.FlashReadyBatchOff, "Натисніть ПРОШИТИ. Режим партій вимкнено."),
        (DesktopTextKey.FlashDownloading, "Завантаження прошивки…"),
        (DesktopTextKey.FlashRunning, "Прошивання…"),
        (DesktopTextKey.FlashPass, "✓ УСПІШНО"),
        (DesktopTextKey.FlashDuration, "Виконано за {0} мс"),
        (DesktopTextKey.ErrorDetails, "деталі помилки"),
        (DesktopTextKey.ReadyProbe, "Підключіть Black Magic Probe"),
        (DesktopTextKey.ReadyProbeDetail, "Потрібен рівно один програматор. Перевірте USB-кабель і натисніть «Оновити»."),
        (DesktopTextKey.ReadyGdb, "arm-none-eabi-gdb не знайдено"),
        (DesktopTextKey.ReadyGdbDetail, "Встановіть Arm GNU Toolchain або вкажіть шлях у налаштуваннях."),
        (DesktopTextKey.ReadyCatalog, "Каталог не завантажено"),
        (DesktopTextKey.ReadyCatalogDetail, "Перевірте шлях до каталогу та його підпис у налаштуваннях."),
        (DesktopTextKey.ReadyOperator, "Введіть ім’я оператора"),
        (DesktopTextKey.ReadyOperatorDetail, "Ім’я потрапляє в журнал кожної спроби."),
        (DesktopTextKey.ReadyBatch, "Введіть ID партії"),
        (DesktopTextKey.ReadyBatchDetail, "Режим партій увімкнено в налаштуваннях."),
        (DesktopTextKey.ReadySelection, "Виберіть виріб і версію"),
        (DesktopTextKey.BatchLocked, "Партію зафіксовано: {0} v{1} · sha256 {2}"),
        (DesktopTextKey.AuthHintRemote, "Ця версія завантажується з GitHub. Увійдіть на станції через WPF-застосунок або «Iskra.Cli --login»."),
        (DesktopTextKey.HotkeyHint, "Гаряча клавіша: {0}"),
        (DesktopTextKey.HotkeyTooltip, "Запускає прошивання. Гаряча клавіша: {0}"),
        (DesktopTextKey.HotkeySpace, "Пробіл"),
        (DesktopTextKey.TabHistory, "Історія"),
        (DesktopTextKey.LocalLog, "Локальний журнал"),
        (DesktopTextKey.HistoryMigration, "SQLite залишається локальним джерелом істини. Спроби прошивання з цього інтерфейсу пишуться в ту саму базу, що й у WPF; експорт CSV мігрує далі."),
        (DesktopTextKey.FileStatus, "СТАН ФАЙЛУ"),
        (DesktopTextKey.TabCatalog, "Каталог"),
        (DesktopTextKey.AvailableProducts, "Доступні вироби"),
        (DesktopTextKey.Target, "ЦІЛЬ"),
        (DesktopTextKey.DefaultRelease, "ТИПОВИЙ РЕЛІЗ"),
        (DesktopTextKey.TabSettings, "Налаштування"),
        (DesktopTextKey.CurrentConfiguration, "Поточна конфігурація станції"),
        (DesktopTextKey.SettingsMigration, "На першому етапі кросплатформний інтерфейс читає чинні безпечні налаштування без їх дублювання. Редагування з автоматичним збереженням буде підключено до спільного процесу налаштувань наступним кроком."),
        (DesktopTextKey.Station, "СТАНЦІЯ"),
        (DesktopTextKey.SettingsFile, "ФАЙЛ НАЛАШТУВАНЬ"),
        (DesktopTextKey.OfficialCatalog, "ОФІЦІЙНИЙ КАТАЛОГ"),
        (DesktopTextKey.CloudLog, "ХМАРНИЙ ЖУРНАЛ"),
        (DesktopTextKey.PrivateStationKey, "ПРИВАТНИЙ КЛЮЧ СТАНЦІЇ (.PEM)"),
        (DesktopTextKey.BatchMode, "РЕЖИМ ПАРТІЙ"),
        (DesktopTextKey.SettingsGroupingMigration, "Керування партіями та шлях до ключа журналу будуть згруповані тут у наступному UI-зрізі."),
        (DesktopTextKey.Language, "МОВА"),
        (DesktopTextKey.LanguageSaveFailed, "Не вдалося зберегти мову"),
        (DesktopTextKey.CheckingStation, "Перевірка станції…"),
        (DesktopTextKey.WaitLocalCheck, "Очікуйте завершення локальної перевірки."),
        (DesktopTextKey.NotChecked, "Ще не перевірено"),
        (DesktopTextKey.Checking, "Перевірка…"),
        (DesktopTextKey.SearchBmp, "Пошук GDB-інтерфейсу BMP"),
        (DesktopTextKey.SearchGdb, "Пошук arm-none-eabi-gdb"),
        (DesktopTextKey.SearchSignedCatalog, "Пошук локального підписаного каталогу"),
        (DesktopTextKey.CatalogNotLoaded, "Каталог ще не завантажено."),
        (DesktopTextKey.LogNotCreated, "Журнал ще не створено"),
        (DesktopTextKey.LogShippingEnabled, "Увімкнено в конфігурації"),
        (DesktopTextKey.LogShippingDisabled, "Вимкнено в конфігурації"),
        (DesktopTextKey.BatchEnabled, "Увімкнено — ідентифікатор буде обов’язковим"),
        (DesktopTextKey.BatchDisabled, "Вимкнено — локальне блокування партій не застосовується"),
        (DesktopTextKey.Connected, "Підключено"),
        (DesktopTextKey.SerialNumber, "{0} · серійний № {1}"),
        (DesktopTextKey.BlockedProbes, "Заблоковано: зондів — {0}"),
        (DesktopTextKey.LeaveOneBmp, "Залиште підключеним рівно один BMP: {0}"),
        (DesktopTextKey.PortWithSerial, "{0} (серійний № {1})"),
        (DesktopTextKey.MultipleBmpIssue, "кілька BMP"),
        (DesktopTextKey.SearchError, "Помилка пошуку"),
        (DesktopTextKey.NotFound, "Не знайдено"),
        (DesktopTextKey.MacAutoDiscovery, "Автопошук macOS ще мігрує; явний /dev/cu.usbmodem… підтримується Core."),
        (DesktopTextKey.BmpHelp, "Підключіть BMP і перевірте USB-кабель та права доступу до порту."),
        (DesktopTextKey.BmpIssue, "BMP"),
        (DesktopTextKey.Found, "Знайдено"),
        (DesktopTextKey.GdbHelp, "Встановіть Arm GNU Toolchain або вкажіть чинний шлях у налаштуваннях."),
        (DesktopTextKey.GdbIssue, "ARM GDB"),
        (DesktopTextKey.SignatureVerified, "Підпис перевірено"),
        (DesktopTextKey.LabMode, "Лабораторний режим"),
        (DesktopTextKey.CatalogProductDetail, "Виробів: {0} · {1}"),
        (DesktopTextKey.CatalogOverview, "Каталог згенеровано {0}; виробів: {1}; відкликань: {2}."),
        (DesktopTextKey.CatalogIssue, "каталог"),
        (DesktopTextKey.CatalogRejected, "Відхилено"),
        (DesktopTextKey.CatalogError, "Помилка каталогу"),
        (DesktopTextKey.CatalogNotReady, "Каталог не готовий."),
        (DesktopTextKey.FileFound, "Знайдено · {0}"),
        (DesktopTextKey.FileCreateLater, "Файл буде створено після першої спроби прошивання"),
        (DesktopTextKey.StationReady, "Станція готова за базовими перевірками"),
        (DesktopTextKey.StationPartial, "Готовність станції: {0}/3"),
        (DesktopTextKey.StationReadyDetail, "BMP, ARM GDB і підписаний каталог доступні. Апаратну (HIL) приймальну перевірку кросплатформного інтерфейсу ще не завершено."),
        (DesktopTextKey.Attention, "Потрібна увага: {0}."),
        (DesktopTextKey.CheckedAt, "Перевірено {0}"),
        (DesktopTextKey.Megabytes, "{0} МБ"),
        (DesktopTextKey.Kilobytes, "{0} КБ"),
        (DesktopTextKey.Bytes, "{0} Б"),
        (DesktopTextKey.TargetSummary, "{0} · {1} КБ"),
        (DesktopTextKey.ReleaseSummary, "v{0} · релізів: {1}"),
    ];

    private static readonly (DesktopTextKey Key, string Value)[] EnglishCore =
    [
        (DesktopTextKey.WindowTitle, "Iskra — flashing station"),
        (DesktopTextKey.Tagline, "Safe device flashing and verification"),
        (DesktopTextKey.TabFlash, "Flash"),
        (DesktopTextKey.Refresh, "Check again"),
        (DesktopTextKey.SignedCatalog, "SIGNED CATALOG"),
        (DesktopTextKey.OperatorChange, "Operator change"),
        (DesktopTextKey.Operator, "OPERATOR"),
        (DesktopTextKey.OperatorPlaceholder, "Operator name"),
        (DesktopTextKey.Product, "PRODUCT"),
        (DesktopTextKey.Batch, "BATCH"),
        (DesktopTextKey.BatchPlaceholder, "Batch ID"),
        (DesktopTextKey.Version, "VERSION"),
        (DesktopTextKey.FlashAction, "FLASH"),
        (DesktopTextKey.GdbDetails, "gdb details"),
        (DesktopTextKey.GdbLogEmpty, "gdb output appears here once flashing starts."),
        (DesktopTextKey.FlashReady, "Ready to flash"),
        (DesktopTextKey.FlashReadyBatchOn, "Press FLASH. The batch locks on the first attempt."),
        (DesktopTextKey.FlashReadyBatchOff, "Press FLASH. Batch mode is disabled."),
        (DesktopTextKey.FlashDownloading, "Downloading firmware…"),
        (DesktopTextKey.FlashRunning, "Flashing…"),
        (DesktopTextKey.FlashPass, "✓ PASS"),
        (DesktopTextKey.FlashDuration, "Completed in {0} ms"),
        (DesktopTextKey.ErrorDetails, "error details"),
        (DesktopTextKey.ReadyProbe, "Connect the Black Magic Probe"),
        (DesktopTextKey.ReadyProbeDetail, "Exactly one probe is required. Check the USB cable and press Refresh."),
        (DesktopTextKey.ReadyGdb, "arm-none-eabi-gdb was not found"),
        (DesktopTextKey.ReadyGdbDetail, "Install Arm GNU Toolchain or set the path in Settings."),
        (DesktopTextKey.ReadyCatalog, "The catalog is not loaded"),
        (DesktopTextKey.ReadyCatalogDetail, "Check the catalog path and its signature in Settings."),
        (DesktopTextKey.ReadyOperator, "Enter the operator name"),
        (DesktopTextKey.ReadyOperatorDetail, "The name is written to the log of every attempt."),
        (DesktopTextKey.ReadyBatch, "Enter the batch ID"),
        (DesktopTextKey.ReadyBatchDetail, "Batch mode is enabled in Settings."),
        (DesktopTextKey.ReadySelection, "Select a product and version"),
        (DesktopTextKey.BatchLocked, "Batch locked to {0} v{1} · sha256 {2}"),
        (DesktopTextKey.AuthHintRemote, "This release downloads from GitHub. Sign in on this station through the WPF app or with \"Iskra.Cli --login\"."),
        (DesktopTextKey.HotkeyHint, "Hotkey: {0}"),
        (DesktopTextKey.HotkeyTooltip, "Starts flashing. Hotkey: {0}"),
        (DesktopTextKey.HotkeySpace, "Space"),
        (DesktopTextKey.TabHistory, "History"),
        (DesktopTextKey.LocalLog, "Local log"),
        (DesktopTextKey.HistoryMigration, "SQLite remains the local source of truth. Attempts made from this frontend are written to the same database as WPF; CSV export migrates next."),
        (DesktopTextKey.FileStatus, "FILE STATUS"),
        (DesktopTextKey.TabCatalog, "Catalog"),
        (DesktopTextKey.AvailableProducts, "Available products"),
        (DesktopTextKey.Target, "TARGET"),
        (DesktopTextKey.DefaultRelease, "DEFAULT RELEASE"),
        (DesktopTextKey.TabSettings, "Settings"),
        (DesktopTextKey.CurrentConfiguration, "Current station configuration"),
        (DesktopTextKey.SettingsMigration, "In the first stage, the cross-platform interface reads the current safe settings without duplicating them. Editing with automatic saving will be connected to the shared settings workflow in the next step."),
        (DesktopTextKey.Station, "STATION"),
        (DesktopTextKey.SettingsFile, "SETTINGS FILE"),
        (DesktopTextKey.OfficialCatalog, "OFFICIAL CATALOG"),
        (DesktopTextKey.CloudLog, "CLOUD LOG"),
        (DesktopTextKey.PrivateStationKey, "STATION PRIVATE KEY (.PEM)"),
        (DesktopTextKey.BatchMode, "BATCH MODE"),
        (DesktopTextKey.SettingsGroupingMigration, "Batch controls and the log-key path will be grouped here in the next UI slice."),
        (DesktopTextKey.Language, "LANGUAGE"),
        (DesktopTextKey.LanguageSaveFailed, "Could not save the language"),
        (DesktopTextKey.CheckingStation, "Checking station…"),
        (DesktopTextKey.WaitLocalCheck, "Please wait for the local check to finish."),
        (DesktopTextKey.NotChecked, "Not checked yet"),
        (DesktopTextKey.Checking, "Checking…"),
        (DesktopTextKey.SearchBmp, "Looking for the BMP GDB interface"),
        (DesktopTextKey.SearchGdb, "Looking for arm-none-eabi-gdb"),
        (DesktopTextKey.SearchSignedCatalog, "Looking for a local signed catalog"),
        (DesktopTextKey.CatalogNotLoaded, "The catalog has not been loaded yet."),
        (DesktopTextKey.LogNotCreated, "The log has not been created yet"),
        (DesktopTextKey.LogShippingEnabled, "Enabled in the configuration"),
        (DesktopTextKey.LogShippingDisabled, "Disabled in the configuration"),
        (DesktopTextKey.BatchEnabled, "Enabled — an identifier will be required"),
        (DesktopTextKey.BatchDisabled, "Disabled — local batch locking is not applied"),
        (DesktopTextKey.Connected, "Connected"),
        (DesktopTextKey.SerialNumber, "{0} · serial no. {1}"),
        (DesktopTextKey.BlockedProbes, "Blocked: {0} probes"),
        (DesktopTextKey.LeaveOneBmp, "Leave exactly one BMP connected: {0}"),
        (DesktopTextKey.PortWithSerial, "{0} (serial no. {1})"),
        (DesktopTextKey.MultipleBmpIssue, "multiple BMPs"),
        (DesktopTextKey.SearchError, "Discovery error"),
        (DesktopTextKey.NotFound, "Not found"),
        (DesktopTextKey.MacAutoDiscovery, "macOS auto-discovery is still being migrated; an explicit /dev/cu.usbmodem… path is supported by Core."),
        (DesktopTextKey.BmpHelp, "Connect the BMP and check the USB cable and port permissions."),
        (DesktopTextKey.BmpIssue, "BMP"),
        (DesktopTextKey.Found, "Found"),
        (DesktopTextKey.GdbHelp, "Install Arm GNU Toolchain or specify a valid path in Settings."),
        (DesktopTextKey.GdbIssue, "ARM GDB"),
        (DesktopTextKey.SignatureVerified, "Signature verified"),
        (DesktopTextKey.LabMode, "Lab mode"),
        (DesktopTextKey.CatalogProductDetail, "Products: {0} · {1}"),
        (DesktopTextKey.CatalogOverview, "Catalog generated {0}; products: {1}; revocations: {2}."),
        (DesktopTextKey.CatalogIssue, "catalog"),
        (DesktopTextKey.CatalogRejected, "Rejected"),
        (DesktopTextKey.CatalogError, "Catalog error"),
        (DesktopTextKey.CatalogNotReady, "The catalog is not ready."),
        (DesktopTextKey.FileFound, "Found · {0}"),
        (DesktopTextKey.FileCreateLater, "The file will be created after the first flashing attempt"),
        (DesktopTextKey.StationReady, "Station ready by basic checks"),
        (DesktopTextKey.StationPartial, "Station readiness: {0}/3"),
        (DesktopTextKey.StationReadyDetail, "BMP, ARM GDB, and the signed catalog are available. Hardware-in-the-loop acceptance of the cross-platform UI is still outstanding."),
        (DesktopTextKey.Attention, "Needs attention: {0}."),
        (DesktopTextKey.CheckedAt, "Checked {0}"),
        (DesktopTextKey.Megabytes, "{0} MB"),
        (DesktopTextKey.Kilobytes, "{0} KB"),
        (DesktopTextKey.Bytes, "{0} B"),
        (DesktopTextKey.TargetSummary, "{0} · {1} KB"),
        (DesktopTextKey.ReleaseSummary, "v{0} · releases: {1}"),
    ];

    private static readonly (DesktopTextKey Key, string Value)[] GermanCore =
    [
        (DesktopTextKey.WindowTitle, "Iskra — Flash-Station"),
        (DesktopTextKey.Tagline, "Sicheres Flashen und Prüfen von Geräten"),
        (DesktopTextKey.TabFlash, "Flashen"),
        (DesktopTextKey.Refresh, "Erneut prüfen"),
        (DesktopTextKey.SignedCatalog, "SIGNIERTER KATALOG"),
        (DesktopTextKey.OperatorChange, "Bedienerwechsel"),
        (DesktopTextKey.Operator, "BEDIENER"),
        (DesktopTextKey.OperatorPlaceholder, "Name des Bedieners"),
        (DesktopTextKey.Product, "PRODUKT"),
        (DesktopTextKey.Batch, "CHARGE"),
        (DesktopTextKey.BatchPlaceholder, "Chargen-ID"),
        (DesktopTextKey.Version, "VERSION"),
        (DesktopTextKey.FlashAction, "FLASHEN"),
        (DesktopTextKey.GdbDetails, "gdb-Details"),
        (DesktopTextKey.GdbLogEmpty, "Die gdb-Ausgabe erscheint hier, sobald das Flashen startet."),
        (DesktopTextKey.FlashReady, "Bereit zum Flashen"),
        (DesktopTextKey.FlashReadyBatchOn, "FLASHEN drücken. Die Charge wird beim ersten Versuch festgelegt."),
        (DesktopTextKey.FlashReadyBatchOff, "FLASHEN drücken. Der Chargenmodus ist deaktiviert."),
        (DesktopTextKey.FlashDownloading, "Firmware wird geladen…"),
        (DesktopTextKey.FlashRunning, "Flashen läuft…"),
        (DesktopTextKey.FlashPass, "✓ BESTANDEN"),
        (DesktopTextKey.FlashDuration, "Abgeschlossen in {0} ms"),
        (DesktopTextKey.ErrorDetails, "Fehlerdetails"),
        (DesktopTextKey.ReadyProbe, "Black Magic Probe anschließen"),
        (DesktopTextKey.ReadyProbeDetail, "Genau eine Sonde ist erforderlich. USB-Kabel prüfen und «Erneut prüfen» drücken."),
        (DesktopTextKey.ReadyGdb, "arm-none-eabi-gdb nicht gefunden"),
        (DesktopTextKey.ReadyGdbDetail, "Arm GNU Toolchain installieren oder den Pfad in den Einstellungen setzen."),
        (DesktopTextKey.ReadyCatalog, "Katalog nicht geladen"),
        (DesktopTextKey.ReadyCatalogDetail, "Katalogpfad und Signatur in den Einstellungen prüfen."),
        (DesktopTextKey.ReadyOperator, "Bedienername eingeben"),
        (DesktopTextKey.ReadyOperatorDetail, "Der Name wird bei jedem Versuch protokolliert."),
        (DesktopTextKey.ReadyBatch, "Chargen-ID eingeben"),
        (DesktopTextKey.ReadyBatchDetail, "Der Chargenmodus ist in den Einstellungen aktiviert."),
        (DesktopTextKey.ReadySelection, "Produkt und Version wählen"),
        (DesktopTextKey.BatchLocked, "Charge festgelegt auf {0} v{1} · sha256 {2}"),
        (DesktopTextKey.AuthHintRemote, "Dieses Release wird von GitHub geladen. Melden Sie sich an dieser Station über die WPF-Anwendung oder mit «Iskra.Cli --login» an."),
        (DesktopTextKey.HotkeyHint, "Tastenkürzel: {0}"),
        (DesktopTextKey.HotkeyTooltip, "Startet das Flashen. Tastenkürzel: {0}"),
        (DesktopTextKey.HotkeySpace, "Leertaste"),
        (DesktopTextKey.TabHistory, "Verlauf"),
        (DesktopTextKey.LocalLog, "Lokales Protokoll"),
        (DesktopTextKey.HistoryMigration, "SQLite bleibt die lokale Quelle der Wahrheit. Versuche aus dieser Oberfläche werden in dieselbe Datenbank wie bei WPF geschrieben; der CSV-Export folgt."),
        (DesktopTextKey.FileStatus, "DATEISTATUS"),
        (DesktopTextKey.TabCatalog, "Katalog"),
        (DesktopTextKey.AvailableProducts, "Verfügbare Produkte"),
        (DesktopTextKey.Target, "ZIEL"),
        (DesktopTextKey.DefaultRelease, "STANDARD-RELEASE"),
        (DesktopTextKey.TabSettings, "Einstellungen"),
        (DesktopTextKey.CurrentConfiguration, "Aktuelle Stationskonfiguration"),
        (DesktopTextKey.SettingsMigration, "Im ersten Schritt liest die plattformübergreifende Oberfläche die vorhandenen sicheren Einstellungen, ohne sie zu duplizieren. Die Bearbeitung mit automatischem Speichern wird im nächsten Schritt an den gemeinsamen Einstellungsablauf angebunden."),
        (DesktopTextKey.Station, "STATION"),
        (DesktopTextKey.SettingsFile, "EINSTELLUNGSDATEI"),
        (DesktopTextKey.OfficialCatalog, "OFFIZIELLER KATALOG"),
        (DesktopTextKey.CloudLog, "CLOUD-PROTOKOLL"),
        (DesktopTextKey.PrivateStationKey, "PRIVATER STATIONSSCHLÜSSEL (.PEM)"),
        (DesktopTextKey.BatchMode, "CHARGENMODUS"),
        (DesktopTextKey.SettingsGroupingMigration, "Chargensteuerung und Pfad zum Protokollschlüssel werden im nächsten UI-Schritt hier zusammengefasst."),
        (DesktopTextKey.Language, "SPRACHE"),
        (DesktopTextKey.LanguageSaveFailed, "Sprache konnte nicht gespeichert werden"),
        (DesktopTextKey.CheckingStation, "Station wird geprüft…"),
        (DesktopTextKey.WaitLocalCheck, "Bitte warten Sie, bis die lokale Prüfung abgeschlossen ist."),
        (DesktopTextKey.NotChecked, "Noch nicht geprüft"),
        (DesktopTextKey.Checking, "Prüfung…"),
        (DesktopTextKey.SearchBmp, "BMP-GDB-Schnittstelle wird gesucht"),
        (DesktopTextKey.SearchGdb, "arm-none-eabi-gdb wird gesucht"),
        (DesktopTextKey.SearchSignedCatalog, "Lokaler signierter Katalog wird gesucht"),
        (DesktopTextKey.CatalogNotLoaded, "Der Katalog wurde noch nicht geladen."),
        (DesktopTextKey.LogNotCreated, "Das Protokoll wurde noch nicht erstellt"),
        (DesktopTextKey.LogShippingEnabled, "In der Konfiguration aktiviert"),
        (DesktopTextKey.LogShippingDisabled, "In der Konfiguration deaktiviert"),
        (DesktopTextKey.BatchEnabled, "Aktiviert — eine Kennung ist erforderlich"),
        (DesktopTextKey.BatchDisabled, "Deaktiviert — lokale Chargensperre wird nicht angewendet"),
        (DesktopTextKey.Connected, "Verbunden"),
        (DesktopTextKey.SerialNumber, "{0} · Seriennr. {1}"),
        (DesktopTextKey.BlockedProbes, "Gesperrt: {0} Sonden"),
        (DesktopTextKey.LeaveOneBmp, "Lassen Sie genau einen BMP angeschlossen: {0}"),
        (DesktopTextKey.PortWithSerial, "{0} (Seriennr. {1})"),
        (DesktopTextKey.MultipleBmpIssue, "mehrere BMPs"),
        (DesktopTextKey.SearchError, "Suchfehler"),
        (DesktopTextKey.NotFound, "Nicht gefunden"),
        (DesktopTextKey.MacAutoDiscovery, "Die automatische macOS-Suche wird noch migriert; ein expliziter /dev/cu.usbmodem…-Pfad wird von Core unterstützt."),
        (DesktopTextKey.BmpHelp, "Schließen Sie den BMP an und prüfen Sie USB-Kabel und Portberechtigungen."),
        (DesktopTextKey.BmpIssue, "BMP"),
        (DesktopTextKey.Found, "Gefunden"),
        (DesktopTextKey.GdbHelp, "Installieren Sie die Arm GNU Toolchain oder geben Sie unter Einstellungen einen gültigen Pfad an."),
        (DesktopTextKey.GdbIssue, "ARM GDB"),
        (DesktopTextKey.SignatureVerified, "Signatur geprüft"),
        (DesktopTextKey.LabMode, "Labormodus"),
        (DesktopTextKey.CatalogProductDetail, "Produkte: {0} · {1}"),
        (DesktopTextKey.CatalogOverview, "Katalog erstellt: {0}; Produkte: {1}; Widerrufe: {2}."),
        (DesktopTextKey.CatalogIssue, "Katalog"),
        (DesktopTextKey.CatalogRejected, "Abgelehnt"),
        (DesktopTextKey.CatalogError, "Katalogfehler"),
        (DesktopTextKey.CatalogNotReady, "Der Katalog ist nicht bereit."),
        (DesktopTextKey.FileFound, "Gefunden · {0}"),
        (DesktopTextKey.FileCreateLater, "Die Datei wird nach dem ersten Flash-Versuch erstellt"),
        (DesktopTextKey.StationReady, "Station nach Basisprüfungen bereit"),
        (DesktopTextKey.StationPartial, "Stationsbereitschaft: {0}/3"),
        (DesktopTextKey.StationReadyDetail, "BMP, ARM GDB und der signierte Katalog sind verfügbar. Die Hardware-in-the-Loop-Abnahme der plattformübergreifenden Oberfläche steht noch aus."),
        (DesktopTextKey.Attention, "Eingriff erforderlich: {0}."),
        (DesktopTextKey.CheckedAt, "Geprüft {0}"),
        (DesktopTextKey.Megabytes, "{0} MB"),
        (DesktopTextKey.Kilobytes, "{0} KB"),
        (DesktopTextKey.Bytes, "{0} B"),
        (DesktopTextKey.TargetSummary, "{0} · {1} KB"),
        (DesktopTextKey.ReleaseSummary, "v{0} · Releases: {1}"),
    ];

    private static IReadOnlyDictionary<DesktopTextKey, string> CreateCatalog(
        IEnumerable<(DesktopTextKey Key, string Value)> entries)
    {
        var catalog = entries.ToDictionary(entry => entry.Key, entry => entry.Value);
        var missing = Enum.GetValues<DesktopTextKey>().Where(key => !catalog.ContainsKey(key)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Incomplete desktop text catalog: {string.Join(", ", missing)}");
        }

        return catalog;
    }
}

public sealed class DesktopText
{
    private readonly IReadOnlyDictionary<DesktopTextKey, string> _values;

    internal DesktopText(string languageCode, IReadOnlyDictionary<DesktopTextKey, string> values)
    {
        LanguageCode = languageCode;
        Culture = IskraLanguages.CultureFor(languageCode);
        _values = values;
    }

    public string LanguageCode { get; }
    public CultureInfo Culture { get; }

    public string WindowTitle => Get(DesktopTextKey.WindowTitle);
    public string Tagline => Get(DesktopTextKey.Tagline);
    public string TabFlash => Get(DesktopTextKey.TabFlash);
    public string Refresh => Get(DesktopTextKey.Refresh);
    public string SignedCatalog => Get(DesktopTextKey.SignedCatalog);
    public string OperatorChange => Get(DesktopTextKey.OperatorChange);
    public string Operator => Get(DesktopTextKey.Operator);
    public string OperatorPlaceholder => Get(DesktopTextKey.OperatorPlaceholder);
    public string Product => Get(DesktopTextKey.Product);
    public string Batch => Get(DesktopTextKey.Batch);
    public string BatchPlaceholder => Get(DesktopTextKey.BatchPlaceholder);
    public string Version => Get(DesktopTextKey.Version);
    public string FlashAction => Get(DesktopTextKey.FlashAction);
    public string GdbDetails => Get(DesktopTextKey.GdbDetails);
    public string GdbLogEmpty => Get(DesktopTextKey.GdbLogEmpty);
    public string FlashReady => Get(DesktopTextKey.FlashReady);
    public string FlashReadyBatchOn => Get(DesktopTextKey.FlashReadyBatchOn);
    public string FlashReadyBatchOff => Get(DesktopTextKey.FlashReadyBatchOff);
    public string FlashDownloading => Get(DesktopTextKey.FlashDownloading);
    public string FlashRunning => Get(DesktopTextKey.FlashRunning);
    public string FlashPass => Get(DesktopTextKey.FlashPass);
    public string ErrorDetails => Get(DesktopTextKey.ErrorDetails);
    public string ReadyProbe => Get(DesktopTextKey.ReadyProbe);
    public string ReadyProbeDetail => Get(DesktopTextKey.ReadyProbeDetail);
    public string ReadyGdb => Get(DesktopTextKey.ReadyGdb);
    public string ReadyGdbDetail => Get(DesktopTextKey.ReadyGdbDetail);
    public string ReadyCatalog => Get(DesktopTextKey.ReadyCatalog);
    public string ReadyCatalogDetail => Get(DesktopTextKey.ReadyCatalogDetail);
    public string ReadyOperator => Get(DesktopTextKey.ReadyOperator);
    public string ReadyOperatorDetail => Get(DesktopTextKey.ReadyOperatorDetail);
    public string ReadyBatch => Get(DesktopTextKey.ReadyBatch);
    public string ReadyBatchDetail => Get(DesktopTextKey.ReadyBatchDetail);
    public string ReadySelection => Get(DesktopTextKey.ReadySelection);
    public string AuthHintRemote => Get(DesktopTextKey.AuthHintRemote);
    public string HotkeySpace => Get(DesktopTextKey.HotkeySpace);
    public string TabHistory => Get(DesktopTextKey.TabHistory);
    public string LocalLog => Get(DesktopTextKey.LocalLog);
    public string HistoryMigration => Get(DesktopTextKey.HistoryMigration);
    public string FileStatus => Get(DesktopTextKey.FileStatus);
    public string TabCatalog => Get(DesktopTextKey.TabCatalog);
    public string AvailableProducts => Get(DesktopTextKey.AvailableProducts);
    public string Target => Get(DesktopTextKey.Target);
    public string DefaultRelease => Get(DesktopTextKey.DefaultRelease);
    public string TabSettings => Get(DesktopTextKey.TabSettings);
    public string CurrentConfiguration => Get(DesktopTextKey.CurrentConfiguration);
    public string SettingsMigration => Get(DesktopTextKey.SettingsMigration);
    public string Station => Get(DesktopTextKey.Station);
    public string SettingsFile => Get(DesktopTextKey.SettingsFile);
    public string OfficialCatalog => Get(DesktopTextKey.OfficialCatalog);
    public string CloudLog => Get(DesktopTextKey.CloudLog);
    public string PrivateStationKey => Get(DesktopTextKey.PrivateStationKey);
    public string BatchMode => Get(DesktopTextKey.BatchMode);
    public string SettingsGroupingMigration => Get(DesktopTextKey.SettingsGroupingMigration);
    public string Language => Get(DesktopTextKey.Language);
    public string LanguageSaveFailed => Get(DesktopTextKey.LanguageSaveFailed);
    public string CheckingStation => Get(DesktopTextKey.CheckingStation);
    public string WaitLocalCheck => Get(DesktopTextKey.WaitLocalCheck);
    public string NotChecked => Get(DesktopTextKey.NotChecked);
    public string Checking => Get(DesktopTextKey.Checking);
    public string SearchBmp => Get(DesktopTextKey.SearchBmp);
    public string SearchGdb => Get(DesktopTextKey.SearchGdb);
    public string SearchSignedCatalog => Get(DesktopTextKey.SearchSignedCatalog);
    public string CatalogNotLoaded => Get(DesktopTextKey.CatalogNotLoaded);
    public string LogNotCreated => Get(DesktopTextKey.LogNotCreated);
    public string LogShippingEnabled => Get(DesktopTextKey.LogShippingEnabled);
    public string LogShippingDisabled => Get(DesktopTextKey.LogShippingDisabled);
    public string BatchEnabled => Get(DesktopTextKey.BatchEnabled);
    public string BatchDisabled => Get(DesktopTextKey.BatchDisabled);
    public string Connected => Get(DesktopTextKey.Connected);
    public string MultipleBmpIssue => Get(DesktopTextKey.MultipleBmpIssue);
    public string SearchError => Get(DesktopTextKey.SearchError);
    public string NotFound => Get(DesktopTextKey.NotFound);
    public string MacAutoDiscovery => Get(DesktopTextKey.MacAutoDiscovery);
    public string BmpHelp => Get(DesktopTextKey.BmpHelp);
    public string BmpIssue => Get(DesktopTextKey.BmpIssue);
    public string Found => Get(DesktopTextKey.Found);
    public string GdbHelp => Get(DesktopTextKey.GdbHelp);
    public string GdbIssue => Get(DesktopTextKey.GdbIssue);
    public string SignatureVerified => Get(DesktopTextKey.SignatureVerified);
    public string LabMode => Get(DesktopTextKey.LabMode);
    public string CatalogIssue => Get(DesktopTextKey.CatalogIssue);
    public string CatalogRejected => Get(DesktopTextKey.CatalogRejected);
    public string CatalogError => Get(DesktopTextKey.CatalogError);
    public string CatalogNotReady => Get(DesktopTextKey.CatalogNotReady);
    public string FileCreateLater => Get(DesktopTextKey.FileCreateLater);
    public string StationReady => Get(DesktopTextKey.StationReady);
    public string StationReadyDetail => Get(DesktopTextKey.StationReadyDetail);

    public string FlashDuration(double milliseconds) =>
        Format(DesktopTextKey.FlashDuration, milliseconds.ToString("F0", Culture));
    public string BatchLocked(string productId, string version, string shortSha) =>
        Format(DesktopTextKey.BatchLocked, productId, version, shortSha);
    public string HotkeyHint(string key) => Format(DesktopTextKey.HotkeyHint, key);
    public string HotkeyTooltip(string key) => Format(DesktopTextKey.HotkeyTooltip, key);
    public string SerialNumber(string port, string serial) => Format(DesktopTextKey.SerialNumber, port, serial);
    public string BlockedProbes(int count) => Format(DesktopTextKey.BlockedProbes, count);
    public string LeaveOneBmp(string probeList) => Format(DesktopTextKey.LeaveOneBmp, probeList);
    public string PortWithSerial(string port, string serial) => Format(DesktopTextKey.PortWithSerial, port, serial);
    public string CatalogProductDetail(int count, string path) => Format(DesktopTextKey.CatalogProductDetail, count, path);
    public string CatalogOverview(string generatedAt, int productCount, int revocationCount) =>
        Format(DesktopTextKey.CatalogOverview, generatedAt, productCount, revocationCount);
    public string FileFound(string size) => Format(DesktopTextKey.FileFound, size);
    public string StationPartial(int readyChecks) => Format(DesktopTextKey.StationPartial, readyChecks);
    public string Attention(string issues) => Format(DesktopTextKey.Attention, issues);
    public string CheckedAt(DateTime time) => Format(DesktopTextKey.CheckedAt, time.ToString("T", Culture));
    public string Megabytes(double value) => Format(DesktopTextKey.Megabytes, value.ToString("F1", Culture));
    public string Kilobytes(double value) => Format(DesktopTextKey.Kilobytes, value.ToString("F1", Culture));
    public string Bytes(long value) => Format(DesktopTextKey.Bytes, value.ToString("N0", Culture));
    public string TargetSummary(string partNumber, int flashKb) => Format(DesktopTextKey.TargetSummary, partNumber, flashKb);
    public string ReleaseSummary(string version, int releaseCount) => Format(DesktopTextKey.ReleaseSummary, version, releaseCount);

    // --- extended catalog ---
    public string Cancel => Get(DesktopTextKey.Cancel);
    public string ActionRefresh => Get(DesktopTextKey.ActionRefresh);
    public string ActionSave => Get(DesktopTextKey.ActionSave);
    public string ActionReset => Get(DesktopTextKey.ActionReset);
    public string ActionBrowse => Get(DesktopTextKey.ActionBrowse);
    public string ActionCheck => Get(DesktopTextKey.ActionCheck);
    public string ActionCheckUpdates => Get(DesktopTextKey.ActionCheckUpdates);
    public string ActionOpenRelease => Get(DesktopTextKey.ActionOpenRelease);
    public string ActionUploadNow => Get(DesktopTextKey.ActionUploadNow);
    public string ActionSignOut => Get(DesktopTextKey.ActionSignOut);
    public string SettingsSectionCatalog => Get(DesktopTextKey.SettingsSectionCatalog);
    public string SettingsCatalogPath => Get(DesktopTextKey.SettingsCatalogPath);
    public string SettingsSignatureRequired => Get(DesktopTextKey.SettingsSignatureRequired);
    public string SettingsSignatureRequiredContent => Get(DesktopTextKey.SettingsSignatureRequiredContent);
    public string SettingsSignatureMandatory => Get(DesktopTextKey.SettingsSignatureMandatory);
    public string SettingsSectionCatalogUpdate => Get(DesktopTextKey.SettingsSectionCatalogUpdate);
    public string SettingsAutoUpdate => Get(DesktopTextKey.SettingsAutoUpdate);
    public string SettingsAutoUpdateContent => Get(DesktopTextKey.SettingsAutoUpdateContent);
    public string SettingsLockedSource => Get(DesktopTextKey.SettingsLockedSource);
    public string SettingsSectionAppUpdate => Get(DesktopTextKey.SettingsSectionAppUpdate);
    public string SettingsCurrentVersion => Get(DesktopTextKey.SettingsCurrentVersion);
    public string SettingsReleases => Get(DesktopTextKey.SettingsReleases);
    public string SettingsStatus => Get(DesktopTextKey.SettingsStatus);
    public string SettingsSectionGitHubAuth => Get(DesktopTextKey.SettingsSectionGitHubAuth);
    public string SettingsSectionDebugger => Get(DesktopTextKey.SettingsSectionDebugger);
    public string SettingsGdbPath => Get(DesktopTextKey.SettingsGdbPath);
    public string SettingsSwdFrequency => Get(DesktopTextKey.SettingsSwdFrequency);
    public string SettingsPower => Get(DesktopTextKey.SettingsPower);
    public string SettingsPowerExternal => Get(DesktopTextKey.SettingsPowerExternal);
    public string SettingsPowerProbe => Get(DesktopTextKey.SettingsPowerProbe);
    public string SettingsConnectReset => Get(DesktopTextKey.SettingsConnectReset);
    public string SettingsConnectResetContent => Get(DesktopTextKey.SettingsConnectResetContent);
    public string SettingsTimeout => Get(DesktopTextKey.SettingsTimeout);
    public string SettingsSectionLogStation => Get(DesktopTextKey.SettingsSectionLogStation);
    public string SettingsLogFile => Get(DesktopTextKey.SettingsLogFile);
    public string SettingsStationId => Get(DesktopTextKey.SettingsStationId);
    public string SettingsSectionOperatorUi => Get(DesktopTextKey.SettingsSectionOperatorUi);
    public string SettingsHotkey => Get(DesktopTextKey.SettingsHotkey);
    public string SettingsSectionCloudLog => Get(DesktopTextKey.SettingsSectionCloudLog);
    public string SettingsCloudAutoUpload => Get(DesktopTextKey.SettingsCloudAutoUpload);
    public string SettingsCloudInterval => Get(DesktopTextKey.SettingsCloudInterval);
    public string SettingsCloudPrivateKey => Get(DesktopTextKey.SettingsCloudPrivateKey);
    public string SettingsBatches => Get(DesktopTextKey.SettingsBatches);
    public string SettingsBatchesContent => Get(DesktopTextKey.SettingsBatchesContent);
    public string SettingsRepository => Get(DesktopTextKey.SettingsRepository);
    public string HotkeyDisabled => Get(DesktopTextKey.HotkeyDisabled);
    public string SettingsAutoSaveNote => Get(DesktopTextKey.SettingsAutoSaveNote);
    public string SettingsSaved => Get(DesktopTextKey.SettingsSaved);
    public string SettingsUnsaved => Get(DesktopTextKey.SettingsUnsaved);
    public string SettingsNoChanges => Get(DesktopTextKey.SettingsNoChanges);
    public string SettingsResetNotice => Get(DesktopTextKey.SettingsResetNotice);
    public string DialogCatalogTitle => Get(DesktopTextKey.DialogCatalogTitle);
    public string DialogFilterCatalog => Get(DesktopTextKey.DialogFilterCatalog);
    public string DialogGdbTitle => Get(DesktopTextKey.DialogGdbTitle);
    public string DialogFilterGdb => Get(DesktopTextKey.DialogFilterGdb);
    public string DialogLogTitle => Get(DesktopTextKey.DialogLogTitle);
    public string DialogFilterSqlite => Get(DesktopTextKey.DialogFilterSqlite);
    public string DialogPemTitle => Get(DesktopTextKey.DialogPemTitle);
    public string DialogFilterPem => Get(DesktopTextKey.DialogFilterPem);
    public string DialogExportTitle => Get(DesktopTextKey.DialogExportTitle);
    public string DialogFilterCsv => Get(DesktopTextKey.DialogFilterCsv);
    public string DialogUnavailable => Get(DesktopTextKey.DialogUnavailable);
    public string AuthSignIn => Get(DesktopTextKey.AuthSignIn);
    public string AuthUnsupportedPlatform => Get(DesktopTextKey.AuthUnsupportedPlatform);
    public string AuthClientMissing => Get(DesktopTextKey.AuthClientMissing);
    public string AuthNotSignedIn => Get(DesktopTextKey.AuthNotSignedIn);
    public string AuthSessionExpired => Get(DesktopTextKey.AuthSessionExpired);
    public string AuthAccessValid => Get(DesktopTextKey.AuthAccessValid);
    public string AuthAccessRefresh => Get(DesktopTextKey.AuthAccessRefresh);
    public string DeviceTitle => Get(DesktopTextKey.DeviceTitle);
    public string DeviceStep1 => Get(DesktopTextKey.DeviceStep1);
    public string DeviceStep2 => Get(DesktopTextKey.DeviceStep2);
    public string DeviceOpenBrowser => Get(DesktopTextKey.DeviceOpenBrowser);
    public string DeviceCopyCode => Get(DesktopTextKey.DeviceCopyCode);
    public string DeviceWaiting => Get(DesktopTextKey.DeviceWaiting);
    public string DeviceCodeCopied => Get(DesktopTextKey.DeviceCodeCopied);
    public string DeviceAccessDenied => Get(DesktopTextKey.DeviceAccessDenied);
    public string DeviceCodeExpired => Get(DesktopTextKey.DeviceCodeExpired);
    public string UpdateChecking => Get(DesktopTextKey.UpdateChecking);
    public string CatalogDefaultBadge => Get(DesktopTextKey.CatalogDefaultBadge);
    public string CatalogRevokedBadge => Get(DesktopTextKey.CatalogRevokedBadge);
    public string CatalogRemoteBadge => Get(DesktopTextKey.CatalogRemoteBadge);
    public string FullScreenTooltip => Get(DesktopTextKey.FullScreenTooltip);
    public string CatalogNoRelease => Get(DesktopTextKey.CatalogNoRelease);
    public string CatalogNetworkError => Get(DesktopTextKey.CatalogNetworkError);
    public string CatalogBadSignature => Get(DesktopTextKey.CatalogBadSignature);
    public string CatalogAssetsMissing => Get(DesktopTextKey.CatalogAssetsMissing);
    public string CatalogParseError => Get(DesktopTextKey.CatalogParseError);
    public string CatalogSourceNotAllowed => Get(DesktopTextKey.CatalogSourceNotAllowed);
    public string CatalogRollbackRejected => Get(DesktopTextKey.CatalogRollbackRejected);
    public string AppUpToDate => Get(DesktopTextKey.AppUpToDate);
    public string AppNoRelease => Get(DesktopTextKey.AppNoRelease);
    public string AppUpdateParseError => Get(DesktopTextKey.AppUpdateParseError);
    public string CloudDisabledShort => Get(DesktopTextKey.CloudDisabledShort);
    public string CloudDisabledDetail => Get(DesktopTextKey.CloudDisabledDetail);
    public string CloudUnconfiguredShort => Get(DesktopTextKey.CloudUnconfiguredShort);
    public string CloudUnconfiguredDetail => Get(DesktopTextKey.CloudUnconfiguredDetail);
    public string CloudEmptyShort => Get(DesktopTextKey.CloudEmptyShort);
    public string CloudEmptyDetail => Get(DesktopTextKey.CloudEmptyDetail);
    public string CloudSyncedShort => Get(DesktopTextKey.CloudSyncedShort);
    public string CloudErrorShort => Get(DesktopTextKey.CloudErrorShort);
    public string CloudUploadedAll => Get(DesktopTextKey.CloudUploadedAll);
    public string CloudEnableFirst => Get(DesktopTextKey.CloudEnableFirst);
    public string CloudUploading => Get(DesktopTextKey.CloudUploading);
    public string HistoryExportBatch => Get(DesktopTextKey.HistoryExportBatch);
    public string HistoryExportAll => Get(DesktopTextKey.HistoryExportAll);
    public string ExportBatchesDisabled => Get(DesktopTextKey.ExportBatchesDisabled);
    public string ExportBatchRequired => Get(DesktopTextKey.ExportBatchRequired);
    public string ExportNoDatabase => Get(DesktopTextKey.ExportNoDatabase);

    public string SettingsInvalidField(string field) => Format(DesktopTextKey.SettingsInvalidField, field);
    public string SettingsSaveFailed(string diagnostic) => Format(DesktopTextKey.SettingsSaveFailed, diagnostic);
    public string AuthSignedIn(string access, string until) => Format(DesktopTextKey.AuthSignedIn, access, until);
    public string AuthTokenCorrupt(string message) => Format(DesktopTextKey.AuthTokenCorrupt, message);
    public string AuthDeviceCodeFailed(string message) => Format(DesktopTextKey.AuthDeviceCodeFailed, message);
    public string AuthSaveFailed(string message) => Format(DesktopTextKey.AuthSaveFailed, message);
    public string AuthDeleteFailed(string message) => Format(DesktopTextKey.AuthDeleteFailed, message);
    public string AuthRefreshFailed(string message) => Format(DesktopTextKey.AuthRefreshFailed, message);
    public string DeviceCopyFailed(string message) => Format(DesktopTextKey.DeviceCopyFailed, message);
    public string DeviceBrowserFailed(string message) => Format(DesktopTextKey.DeviceBrowserFailed, message);
    public string DeviceError(string message) => Format(DesktopTextKey.DeviceError, message);
    public string CatalogUpdated(string tag) => Format(DesktopTextKey.CatalogUpdated, tag);
    public string CatalogUpdateAvailable(string tag) => Format(DesktopTextKey.CatalogUpdateAvailable, tag);
    public string TargetDetail(string bmpMatch, string partNumber, int flashKb, string flashOrigin) =>
        Format(DesktopTextKey.TargetDetail, bmpMatch, partNumber, flashKb, flashOrigin);
    public string CatalogUpToDate(string tag) => Format(DesktopTextKey.CatalogUpToDate, tag);
    public string AppUpdateAvailable(string version) => Format(DesktopTextKey.AppUpdateAvailable, version);
    public string CloudQueuedShort(int pending) => Format(DesktopTextKey.CloudQueuedShort, pending);
    public string CloudRowsWaiting(int pending) => Format(DesktopTextKey.CloudRowsWaiting, pending);
    public string CloudKeyMissing(string path) => Format(DesktopTextKey.CloudKeyMissing, path);
    public string CloudUploadReport(int rows, int created, int updated, int leftover) =>
        Format(DesktopTextKey.CloudUploadReport, rows, created, updated, leftover);
    public string RecentAttempts(int count) => Format(DesktopTextKey.RecentAttempts, count);
    public string HistoryBatchEmpty(string batchId) => Format(DesktopTextKey.HistoryBatchEmpty, batchId);
    public string HistoryBatchCounts(string batchId, int total, int pass, int fail, double passRate) =>
        Format(DesktopTextKey.HistoryBatchCounts, batchId, total, pass, fail, passRate.ToString("P1", Culture));
    public string HistoryNeedBatch(int rows) => Format(DesktopTextKey.HistoryNeedBatch, rows);
    public string HistoryNoBatches(int rows) => Format(DesktopTextKey.HistoryNoBatches, rows);
    public string ExportDone(int rows, string path) => Format(DesktopTextKey.ExportDone, rows, path);

    private string Get(DesktopTextKey key) => _values[key];
    private string Format(DesktopTextKey key, params object[] arguments) =>
        string.Format(Culture, Get(key), arguments);
}

internal enum DesktopTextKey
{
    WindowTitle,
    Tagline,
    TabFlash,
    Refresh,
    SignedCatalog,
    OperatorChange,
    Operator,
    OperatorPlaceholder,
    Product,
    Batch,
    BatchPlaceholder,
    Version,
    FlashAction,
    GdbDetails,
    GdbLogEmpty,
    FlashReady,
    FlashReadyBatchOn,
    FlashReadyBatchOff,
    FlashDownloading,
    FlashRunning,
    FlashPass,
    FlashDuration,
    ErrorDetails,
    ReadyProbe,
    ReadyProbeDetail,
    ReadyGdb,
    ReadyGdbDetail,
    ReadyCatalog,
    ReadyCatalogDetail,
    ReadyOperator,
    ReadyOperatorDetail,
    ReadyBatch,
    ReadyBatchDetail,
    ReadySelection,
    BatchLocked,
    AuthHintRemote,
    HotkeyHint,
    HotkeyTooltip,
    HotkeySpace,
    TabHistory,
    LocalLog,
    HistoryMigration,
    FileStatus,
    TabCatalog,
    AvailableProducts,
    Target,
    DefaultRelease,
    TabSettings,
    CurrentConfiguration,
    SettingsMigration,
    Station,
    SettingsFile,
    OfficialCatalog,
    CloudLog,
    PrivateStationKey,
    BatchMode,
    SettingsGroupingMigration,
    Language,
    LanguageSaveFailed,
    CheckingStation,
    WaitLocalCheck,
    NotChecked,
    Checking,
    SearchBmp,
    SearchGdb,
    SearchSignedCatalog,
    CatalogNotLoaded,
    LogNotCreated,
    LogShippingEnabled,
    LogShippingDisabled,
    BatchEnabled,
    BatchDisabled,
    Connected,
    SerialNumber,
    BlockedProbes,
    LeaveOneBmp,
    PortWithSerial,
    MultipleBmpIssue,
    SearchError,
    NotFound,
    MacAutoDiscovery,
    BmpHelp,
    BmpIssue,
    Found,
    GdbHelp,
    GdbIssue,
    SignatureVerified,
    LabMode,
    CatalogProductDetail,
    CatalogOverview,
    CatalogIssue,
    CatalogRejected,
    CatalogError,
    CatalogNotReady,
    FileFound,
    FileCreateLater,
    StationReady,
    StationPartial,
    StationReadyDetail,
    Attention,
    CheckedAt,
    Megabytes,
    Kilobytes,
    Bytes,
    TargetSummary,
    ReleaseSummary,

    // --- extended catalog (DesktopLocalization.Extended.cs) ---
    Cancel,
    ActionRefresh,
    ActionSave,
    ActionReset,
    ActionBrowse,
    ActionCheck,
    ActionCheckUpdates,
    ActionOpenRelease,
    ActionUploadNow,
    ActionSignOut,
    SettingsSectionCatalog,
    SettingsCatalogPath,
    SettingsSignatureRequired,
    SettingsSignatureRequiredContent,
    SettingsSignatureMandatory,
    SettingsSectionCatalogUpdate,
    SettingsAutoUpdate,
    SettingsAutoUpdateContent,
    SettingsLockedSource,
    SettingsSectionAppUpdate,
    SettingsCurrentVersion,
    SettingsReleases,
    SettingsStatus,
    SettingsSectionGitHubAuth,
    SettingsSectionDebugger,
    SettingsGdbPath,
    SettingsSwdFrequency,
    SettingsPower,
    SettingsPowerExternal,
    SettingsPowerProbe,
    SettingsConnectReset,
    SettingsConnectResetContent,
    SettingsTimeout,
    SettingsSectionLogStation,
    SettingsLogFile,
    SettingsStationId,
    SettingsSectionOperatorUi,
    SettingsHotkey,
    SettingsSectionCloudLog,
    SettingsCloudAutoUpload,
    SettingsCloudInterval,
    SettingsCloudPrivateKey,
    SettingsBatches,
    SettingsBatchesContent,
    SettingsRepository,
    HotkeyDisabled,
    SettingsAutoSaveNote,
    SettingsSaved,
    SettingsUnsaved,
    SettingsNoChanges,
    SettingsResetNotice,
    SettingsInvalidField,
    SettingsSaveFailed,
    DialogCatalogTitle,
    DialogFilterCatalog,
    DialogGdbTitle,
    DialogFilterGdb,
    DialogLogTitle,
    DialogFilterSqlite,
    DialogPemTitle,
    DialogFilterPem,
    DialogExportTitle,
    DialogFilterCsv,
    DialogUnavailable,
    AuthSignIn,
    AuthUnsupportedPlatform,
    AuthClientMissing,
    AuthNotSignedIn,
    AuthSessionExpired,
    AuthAccessValid,
    AuthAccessRefresh,
    AuthSignedIn,
    AuthTokenCorrupt,
    AuthDeviceCodeFailed,
    AuthSaveFailed,
    AuthDeleteFailed,
    AuthRefreshFailed,
    DeviceTitle,
    DeviceStep1,
    DeviceStep2,
    DeviceOpenBrowser,
    DeviceCopyCode,
    DeviceWaiting,
    DeviceCodeCopied,
    DeviceCopyFailed,
    DeviceBrowserFailed,
    DeviceAccessDenied,
    DeviceCodeExpired,
    DeviceError,
    UpdateChecking,
    CatalogUpdated,
    CatalogUpdateAvailable,
    CatalogUpToDate,
    TargetDetail,
    CatalogDefaultBadge,
    CatalogRevokedBadge,
    CatalogRemoteBadge,
    FullScreenTooltip,
    CatalogNoRelease,
    CatalogNetworkError,
    CatalogBadSignature,
    CatalogAssetsMissing,
    CatalogParseError,
    CatalogSourceNotAllowed,
    CatalogRollbackRejected,
    AppUpdateAvailable,
    AppUpToDate,
    AppNoRelease,
    AppUpdateParseError,
    CloudDisabledShort,
    CloudDisabledDetail,
    CloudUnconfiguredShort,
    CloudUnconfiguredDetail,
    CloudEmptyShort,
    CloudEmptyDetail,
    CloudSyncedShort,
    CloudQueuedShort,
    CloudErrorShort,
    CloudUploadedAll,
    CloudRowsWaiting,
    CloudEnableFirst,
    CloudKeyMissing,
    CloudUploading,
    CloudUploadReport,
    HistoryExportBatch,
    HistoryExportAll,
    RecentAttempts,
    HistoryBatchEmpty,
    HistoryBatchCounts,
    HistoryNeedBatch,
    HistoryNoBatches,
    ExportDone,
    ExportBatchesDisabled,
    ExportBatchRequired,
    ExportNoDatabase,
}
