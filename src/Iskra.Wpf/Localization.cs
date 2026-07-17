using System.Globalization;
using System.Windows.Markup;
using Iskra.Application.Localization;
using Iskra.Core;

namespace Iskra.Wpf;

/// <summary>
/// Embedded operator-interface text. Keeping the three translations in the
/// executable avoids satellite-resource files being omitted by the single-file
/// installer. Technical identifiers, error codes, paths, and log payloads are
/// deliberately supplied as format arguments rather than translated here.
/// </summary>
internal static class UiText
{
    private sealed record Text3(string Uk, string En, string De);

    private static readonly IReadOnlyDictionary<string, Text3> Texts =
        new Dictionary<string, Text3>(StringComparer.Ordinal)
        {
            // Main window and status strip.
            ["Window.Title"] = new("Iskra — масова прошивка", "Iskra — production flashing", "Iskra — Serienprogrammierung"),
            ["Status.Port.Pending"] = new("Порт: …", "Port: …", "Port: …"),
            ["Status.Gdb.Pending"] = new("gdb: …", "gdb: …", "gdb: …"),
            ["Status.Catalog.Pending"] = new("Каталог: …", "Catalog: …", "Katalog: …"),
            ["Status.Cloud.Pending"] = new("Хмара: …", "Cloud: …", "Cloud: …"),
            ["Probe.Refresh"] = new("Оновити BMP", "Refresh BMP", "BMP aktualisieren"),
            ["Probe.Refresh.Tooltip"] = new("Повторно знайти підключений Black Magic Probe", "Scan again for a connected Black Magic Probe", "Erneut nach einer angeschlossenen Black Magic Probe suchen"),

            // Tabs and flash screen.
            ["Tab.Flash"] = new("Прошивка", "Flash", "Programmieren"),
            ["Tab.History"] = new("Історія", "History", "Verlauf"),
            ["Tab.Catalog"] = new("Каталог", "Catalog", "Katalog"),
            ["Tab.Settings"] = new("Налаштування", "Settings", "Einstellungen"),
            ["Flash.Operator"] = new("Оператор:", "Operator:", "Bediener:"),
            ["Flash.Batch"] = new("Партія:", "Batch:", "Charge:"),
            ["Flash.Product"] = new("Продукт:", "Product:", "Produkt:"),
            ["Flash.Version"] = new("Версія:", "Version:", "Version:"),
            ["Flash.Ready.Title"] = new("ГОТОВО ДО ПРОШИВКИ", "READY TO FLASH", "BEREIT ZUM PROGRAMMIEREN"),
            ["Flash.Ready.Detail"] = new("Натисніть кнопку нижче, щоб прошити плату", "Press the button below to flash the board", "Klicken Sie unten, um die Platine zu programmieren"),
            ["Flash.Auth.Required"] = new("Цей продукт потребує авторизації GitHub для завантаження прошивки.", "This product requires GitHub sign-in to download firmware.", "Für dieses Produkt ist eine GitHub-Anmeldung zum Herunterladen der Firmware erforderlich."),
            ["Action.SignIn"] = new("Увійти", "Sign in", "Anmelden"),
            ["Flash.Tooltip"] = new("Запустити прошивку поточної плати", "Flash the current board", "Aktuelle Platine programmieren"),
            ["Flash.Button"] = new("ПРОШИТИ ПЛАТУ", "FLASH BOARD", "PLATINE PROGRAMMIEREN"),
            ["Flash.Hotkey.Default"] = new("(або натисніть Enter)", "(or press Enter)", "(oder Enter drücken)"),
            ["Flash.GdbDetails"] = new("Деталі gdb:", "gdb details:", "gdb-Details:"),

            // History.
            ["History.Loading"] = new("Завантаження...", "Loading...", "Wird geladen..."),
            ["History.ExportBatch"] = new("Експорт CSV (партія)", "Export CSV (batch)", "CSV exportieren (Charge)"),
            ["History.ExportAll"] = new("Експорт CSV (все)", "Export CSV (all)", "CSV exportieren (alle)"),
            ["Action.Refresh"] = new("Оновити", "Refresh", "Aktualisieren"),
            ["History.TimeUtc"] = new("Час (UTC)", "Time (UTC)", "Zeit (UTC)"),
            ["History.Result"] = new("Результат", "Result", "Ergebnis"),
            ["History.Product"] = new("Продукт", "Product", "Produkt"),
            ["History.Version"] = new("Версія", "Version", "Version"),
            ["History.Batch"] = new("Партія", "Batch", "Charge"),
            ["History.Operator"] = new("Оператор", "Operator", "Bediener"),
            ["History.DurationMs"] = new("мс", "ms", "ms"),
            ["History.Error"] = new("Помилка", "Error", "Fehler"),
            ["History.Target"] = new("Ціль (BMP)", "Target (BMP)", "Ziel (BMP)"),

            // Catalog tab.
            ["Catalog.NotLoaded"] = new("Каталог не завантажено.", "Catalog is not loaded.", "Katalog ist nicht geladen."),
            ["Catalog.Reload"] = new("Перезавантажити", "Reload", "Neu laden"),
            ["Catalog.Target"] = new("Ціль:", "Target:", "Ziel:"),
            ["Catalog.ReleasesDefault"] = new("Релізи (за замовчуванням: ", "Releases (default: ", "Versionen (Standard: "),
            ["Catalog.Type"] = new("Тип: ", "Type: ", "Typ: "),
            ["Catalog.Date"] = new("Дата: ", "Date: ", "Datum: "),

            // Settings static text.
            ["Settings.Section.Catalog"] = new("Каталог прошивок", "Firmware catalog", "Firmwarekatalog"),
            ["Settings.CatalogPath"] = new("catalog.json / sideload:", "catalog.json / sideload:", "catalog.json / Sideload:"),
            ["Settings.SignatureRequired"] = new("Обовʼязковий підпис:", "Signature required:", "Signatur erforderlich:"),
            ["Settings.SignatureRequired.Content"] = new("Відмовляти у роботі, якщо catalog.json не підписано Ed25519 (вимикати лише в лабораторії)", "Refuse operation when catalog.json is not Ed25519-signed (disable only in the lab)", "Betrieb verweigern, wenn catalog.json nicht mit Ed25519 signiert ist (nur im Labor deaktivieren)"),
            ["Settings.Section.CatalogUpdate"] = new("Каталог GitHub (авто-оновлення)", "GitHub catalog (automatic updates)", "GitHub-Katalog (automatische Updates)"),
            ["Settings.AutoUpdate"] = new("Авто-оновлення:", "Automatic updates:", "Automatische Updates:"),
            ["Settings.AutoUpdate.Content"] = new("Завантажувати свіжий каталог з iskra-catalog при запуску", "Download the latest catalog from iskra-catalog at startup", "Beim Start den neuesten Katalog aus iskra-catalog laden"),
            ["Settings.LockedSource"] = new("Джерело (заблоковано):", "Source (locked):", "Quelle (gesperrt):"),
            ["Settings.LockedSource.Tooltip"] = new("Джерело каталогу зашите у застосунок (CatalogTrust.AllowedCatalogSources). Змінюється лише новою збіркою.", "The catalog source is built into the app (CatalogTrust.AllowedCatalogSources). It changes only with a new build.", "Die Katalogquelle ist in die App eingebaut (CatalogTrust.AllowedCatalogSources). Sie ändert sich nur mit einem neuen Build."),
            ["Settings.CurrentVersion"] = new("Поточна версія:", "Current version:", "Aktuelle Version:"),
            ["Action.CheckUpdates"] = new("Перевірити оновлення", "Check for updates", "Nach Updates suchen"),
            ["Settings.Section.AppUpdate"] = new("Оновлення застосунку Iskra", "Iskra application updates", "Iskra-Anwendungsupdates"),
            ["Settings.Releases"] = new("Релізи:", "Releases:", "Versionen:"),
            ["Settings.Status"] = new("Стан:", "Status:", "Status:"),
            ["Action.Check"] = new("Перевірити", "Check", "Prüfen"),
            ["Action.OpenRelease"] = new("Відкрити реліз", "Open release", "Version öffnen"),
            ["Settings.Section.GitHubAuth"] = new("Авторизація GitHub", "GitHub authorization", "GitHub-Autorisierung"),
            ["Action.SignOut"] = new("Вийти", "Sign out", "Abmelden"),
            ["Settings.Section.Debugger"] = new("Відлагоджувач (Black Magic Probe)", "Debugger (Black Magic Probe)", "Debugger (Black Magic Probe)"),
            ["Settings.GdbPath"] = new("Шлях до arm-none-eabi-gdb:", "Path to arm-none-eabi-gdb:", "Pfad zu arm-none-eabi-gdb:"),
            ["Settings.AutoDetect.Tooltip"] = new("Залиште порожнім для авто-визначення", "Leave blank for automatic detection", "Für automatische Erkennung leer lassen"),
            ["Settings.SwdFrequency"] = new("Частота SWD, Hz:", "SWD frequency, Hz:", "SWD-Frequenz, Hz:"),
            ["Settings.Power"] = new("Живлення:", "Power:", "Stromversorgung:"),
            ["Settings.Power.External"] = new("Зовнішнє", "External", "Extern"),
            ["Settings.Power.Probe"] = new("Від BMP (tpwr)", "From BMP (tpwr)", "Vom BMP (tpwr)"),
            ["Settings.ConnectReset.Content"] = new("Утримувати NRST при підключенні (для чипів зі сплячим SWD)", "Hold NRST while connecting (for chips with sleeping SWD)", "NRST beim Verbinden halten (für Chips mit inaktivem SWD)"),
            ["Settings.ConnectReset"] = new("Connect-under-reset:", "Connect-under-reset:", "Connect-under-reset:"),
            ["Settings.Timeout"] = new("Тайм-аут, сек:", "Timeout, sec:", "Zeitlimit, Sek.:"),
            ["Settings.Section.LogStation"] = new("Журнал і станція", "Log and station", "Protokoll und Station"),
            ["Settings.LogFile"] = new("Файл журналу (.db):", "Log file (.db):", "Protokolldatei (.db):"),
            ["Settings.LogFile.Tooltip"] = new("Залиште порожнім для %LOCALAPPDATA%\\Iskra\\flash_log.db", "Leave blank for %LOCALAPPDATA%\\Iskra\\flash_log.db", "Für %LOCALAPPDATA%\\Iskra\\flash_log.db leer lassen"),
            ["Settings.StationId"] = new("ID станції:", "Station ID:", "Stations-ID:"),
            ["Settings.Section.OperatorUi"] = new("Інтерфейс оператора", "Operator interface", "Bedienoberfläche"),
            ["Settings.Language"] = new("Мова інтерфейсу:", "Interface language:", "Oberflächensprache:"),
            ["Settings.Language.Tooltip"] = new("Зміна мови застосовується після перезапуску Iskra.", "The language change takes effect after restarting Iskra.", "Die Sprachänderung wird nach einem Neustart von Iskra wirksam."),
            ["Settings.Hotkey"] = new("Гаряча клавіша прошивки:", "Flash hotkey:", "Programmier-Tastenkürzel:"),
            ["Settings.Hotkey.Tooltip"] = new("Клавіша, що натискає кнопку «Прошити плату». Enter зручно поєднується зі сканером штрих-кодів.", "Key that activates the Flash Board button. Enter works well with a barcode scanner.", "Taste zum Auslösen der Schaltfläche „Platine programmieren“. Enter eignet sich gut für Barcodescanner."),
            ["Hotkey.Disabled"] = new("(вимкнено)", "(disabled)", "(deaktiviert)"),
            ["Hotkey.Space"] = new("Пробіл", "Space", "Leertaste"),
            ["Settings.Section.CloudLog"] = new("Хмарний журнал (iskra-logs)", "Cloud log (iskra-logs)", "Cloud-Protokoll (iskra-logs)"),
            ["Settings.Cloud.AutoUpload"] = new("Авто-вивантажувати журнал у GitHub", "Automatically upload the log to GitHub", "Protokoll automatisch zu GitHub hochladen"),
            ["Settings.Cloud.Interval"] = new("Інтервал (хв):", "Interval (min):", "Intervall (Min.):"),
            ["Settings.Cloud.Interval.Tooltip"] = new("Як часто фонова черга підштовхує несинхронізовані рядки", "How often the background queue uploads unsynchronized rows", "Wie oft die Hintergrundwarteschlange nicht synchronisierte Zeilen hochlädt"),
            ["Settings.Cloud.PrivateKey"] = new("Приватний ключ (.pem):", "Private key (.pem):", "Privater Schlüssel (.pem):"),
            ["Settings.Cloud.PrivateKey.Tooltip"] = new("Файл приватного ключа GitHub App (за замовчанням %PROGRAMDATA%\\Iskra\\station-app.pem)", "GitHub App private-key file (default: %PROGRAMDATA%\\Iskra\\station-app.pem)", "Private-Schlüssel-Datei der GitHub App (Standard: %PROGRAMDATA%\\Iskra\\station-app.pem)"),
            ["Settings.Batches"] = new("Партії:", "Batches:", "Chargen:"),
            ["Settings.Batches.Content"] = new("Увімкнути поле партії та локальне блокування прошивки", "Enable the batch field and local firmware lock", "Chargenfeld und lokale Firmware-Sperre aktivieren"),
            ["Settings.Batches.Tooltip"] = new("За замовчуванням вимкнено. Увімкніть лише коли потрібно групувати та блокувати прошивку за ID партії.", "Disabled by default. Enable only when flashes must be grouped and locked by batch ID.", "Standardmäßig deaktiviert. Nur aktivieren, wenn Programmiervorgänge nach Chargen-ID gruppiert und gesperrt werden müssen."),
            ["Settings.Repository"] = new("Репозиторій:", "Repository:", "Repository:"),
            ["Action.UploadNow"] = new("Вивантажити зараз", "Upload now", "Jetzt hochladen"),
            ["Action.Save"] = new("Зберегти", "Save", "Speichern"),
            ["Action.ResetDefaults"] = new("Скинути до замовчень", "Reset to defaults", "Auf Standardwerte zurücksetzen"),
            ["Settings.AutoSave.Note"] = new("Зміни також зберігаються автоматично при переході з цієї вкладки та перед закриттям Iskra.", "Changes are also saved automatically when leaving this tab and before closing Iskra.", "Änderungen werden beim Verlassen dieser Registerkarte und vor dem Schließen von Iskra automatisch gespeichert."),

            // Device-flow dialog.
            ["Device.Title"] = new("GitHub: вхід", "GitHub: sign in", "GitHub: Anmeldung"),
            ["Device.Heading"] = new("Авторизація GitHub Device Flow", "GitHub Device Flow authorization", "GitHub Device-Flow-Autorisierung"),
            ["Device.Step1"] = new("1. Відкрийте у браузері:", "1. Open in your browser:", "1. Im Browser öffnen:"),
            ["Action.Open"] = new("Відкрити", "Open", "Öffnen"),
            ["Device.Step2"] = new("2. Введіть код:", "2. Enter the code:", "2. Code eingeben:"),
            ["Device.CopyCode"] = new("Копіювати код", "Copy code", "Code kopieren"),
            ["Device.Waiting"] = new("Очікування авторизації…", "Waiting for authorization…", "Warten auf Autorisierung…"),
            ["Action.Cancel"] = new("Скасувати", "Cancel", "Abbrechen"),

            // Dynamic readiness and catalog state.
            ["Gdb.NotFound.Status"] = new("gdb: НЕ ЗНАЙДЕНО", "gdb: NOT FOUND", "gdb: NICHT GEFUNDEN"),
            ["Probe.Port"] = new("Порт: {0}{1}", "Port: {0}{1}", "Port: {0}{1}"),
            ["Probe.NotFound.Status"] = new("Порт: BMP не знайдено", "Port: BMP not found", "Port: BMP nicht gefunden"),
            ["Probe.Multiple.Status"] = new("Порт: знайдено {0} BMP (потрібно один)", "Port: {0} BMPs found (exactly one required)", "Port: {0} BMPs gefunden (genau eine erforderlich)"),
            ["Ready.Probe.Title"] = new("BLACK MAGIC PROBE НЕ ПІДКЛЮЧЕНО", "BLACK MAGIC PROBE NOT CONNECTED", "BLACK MAGIC PROBE NICHT VERBUNDEN"),
            ["Ready.Probe.Detail"] = new("Підключіть рівно один BMP і натисніть «Оновити BMP» у верхній панелі.", "Connect exactly one BMP and select Refresh BMP in the top bar.", "Schließen Sie genau eine BMP an und wählen Sie oben „BMP aktualisieren“."),
            ["Ready.Gdb.Title"] = new("GDB НЕ ЗНАЙДЕНО", "GDB NOT FOUND", "GDB NICHT GEFUNDEN"),
            ["Ready.Gdb.Detail"] = new("Вкажіть arm-none-eabi-gdb у Налаштуваннях або повторно запустіть інсталятор Iskra.", "Set arm-none-eabi-gdb in Settings or run the Iskra installer again.", "Geben Sie arm-none-eabi-gdb in den Einstellungen an oder führen Sie den Iskra-Installer erneut aus."),
            ["Ready.Catalog.Title"] = new("КАТАЛОГ НЕ ГОТОВИЙ", "CATALOG NOT READY", "KATALOG NICHT BEREIT"),
            ["Ready.Catalog.Detail"] = new("Перевірте підпис і шлях до catalog.json у Налаштуваннях.", "Check the signature and catalog.json path in Settings.", "Prüfen Sie die Signatur und den Pfad zu catalog.json in den Einstellungen."),
            ["Ready.Operator.Title"] = new("ВКАЖІТЬ ОПЕРАТОРА", "ENTER OPERATOR", "BEDIENER EINGEBEN"),
            ["Ready.Operator.Detail"] = new("Після цього прошивка стане доступною.", "Flashing will then become available.", "Danach ist das Programmieren verfügbar."),
            ["Ready.Batch.Title"] = new("ВКАЖІТЬ ID ПАРТІЇ", "ENTER BATCH ID", "CHARGEN-ID EINGEBEN"),
            ["Ready.Batch.Detail"] = new("Партії можна вимкнути в Налаштуваннях → Хмарний журнал.", "Batches can be disabled under Settings → Cloud log.", "Chargen können unter Einstellungen → Cloud-Protokoll deaktiviert werden."),
            ["Ready.Selection.Title"] = new("ОБЕРІТЬ ПРОДУКТ І ВЕРСІЮ", "SELECT PRODUCT AND VERSION", "PRODUKT UND VERSION AUSWÄHLEN"),
            ["Ready.BatchEnabled.Detail"] = new("Перевірте ID партії та натисніть кнопку нижче", "Check the batch ID and press the button below", "Prüfen Sie die Chargen-ID und klicken Sie unten"),
            ["Ready.BatchDisabled.Detail"] = new("Партії вимкнено · спроба буде записана без ID партії", "Batches are disabled · the attempt will be logged without a batch ID", "Chargen sind deaktiviert · der Versuch wird ohne Chargen-ID protokolliert"),
            ["Catalog.Status"] = new("Каталог: {0}", "Catalog: {0}", "Katalog: {0}"),
            ["Catalog.Unavailable"] = new("Каталог недоступний: {0}", "Catalog unavailable: {0}", "Katalog nicht verfügbar: {0}"),
            ["Catalog.Trust.Sideload"] = new("sideload (лабораторний режим)", "sideload (lab mode)", "Sideload (Labormodus)"),
            ["Catalog.Trust.Unsigned"] = new("без підпису (лабораторний режим)", "unsigned (lab mode)", "nicht signiert (Labormodus)"),
            ["Catalog.Trust.Unknown"] = new("невідомий стан довіри", "unknown trust state", "unbekannter Vertrauensstatus"),
            ["Catalog.Loaded.Status"] = new("Каталог: {0} продукт(ів) · {1} · {2}", "Catalog: {0} product(s) · {1} · {2}", "Katalog: {0} Produkt(e) · {1} · {2}"),
            ["Catalog.Revoked.Suffix"] = new(" · {0} відкликано", " · {0} revoked", " · {0} zurückgerufen"),
            ["Catalog.Header"] = new("{0} · {1} продукт(ів) · {2}{3}", "{0} · {1} product(s) · {2}{3}", "{0} · {1} Produkt(e) · {2}{3}"),
            ["Catalog.Fail.NotFound"] = new("не знайдено (вкажіть шлях у Налаштуваннях)", "not found (set the path in Settings)", "nicht gefunden (Pfad in den Einstellungen angeben)"),
            ["Catalog.Fail.PathMissing"] = new("вказаний шлях не існує: {0}", "configured path does not exist: {0}", "angegebener Pfad ist nicht vorhanden: {0}"),
            ["Catalog.Fail.SideloadLab"] = new("sideload дозволено лише в явному лабораторному режимі", "sideload is allowed only in explicit lab mode", "Sideload ist nur im ausdrücklich aktivierten Labormodus erlaubt"),
            ["Catalog.Fail.Unsigned"] = new("підпис обов'язковий, але файл .sig відсутній", "a signature is required, but the .sig file is missing", "eine Signatur ist erforderlich, aber die .sig-Datei fehlt"),
            ["Catalog.Fail.BadSignature"] = new("невірний підпис Ed25519", "invalid Ed25519 signature", "ungültige Ed25519-Signatur"),
            ["Catalog.Fail.NoKey"] = new("у застосунку немає довіреного публічного ключа", "the app has no trusted public key", "die App enthält keinen vertrauenswürdigen öffentlichen Schlüssel"),
            ["Catalog.Fail.SignatureIo"] = new("файл підпису неможливо прочитати", "the signature file cannot be read", "die Signaturdatei kann nicht gelesen werden"),
            ["Catalog.Fail.Trust"] = new("перевірку довіри не пройдено", "trust verification failed", "Vertrauensprüfung fehlgeschlagen"),
            ["Catalog.Fail.Parse"] = new("помилка формату — {0}", "format error — {0}", "Formatfehler — {0}"),
            ["Catalog.Fail.Read"] = new("помилка читання — {0}", "read error — {0}", "Lesefehler — {0}"),
            ["Catalog.Fail.Generic"] = new("помилка — {0}", "error — {0}", "Fehler — {0}"),

            // Dynamic flash, history, and settings state.
            ["Flash.EnterOperator"] = new("Вкажіть оператора", "Enter an operator", "Bediener eingeben"),
            ["Flash.EnterBatch"] = new("Вкажіть ID партії", "Enter a batch ID", "Chargen-ID eingeben"),
            ["Flash.DisableBatchHint"] = new("Або вимкніть партії в Налаштуваннях → Хмарний журнал.", "Or disable batches under Settings → Cloud log.", "Oder deaktivieren Sie Chargen unter Einstellungen → Cloud-Protokoll."),
            ["Flash.GdbMissing"] = new("gdb не знайдено. Повторно запустіть інсталятор Iskra.", "gdb was not found. Run the Iskra installer again.", "gdb wurde nicht gefunden. Führen Sie den Iskra-Installer erneut aus."),
            ["Flash.ProbeMissing"] = new("Black Magic Probe не знайдено", "Black Magic Probe not found", "Black Magic Probe nicht gefunden"),
            ["Flash.Downloading"] = new("Завантаження прошивки з GitHub…", "Downloading firmware from GitHub…", "Firmware wird von GitHub heruntergeladen…"),
            ["Flash.AuthOpenSettings"] = new("Відкрийте Налаштування → Авторизація GitHub → Увійти.", "Open Settings → GitHub authorization → Sign in.", "Öffnen Sie Einstellungen → GitHub-Autorisierung → Anmelden."),
            ["Flash.AuthExpired"] = new("Сесію GitHub потрібно поновити (>6 міс без оновлення).", "The GitHub session must be renewed (>6 months without refresh).", "Die GitHub-Sitzung muss erneuert werden (>6 Monate ohne Aktualisierung)."),
            ["Flash.FileNotFound"] = new("Файл прошивки не знайдено: {0}", "Firmware file not found: {0}", "Firmwaredatei nicht gefunden: {0}"),
            ["Flash.Running"] = new("Виконується…", "Flashing…", "Programmierung läuft…"),
            ["Flash.FileReadFailed"] = new("Не вдалося прочитати файл прошивки: {0}", "Could not read firmware file: {0}", "Firmwaredatei konnte nicht gelesen werden: {0}"),
            ["Flash.FileBadFormat"] = new("Файл не є коректним {0}: {1}", "File is not valid {0}: {1}", "Datei ist kein gültiges {0}: {1}"),
            ["Flash.Success"] = new("✓ ПРОШИВКА УСПІШНА", "✓ FLASH SUCCESSFUL", "✓ PROGRAMMIERUNG ERFOLGREICH"),
            ["Flash.Duration"] = new("{0:F0} мс", "{0:F0} ms", "{0:F0} ms"),
            ["Flash.ErrorDetails"] = new("Деталі помилки", "Error details", "Fehlerdetails"),
            ["Batch.Locked"] = new("🔒 Партію заблоковано на {0} v{1} · SHA {2}", "🔒 Batch locked to {0} v{1} · SHA {2}", "🔒 Charge auf {0} v{1} gesperrt · SHA {2}"),
            ["Batch.New"] = new("Партія нова — перша прошивка визначить продукт + версію.", "New batch — the first flash will set the product and version.", "Neue Charge — die erste Programmierung legt Produkt und Version fest."),
            ["Batch.CheckFailed"] = new("Неможливо перевірити блокування партії: {0}", "Could not check the batch lock: {0}", "Chargensperre konnte nicht geprüft werden: {0}"),
            ["Common.Unknown"] = new("невідомо", "unknown", "unbekannt"),
            ["History.BatchesDisabled"] = new("Партії вимкнено. Скористайтеся експортом усього журналу.", "Batches are disabled. Export the complete log instead.", "Chargen sind deaktiviert. Exportieren Sie stattdessen das gesamte Protokoll."),
            ["History.NothingToExport"] = new("Журнал ще не створено — нічого експортувати.", "The log has not been created yet — nothing to export.", "Das Protokoll wurde noch nicht erstellt — nichts zu exportieren."),
            ["History.EnterBatch"] = new("Введіть Партію на вкладці Прошивка, щоб експортувати тільки її.", "Enter a batch on the Flash tab to export only that batch.", "Geben Sie auf der Registerkarte Programmieren eine Charge ein, um nur diese zu exportieren."),
            ["Dialog.Filter.Csv"] = new("CSV-файли (*.csv)|*.csv|Усі файли (*.*)|*.*", "CSV files (*.csv)|*.csv|All files (*.*)|*.*", "CSV-Dateien (*.csv)|*.csv|Alle Dateien (*.*)|*.*"),
            ["History.ExportBatch.Title"] = new("Експорт партії {0}", "Export batch {0}", "Charge {0} exportieren"),
            ["History.ExportAll.Title"] = new("Експорт усього журналу", "Export complete log", "Gesamtes Protokoll exportieren"),
            ["History.Exported"] = new("✓ Експортовано {0} рядків у {1}", "✓ Exported {0} row(s) to {1}", "✓ {0} Zeile(n) nach {1} exportiert"),
            ["History.ExportError"] = new("Помилка експорту: {0}", "Export error: {0}", "Exportfehler: {0}"),
            ["History.NoLog"] = new("Журнал ще не створено.", "The log has not been created yet.", "Das Protokoll wurde noch nicht erstellt."),
            ["History.BatchSummary"] = new("Партія «{0}»: {1} PASS / {2} FAIL  ({3:P0} успіх)", "Batch “{0}”: {1} PASS / {2} FAIL  ({3:P0} success)", "Charge „{0}“: {1} PASS / {2} FAIL  ({3:P0} erfolgreich)"),
            ["History.BatchEmpty"] = new("Партія «{0}»: ще немає записів.", "Batch “{0}”: no records yet.", "Charge „{0}“: noch keine Einträge."),
            ["History.RecentNeedBatch"] = new("Останні {0} записів (вкажіть Партію для зведення).", "Latest {0} record(s) (enter a batch for a summary).", "Letzte {0} Einträge (Charge für eine Zusammenfassung eingeben)."),
            ["History.RecentNoBatches"] = new("Останні {0} записів. Партії вимкнено.", "Latest {0} record(s). Batches are disabled.", "Letzte {0} Einträge. Chargen sind deaktiviert."),
            ["History.ReadError"] = new("Помилка читання журналу: {0}", "Log read error: {0}", "Fehler beim Lesen des Protokolls: {0}"),
            ["Settings.LabAllowed"] = new("Лабораторний режим дозволено змінною ISKRA_LAB_ALLOW_UNSIGNED_CATALOG.", "Lab mode is enabled by ISKRA_LAB_ALLOW_UNSIGNED_CATALOG.", "Der Labormodus ist durch ISKRA_LAB_ALLOW_UNSIGNED_CATALOG aktiviert."),
            ["Settings.SignatureMandatory"] = new("На операторській станції підпис каталогу обовʼязковий.", "A catalog signature is mandatory on an operator station.", "Auf einer Bedienstation ist eine Katalogsignatur zwingend erforderlich."),
            ["Settings.ReadOnlySuffix"] = new("(лише читання)", "(read-only)", "(schreibgeschützt)"),
            ["Flash.HotkeyHint"] = new("(або натисніть {0})", "(or press {0})", "(oder {0} drücken)"),
            ["Flash.HotkeyTooltip"] = new("Запустити прошивку. Гаряча клавіша: {0}", "Flash the board. Hotkey: {0}", "Platine programmieren. Tastenkürzel: {0}"),
            ["Settings.Dirty.Detail"] = new("Є незбережені зміни — вони збережуться при виході з вкладки.", "There are unsaved changes — they will be saved when leaving the tab.", "Es gibt ungespeicherte Änderungen — sie werden beim Verlassen der Registerkarte gespeichert."),
            ["Settings.Dirty.Short"] = new("Є незбережені зміни", "Unsaved changes", "Ungespeicherte Änderungen"),
            ["Settings.CloseDuringFlash"] = new("Дочекайтеся завершення прошивки перед закриттям Iskra.", "Wait for flashing to finish before closing Iskra.", "Warten Sie, bis die Programmierung abgeschlossen ist, bevor Sie Iskra schließen."),
            ["Settings.FlashRunning.Title"] = new("Прошивка виконується", "Flashing in progress", "Programmierung läuft"),
            ["Settings.FrequencyInvalid"] = new("Частота повинна бути додатнім цілим числом (Hz).", "Frequency must be a positive integer (Hz).", "Die Frequenz muss eine positive ganze Zahl sein (Hz)."),
            ["Settings.TimeoutInvalid"] = new("Тайм-аут повинен бути додатнім цілим (секунди).", "Timeout must be a positive integer (seconds).", "Das Zeitlimit muss eine positive ganze Zahl sein (Sekunden)."),
            ["Settings.IntervalInvalid"] = new("Інтервал вивантаження повинен бути додатнім цілим (хвилини).", "Upload interval must be a positive integer (minutes).", "Das Upload-Intervall muss eine positive ganze Zahl sein (Minuten)."),
            ["Settings.AutoSaved"] = new("✓ Автозбережено о {0:HH:mm:ss}", "✓ Automatically saved at {0:HH:mm:ss}", "✓ Automatisch gespeichert um {0:HH:mm:ss}"),
            ["Settings.Saved"] = new("✓ Збережено о {0:HH:mm:ss}", "✓ Saved at {0:HH:mm:ss}", "✓ Gespeichert um {0:HH:mm:ss}"),
            ["Settings.AutoSaved.Short"] = new("✓ Автозбережено", "✓ Automatically saved", "✓ Automatisch gespeichert"),
            ["Settings.Saved.Short"] = new("✓ Збережено", "✓ Saved", "✓ Gespeichert"),
            ["Settings.NotSaved.Detail"] = new("✗ Не збережено: {0}", "✗ Not saved: {0}", "✗ Nicht gespeichert: {0}"),
            ["Settings.NotSaved.Short"] = new("✗ Не збережено", "✗ Not saved", "✗ Nicht gespeichert"),
            ["Settings.SaveError.Body"] = new("Налаштування не збережено:\n\n{0}\n\nВиправте значення та повторіть.", "Settings were not saved:\n\n{0}\n\nCorrect the value and try again.", "Einstellungen wurden nicht gespeichert:\n\n{0}\n\nKorrigieren Sie den Wert und versuchen Sie es erneut."),
            ["Settings.SaveError.Title"] = new("Помилка налаштувань", "Settings error", "Einstellungsfehler"),
            ["Settings.ResetNotice"] = new("Значення скинуто. Вони збережуться автоматично при виході з вкладки.", "Values were reset. They will be saved automatically when leaving the tab.", "Die Werte wurden zurückgesetzt. Sie werden beim Verlassen der Registerkarte automatisch gespeichert."),
            ["Settings.RestartNotice"] = new("Мову збережено. Перезапустіть Iskra, щоб застосувати її.", "Language saved. Restart Iskra to apply it.", "Sprache gespeichert. Starten Sie Iskra neu, um sie anzuwenden."),
            ["Settings.RestartNotice.Short"] = new("Потрібен перезапуск", "Restart required", "Neustart erforderlich"),

            // File pickers and cloud/auth/update status.
            ["Dialog.Filter.Catalog"] = new("Файли каталогу (*.json)|*.json|Усі файли (*.*)|*.*", "Catalog files (*.json)|*.json|All files (*.*)|*.*", "Katalogdateien (*.json)|*.json|Alle Dateien (*.*)|*.*"),
            ["Dialog.Catalog.Title"] = new("Виберіть catalog.json (або введіть sideload-папку вручну)", "Select catalog.json (or enter a sideload folder manually)", "catalog.json auswählen (oder einen Sideload-Ordner manuell eingeben)"),
            ["Dialog.Filter.Gdb"] = new("arm-none-eabi-gdb.exe|arm-none-eabi-gdb.exe|Виконувані файли (*.exe)|*.exe|Усі файли (*.*)|*.*", "arm-none-eabi-gdb.exe|arm-none-eabi-gdb.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*", "arm-none-eabi-gdb.exe|arm-none-eabi-gdb.exe|Ausführbare Dateien (*.exe)|*.exe|Alle Dateien (*.*)|*.*"),
            ["Dialog.Gdb.Title"] = new("Виберіть arm-none-eabi-gdb.exe", "Select arm-none-eabi-gdb.exe", "arm-none-eabi-gdb.exe auswählen"),
            ["Dialog.Filter.Sqlite"] = new("Бази даних SQLite (*.db)|*.db|Усі файли (*.*)|*.*", "SQLite databases (*.db)|*.db|All files (*.*)|*.*", "SQLite-Datenbanken (*.db)|*.db|Alle Dateien (*.*)|*.*"),
            ["Dialog.Log.Title"] = new("Файл журналу", "Log file", "Protokolldatei"),
            ["Dialog.Filter.Pem"] = new("Приватні ключі PEM (*.pem)|*.pem|Усі файли (*.*)|*.*", "PEM private keys (*.pem)|*.pem|All files (*.*)|*.*", "Private PEM-Schlüssel (*.pem)|*.pem|Alle Dateien (*.*)|*.*"),
            ["Dialog.Pem.Title"] = new("Виберіть приватний ключ GitHub App", "Select the GitHub App private key", "Privaten Schlüssel der GitHub App auswählen"),
            ["Cloud.Disabled.Status"] = new("Хмара: вимкнено", "Cloud: disabled", "Cloud: deaktiviert"),
            ["Cloud.Disabled.Detail"] = new("Вивантаження вимкнено в налаштуваннях.", "Uploading is disabled in Settings.", "Der Upload ist in den Einstellungen deaktiviert."),
            ["Cloud.Unconfigured.Status"] = new("Хмара: не налаштовано", "Cloud: not configured", "Cloud: nicht konfiguriert"),
            ["Cloud.Unconfigured.Detail"] = new("GitHub App ще не зареєстровано — зверніться до розробника.", "The GitHub App is not registered yet — contact the developer.", "Die GitHub App ist noch nicht registriert — wenden Sie sich an den Entwickler."),
            ["Cloud.Empty.Status"] = new("Хмара: 0 в черзі", "Cloud: 0 queued", "Cloud: 0 in Warteschlange"),
            ["Cloud.Empty.Detail"] = new("Журнал порожній.", "The log is empty.", "Das Protokoll ist leer."),
            ["Cloud.Synced.Status"] = new("Хмара: ✓ синхр.", "Cloud: ✓ synced", "Cloud: ✓ synchronisiert"),
            ["Cloud.Queued.Status"] = new("Хмара: {0} в черзі", "Cloud: {0} queued", "Cloud: {0} in Warteschlange"),
            ["Cloud.UploadedAll"] = new("✓ Усі рядки вивантажено.", "✓ All rows uploaded.", "✓ Alle Zeilen hochgeladen."),
            ["Cloud.RowsWaiting"] = new("{0} рядк(ів) очікують на вивантаження.", "{0} row(s) waiting to upload.", "{0} Zeile(n) warten auf den Upload."),
            ["Cloud.Error.Status"] = new("Хмара: помилка", "Cloud: error", "Cloud: Fehler"),
            ["Cloud.EnableFirst"] = new("Спочатку увімкніть авто-вивантаження.", "Enable automatic uploading first.", "Aktivieren Sie zuerst den automatischen Upload."),
            ["Cloud.KeyMissing"] = new("✗ Ключ не знайдено: {0}", "✗ Key not found: {0}", "✗ Schlüssel nicht gefunden: {0}"),
            ["Cloud.Uploading"] = new("Вивантаження…", "Uploading…", "Wird hochgeladen…"),
            ["Cloud.UploadReport"] = new("✓ Вивантажено {0} рядк(ів) ({1} нових + {2} оновлено){3}", "✓ Uploaded {0} row(s) ({1} new + {2} updated){3}", "✓ {0} Zeile(n) hochgeladen ({1} neu + {2} aktualisiert){3}"),
            ["Cloud.Leftover"] = new(", залишилось {0}.", ", {0} remaining.", ", {0} verbleibend."),
            ["Auth.TokenCorrupt"] = new("✗ Файл токенів пошкоджено: {0}", "✗ Token file is corrupted: {0}", "✗ Token-Datei ist beschädigt: {0}"),
            ["Auth.ClientMissing.Short"] = new("✗ GitHub Client ID не налаштовано в збірці.", "✗ GitHub Client ID is not configured in this build.", "✗ GitHub Client ID ist in diesem Build nicht konfiguriert."),
            ["Auth.NotSignedIn"] = new("Не авторизовано.", "Not signed in.", "Nicht angemeldet."),
            ["Auth.SessionExpired"] = new("✗ Сесія застаріла. Увійдіть знову.", "✗ Session expired. Sign in again.", "✗ Sitzung abgelaufen. Melden Sie sich erneut an."),
            ["Auth.SignedIn"] = new("✓ Авторизовано. Access {0} до {1:yyyy-MM-dd HH:mm} UTC · Refresh до {2:yyyy-MM-dd} UTC", "✓ Signed in. Access token {0} until {1:yyyy-MM-dd HH:mm} UTC · Refresh token until {2:yyyy-MM-dd} UTC", "✓ Angemeldet. Access-Token {0} bis {1:yyyy-MM-dd HH:mm} UTC · Refresh-Token bis {2:yyyy-MM-dd} UTC"),
            ["Auth.Access.Valid"] = new("дійсний", "valid", "gültig"),
            ["Auth.Access.Refresh"] = new("оновиться", "will refresh", "wird aktualisiert"),
            ["Auth.ProductClientMissing"] = new("Цей продукт потребує завантаження з GitHub, але Client ID не налаштовано в збірці.", "This product requires a GitHub download, but the Client ID is not configured in this build.", "Dieses Produkt erfordert einen GitHub-Download, aber die Client ID ist in diesem Build nicht konfiguriert."),
            ["Auth.ProductNeedsSignIn"] = new("Прошивка «{0}» завантажується з GitHub. Потрібен вхід.", "Firmware “{0}” is downloaded from GitHub. Sign-in is required.", "Firmware „{0}“ wird von GitHub heruntergeladen. Eine Anmeldung ist erforderlich."),
            ["Auth.ProductSessionExpired"] = new("Сесія GitHub застаріла. Увійдіть знову для завантаження прошивки.", "The GitHub session has expired. Sign in again to download firmware.", "Die GitHub-Sitzung ist abgelaufen. Melden Sie sich erneut an, um Firmware herunterzuladen."),
            ["Auth.DeleteFailed"] = new("Не вдалося видалити токени: {0}", "Could not delete tokens: {0}", "Token konnten nicht gelöscht werden: {0}"),
            ["Auth.SignOut.Title"] = new("Iskra — вихід", "Iskra — sign out", "Iskra — Abmeldung"),
            ["Auth.RefreshFailed"] = new("✗ Не вдалося перевірити: {0}", "✗ Check failed: {0}", "✗ Prüfung fehlgeschlagen: {0}"),
            ["Auth.ClientMissing"] = new("GitHub Client ID не налаштовано в збірці. Зверніться до розробника.", "GitHub Client ID is not configured in this build. Contact the developer.", "GitHub Client ID ist in diesem Build nicht konfiguriert. Wenden Sie sich an den Entwickler."),
            ["Auth.SignIn.Title"] = new("Iskra — вхід", "Iskra — sign in", "Iskra — Anmeldung"),
            ["Auth.DeviceCodeFailed"] = new("Не вдалося запросити код пристрою: {0}", "Could not request a device code: {0}", "Gerätecode konnte nicht angefordert werden: {0}"),
            ["Auth.SaveFailed"] = new("Не вдалося зберегти токени у %PROGRAMDATA%\\Iskra: {0}\n\nМожливо, потрібно запустити програму від імені адміністратора (один раз).", "Could not save tokens to %PROGRAMDATA%\\Iskra: {0}\n\nYou may need to run the application as administrator once.", "Token konnten nicht unter %PROGRAMDATA%\\Iskra gespeichert werden: {0}\n\nMöglicherweise müssen Sie die Anwendung einmal als Administrator starten."),
            ["Update.Prompt"] = new("Натисніть «Перевірити», щоб знайти новий інсталятор Iskra.", "Select Check to find a new Iskra installer.", "Wählen Sie „Prüfen“, um einen neuen Iskra-Installer zu suchen."),
            ["Update.Checking"] = new("Перевірка релізів GitHub...", "Checking GitHub releases...", "GitHub-Versionen werden geprüft..."),
            ["Update.Available.Setup"] = new("Доступна версія {0} ({1}). Відкрийте реліз і запустіть setup EXE після завершення роботи.", "Version {0} ({1}) is available. Open the release and run the setup EXE after finishing work.", "Version {0} ({1}) ist verfügbar. Öffnen Sie die Version und starten Sie nach Arbeitsende die Setup-EXE."),
            ["Update.Available.Download"] = new("Доступна версія {0} ({1}). Відкрийте реліз і завантажте інсталятор.", "Version {0} ({1}) is available. Open the release and download the installer.", "Version {0} ({1}) ist verfügbar. Öffnen Sie die Version und laden Sie den Installer herunter."),
            ["Update.Current"] = new("✓ Поточна версія актуальна: {0}.", "✓ Current version is up to date: {0}.", "✓ Die aktuelle Version ist auf dem neuesten Stand: {0}."),
            ["Update.NoRelease"] = new("У репозиторії Iskra ще немає релізів.", "The Iskra repository has no releases yet.", "Das Iskra-Repository enthält noch keine Versionen."),
            ["Update.NetworkError"] = new("✗ Мережна помилка: {0}", "✗ Network error: {0}", "✗ Netzwerkfehler: {0}"),
            ["Update.ParseError"] = new("✗ Реліз неможливо прочитати: {0}", "✗ Release could not be read: {0}", "✗ Version konnte nicht gelesen werden: {0}"),
            ["Browser.OpenFailed"] = new("✗ Не вдалося відкрити браузер: {0}", "✗ Could not open the browser: {0}", "✗ Browser konnte nicht geöffnet werden: {0}"),
            ["CatalogCache.Empty"] = new("Кеш порожній — натисніть «Перевірити оновлення».", "Cache is empty — select Check for updates.", "Der Cache ist leer — wählen Sie „Nach Updates suchen“."),
            ["CatalogCache.Bad"] = new("✗ Кешований {0}, але підпис не пройшов перевірку.", "✗ {0} is cached, but its signature failed verification.", "✗ {0} ist zwischengespeichert, aber die Signaturprüfung ist fehlgeschlagen."),
            ["CatalogCache.Ready"] = new("✓ {0} · {1} продукт(ів) · згенеровано {2:yyyy-MM-dd HH:mm} UTC", "✓ {0} · {1} product(s) · generated {2:yyyy-MM-dd HH:mm} UTC", "✓ {0} · {1} Produkt(e) · erzeugt {2:yyyy-MM-dd HH:mm} UTC"),
            ["CatalogCache.BackgroundUpdated"] = new("Каталог: оновлено до {0} — натисніть «Перезавантажити» на вкладці Каталог.", "Catalog: updated to {0} — select Reload on the Catalog tab.", "Katalog: auf {0} aktualisiert — wählen Sie „Neu laden“ auf der Registerkarte Katalog."),
            ["CatalogCache.Checking"] = new("Перевірка…", "Checking…", "Wird geprüft…"),
            ["CatalogCache.Updated"] = new("✓ Оновлено до {0}. Перезавантажте каталог щоб застосувати.", "✓ Updated to {0}. Reload the catalog to apply it.", "✓ Auf {0} aktualisiert. Laden Sie den Katalog neu, um die Änderung anzuwenden."),
            ["CatalogCache.Unchanged"] = new("✓ {0} (без змін з попередньої перевірки).", "✓ {0} (unchanged since the previous check).", "✓ {0} (seit der letzten Prüfung unverändert)."),
            ["CatalogCache.Current"] = new("✓ Вже актуально: {0}.", "✓ Already up to date: {0}.", "✓ Bereits aktuell: {0}."),
            ["CatalogCache.NoRelease"] = new("⚠ {0}/{1} поки що не має жодного релізу.", "⚠ {0}/{1} has no releases yet.", "⚠ {0}/{1} enthält noch keine Versionen."),
            ["CatalogCache.BadSignature"] = new("✗ Підпис каталогу не співпадає з ключем у застосунку (можлива атака; кеш не змінено).", "✗ Catalog signature does not match the key in the app (possible attack; cache unchanged).", "✗ Die Katalogsignatur stimmt nicht mit dem Schlüssel in der App überein (möglicher Angriff; Cache unverändert)."),
            ["CatalogCache.AssetsMissing"] = new("✗ Реліз без catalog.json або catalog.json.sig — {0}", "✗ Release is missing catalog.json or catalog.json.sig — {0}", "✗ Version enthält catalog.json oder catalog.json.sig nicht — {0}"),
            ["CatalogCache.ParseError"] = new("✗ Завантажений каталог не вдалось розпарсити: {0}", "✗ Downloaded catalog could not be parsed: {0}", "✗ Heruntergeladener Katalog konnte nicht verarbeitet werden: {0}"),
            ["CatalogCache.Error"] = new("✗ Помилка: {0} — {1}", "✗ Error: {0} — {1}", "✗ Fehler: {0} — {1}"),

            // Device-flow dynamic text.
            ["Device.AccessDenied"] = new("Авторизацію відхилено в браузері.", "Authorization was denied in the browser.", "Die Autorisierung wurde im Browser abgelehnt."),
            ["Device.CodeExpired"] = new("Код пристрою застарів. Спробуйте ще раз.", "The device code expired. Try again.", "Der Gerätecode ist abgelaufen. Versuchen Sie es erneut."),
            ["Device.Error"] = new("✗ Помилка: {0}", "✗ Error: {0}", "✗ Fehler: {0}"),
            ["Device.BrowserFailed"] = new("Не вдалося відкрити браузер: {0}", "Could not open the browser: {0}", "Browser konnte nicht geöffnet werden: {0}"),
            ["Device.CodeCopied"] = new("✓ Код скопійовано. Очікування авторизації…", "✓ Code copied. Waiting for authorization…", "✓ Code kopiert. Warten auf Autorisierung…"),
            ["Device.CopyFailed"] = new("Помилка копіювання: {0}", "Copy error: {0}", "Fehler beim Kopieren: {0}"),

        };

    public static void ApplyLanguage(string? languageCode)
    {
        CultureInfo.CurrentUICulture = IskraLanguages.CultureFor(languageCode);
    }

    public static string Get(string key, params object?[] args)
    {
        if (!Texts.TryGetValue(key, out var value))
            throw new KeyNotFoundException($"Missing Iskra UI text key: {key}");

        var template = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            IskraLanguages.English => value.En,
            IskraLanguages.German => value.De,
            _ => value.Uk,
        };
        return args.Length == 0
            ? template
            : string.Format(CultureInfo.CurrentUICulture, template, args);
    }

    public static string ErrorHint(string? errorCode) =>
        OperatorText.ErrorHint(errorCode, CultureInfo.CurrentUICulture.Name);
}

/// <summary>Loads an embedded localized string while XAML is constructed.</summary>
[MarkupExtensionReturnType(typeof(string))]
internal sealed class TrExtension : MarkupExtension
{
    public TrExtension(string key) => Key = key;

    public string Key { get; }

    public override object ProvideValue(IServiceProvider serviceProvider) => UiText.Get(Key);
}
