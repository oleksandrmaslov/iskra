using System.Globalization;
using Iskra.Core;

namespace Iskra.Desktop;

public sealed record LanguageOption(string Code, string DisplayName);

public static class DesktopLocalization
{
    public const string DefaultLanguageCode = IskraLanguages.Ukrainian;

    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(IskraLanguages.Ukrainian, "Українська"),
        new(IskraLanguages.English, "English"),
        new(IskraLanguages.German, "Deutsch"),
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, DesktopText>> Catalogs = new(() =>
        new Dictionary<string, DesktopText>(StringComparer.OrdinalIgnoreCase)
        {
            [IskraLanguages.Ukrainian] = new(IskraLanguages.Ukrainian, Ukrainian!),
            [IskraLanguages.English] = new(IskraLanguages.English, English!),
            [IskraLanguages.German] = new(IskraLanguages.German, German!),
        });

    public static string Normalize(string? code) => IskraLanguages.NormalizeOrDefault(code);

    public static DesktopText For(string? code) => Catalogs.Value[Normalize(code)];

    public static CultureInfo CultureFor(string? code) => IskraLanguages.CultureFor(code);

    private static readonly IReadOnlyDictionary<DesktopTextKey, string> Ukrainian = CreateCatalog(
    [
        (DesktopTextKey.WindowTitle, "Iskra — станція прошивання"),
        (DesktopTextKey.Tagline, "Безпечне прошивання та перевірка пристроїв"),
        (DesktopTextKey.TabFlash, "Прошивка"),
        (DesktopTextKey.Refresh, "Перевірити ще раз"),
        (DesktopTextKey.SignedCatalog, "ПІДПИСАНИЙ КАТАЛОГ"),
        (DesktopTextKey.OperatorChange, "Зміна оператора"),
        (DesktopTextKey.OperatorMigration, "Оператор і виріб будуть підключені до спільного процесу в наступному етапі міграції."),
        (DesktopTextKey.Operator, "ОПЕРАТОР"),
        (DesktopTextKey.OperatorPlaceholder, "Ім’я оператора"),
        (DesktopTextKey.Product, "ВИРІБ"),
        (DesktopTextKey.FlashAction, "ПРОШИТИ"),
        (DesktopTextKey.FlashDisabledUntilParity, "Кнопка активується після перенесення повного процесу прошивання та HIL-перевірки."),
        (DesktopTextKey.TabHistory, "Історія"),
        (DesktopTextKey.LocalLog, "Локальний журнал"),
        (DesktopTextKey.HistoryMigration, "SQLite залишається локальним джерелом істини. У цьому першому зрізі Avalonia журнал відкривається лише для перевірки готовності; таблиця та експорт мігрують далі."),
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
        (DesktopTextKey.MigrationSafetyNotice, "Це перший безпечний зріз перенесення інтерфейсу. Він уже перевіряє BMP, GDB і каталог через Iskra.Core, але навмисно не запускає прошивання, доки кросплатформний процес не матиме тестового та апаратного паритету з WPF."),
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
        (DesktopTextKey.StationReadyDetail, "BMP, ARM GDB і підписаний каталог доступні. Саме прошивання в Avalonia залишено заблокованим до HIL-паритету."),
        (DesktopTextKey.Attention, "Потрібна увага: {0}."),
        (DesktopTextKey.CheckedAt, "Перевірено {0}"),
        (DesktopTextKey.Megabytes, "{0} МБ"),
        (DesktopTextKey.Kilobytes, "{0} КБ"),
        (DesktopTextKey.Bytes, "{0} Б"),
        (DesktopTextKey.TargetSummary, "{0} · {1} КБ"),
        (DesktopTextKey.ReleaseSummary, "v{0} · релізів: {1}"),
    ]);

    private static readonly IReadOnlyDictionary<DesktopTextKey, string> English = CreateCatalog(
    [
        (DesktopTextKey.WindowTitle, "Iskra — flashing station"),
        (DesktopTextKey.Tagline, "Safe device flashing and verification"),
        (DesktopTextKey.TabFlash, "Flash"),
        (DesktopTextKey.Refresh, "Check again"),
        (DesktopTextKey.SignedCatalog, "SIGNED CATALOG"),
        (DesktopTextKey.OperatorChange, "Operator change"),
        (DesktopTextKey.OperatorMigration, "The operator and product will be connected to the shared workflow in the next migration stage."),
        (DesktopTextKey.Operator, "OPERATOR"),
        (DesktopTextKey.OperatorPlaceholder, "Operator name"),
        (DesktopTextKey.Product, "PRODUCT"),
        (DesktopTextKey.FlashAction, "FLASH"),
        (DesktopTextKey.FlashDisabledUntilParity, "The button will be enabled after the complete flashing workflow has been migrated and HIL-verified."),
        (DesktopTextKey.TabHistory, "History"),
        (DesktopTextKey.LocalLog, "Local log"),
        (DesktopTextKey.HistoryMigration, "SQLite remains the local source of truth. In this first Avalonia slice, the log is opened only for readiness checks; the table and export will migrate next."),
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
        (DesktopTextKey.MigrationSafetyNotice, "This is the first safe interface-migration slice. It already checks BMP, GDB, and the catalog through Iskra.Core, but deliberately does not start flashing until the cross-platform workflow has test and hardware parity with WPF."),
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
        (DesktopTextKey.StationReadyDetail, "BMP, ARM GDB, and the signed catalog are available. Flashing in Avalonia remains blocked until HIL parity is reached."),
        (DesktopTextKey.Attention, "Needs attention: {0}."),
        (DesktopTextKey.CheckedAt, "Checked {0}"),
        (DesktopTextKey.Megabytes, "{0} MB"),
        (DesktopTextKey.Kilobytes, "{0} KB"),
        (DesktopTextKey.Bytes, "{0} B"),
        (DesktopTextKey.TargetSummary, "{0} · {1} KB"),
        (DesktopTextKey.ReleaseSummary, "v{0} · releases: {1}"),
    ]);

    private static readonly IReadOnlyDictionary<DesktopTextKey, string> German = CreateCatalog(
    [
        (DesktopTextKey.WindowTitle, "Iskra — Flash-Station"),
        (DesktopTextKey.Tagline, "Sicheres Flashen und Prüfen von Geräten"),
        (DesktopTextKey.TabFlash, "Flashen"),
        (DesktopTextKey.Refresh, "Erneut prüfen"),
        (DesktopTextKey.SignedCatalog, "SIGNIERTER KATALOG"),
        (DesktopTextKey.OperatorChange, "Bedienerwechsel"),
        (DesktopTextKey.OperatorMigration, "Bediener und Produkt werden im nächsten Migrationsschritt mit dem gemeinsamen Ablauf verbunden."),
        (DesktopTextKey.Operator, "BEDIENER"),
        (DesktopTextKey.OperatorPlaceholder, "Name des Bedieners"),
        (DesktopTextKey.Product, "PRODUKT"),
        (DesktopTextKey.FlashAction, "FLASHEN"),
        (DesktopTextKey.FlashDisabledUntilParity, "Die Schaltfläche wird aktiviert, sobald der vollständige Flash-Ablauf migriert und per HIL geprüft wurde."),
        (DesktopTextKey.TabHistory, "Verlauf"),
        (DesktopTextKey.LocalLog, "Lokales Protokoll"),
        (DesktopTextKey.HistoryMigration, "SQLite bleibt die lokale Quelle der Wahrheit. In diesem ersten Avalonia-Schritt wird das Protokoll nur zur Bereitschaftsprüfung geöffnet; Tabelle und Export folgen."),
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
        (DesktopTextKey.MigrationSafetyNotice, "Dies ist der erste sichere Schritt der Oberflächenmigration. BMP, GDB und Katalog werden bereits über Iskra.Core geprüft; das Flashen bleibt jedoch absichtlich gesperrt, bis der plattformübergreifende Ablauf Test- und Hardwareparität mit WPF erreicht."),
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
        (DesktopTextKey.StationReadyDetail, "BMP, ARM GDB und der signierte Katalog sind verfügbar. Das Flashen in Avalonia bleibt bis zur HIL-Parität gesperrt."),
        (DesktopTextKey.Attention, "Eingriff erforderlich: {0}."),
        (DesktopTextKey.CheckedAt, "Geprüft {0}"),
        (DesktopTextKey.Megabytes, "{0} MB"),
        (DesktopTextKey.Kilobytes, "{0} KB"),
        (DesktopTextKey.Bytes, "{0} B"),
        (DesktopTextKey.TargetSummary, "{0} · {1} KB"),
        (DesktopTextKey.ReleaseSummary, "v{0} · Releases: {1}"),
    ]);

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
    public string OperatorMigration => Get(DesktopTextKey.OperatorMigration);
    public string Operator => Get(DesktopTextKey.Operator);
    public string OperatorPlaceholder => Get(DesktopTextKey.OperatorPlaceholder);
    public string Product => Get(DesktopTextKey.Product);
    public string FlashAction => Get(DesktopTextKey.FlashAction);
    public string FlashDisabledUntilParity => Get(DesktopTextKey.FlashDisabledUntilParity);
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
    public string MigrationSafetyNotice => Get(DesktopTextKey.MigrationSafetyNotice);
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
    OperatorMigration,
    Operator,
    OperatorPlaceholder,
    Product,
    FlashAction,
    FlashDisabledUntilParity,
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
    MigrationSafetyNotice,
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
}
