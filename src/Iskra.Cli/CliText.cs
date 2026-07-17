using System.Globalization;
using Iskra.Core;

internal sealed record CliLanguageResult(bool Ok, string LanguageCode, string[] Args, string? InvalidValue);

internal static class CliLanguage
{
    public static CliLanguageResult Resolve(string[] args, string? persistedLanguage)
    {
        var stripped = new List<string>(args.Length);
        string? explicitLanguage = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--lang", StringComparison.Ordinal))
            {
                stripped.Add(args[i]);
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return new(false, IskraLanguages.NormalizeOrDefault(persistedLanguage), stripped.ToArray(), null);

            explicitLanguage = args[++i];
        }

        var selectedLanguage = IskraLanguages.NormalizeOrDefault(persistedLanguage);
        if (explicitLanguage is not null)
        {
            if (!IskraLanguages.TryNormalize(explicitLanguage, out var normalized))
                return new(false, selectedLanguage, stripped.ToArray(), explicitLanguage);
            selectedLanguage = normalized;
        }

        return new(
            true,
            selectedLanguage,
            stripped.ToArray(),
            null);
    }
}

internal static class CliText
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Text =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [IskraLanguages.Ukrainian] = Ukrainian(),
            [IskraLanguages.English] = English(),
            [IskraLanguages.German] = German(),
        };

    public static string Get(string key, params object?[] args)
    {
        var language = IskraLanguages.NormalizeOrDefault(CultureInfo.CurrentUICulture.Name);
        if (!Text[language].TryGetValue(key, out var template))
            throw new InvalidOperationException($"Missing CLI text key '{key}' for '{language}'.");
        return args.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, args);
    }

    public static IReadOnlyCollection<string> Keys(string languageCode)
        => Text[IskraLanguages.NormalizeOrDefault(languageCode)].Keys.ToArray();

    private static Dictionary<string, string> Ukrainian() => new(StringComparer.Ordinal)
    {
        ["Language.Invalid"] = "Помилка: --lang підтримує лише uk, en або de (також приймаються uk-UA, en-US і de-DE).",
        ["Lab.Locked"] = "Помилка: лабораторний режим заблоковано. Лише на лабораторній станції задайте {0}=1.",
        ["Catalog.Required"] = "Помилка: операторський режим вимагає підписаний --catalog. Для ручної лабораторної прошивки потрібні --allow-manual-flash і {0}=1.",
        ["Catalog.SideloadUnsigned"] = "Помилка: sideload-каталог не підписано. Для лабораторної перевірки явно додайте --allow-unsigned-catalog.",
        ["Catalog.SignatureVerified"] = "Підпис каталогу: ✓ (Ed25519)",
        ["Catalog.SignatureMissingLab"] = "Підпис каталогу: відсутній (лабораторний режим --allow-unsigned-catalog)",
        ["Catalog.UnsignedRejected"] = "Помилка: каталог не підписано; безпечний режим вимагає Ed25519-підпис.",
        ["Catalog.BadSignature"] = "Помилка: підпис каталогу не співпадає з ключем у застосунку.",
        ["Catalog.NoPublicKey"] = "Помилка: каталог підписано, але у застосунку немає публічного ключа.",
        ["Catalog.SignatureReadFailed"] = "Помилка: не вдалося прочитати файл підпису каталогу.",
        ["Catalog.Error"] = "Помилка каталогу: {0}",
        ["Catalog.Resolved"] = "Каталог: {0} → v{1} ({2}, {3} KB, {4})",
        ["Firmware.CacheHit"] = "  ✓ кеш: {0}",
        ["Auth.Required"] = "Помилка: потрібна авторизація GitHub. Виконайте: Iskra.Cli --login",
        ["Auth.Expired"] = "Помилка: сесія GitHub застаріла (>6 міс без оновлення). Виконайте: Iskra.Cli --login",
        ["Firmware.AssetMissing"] = "Помилка: реліз GitHub не містить файл — {0}",
        ["GitHub.ApiError"] = "Помилка GitHub API ({0}): {1}",
        ["Firmware.DownloadError"] = "Помилка завантаження прошивки: {0}",
        ["Common.ErrorDetails"] = "Помилка: {0}",
        ["Probe.NotFound"] = "Помилка: Black Magic Probe не знайдено.",
        ["Probe.ConnectHint"] = "Підключіть програматор або вкажіть --port COMxx вручну.",
        ["Probe.Detected"] = "Виявлено програматор: {0}{1}",
        ["Probe.Multiple"] = "Знайдено {0} програматорів. Вкажіть --port явно:",
        ["Firmware.NotFound"] = "Помилка: файл прошивки ({0}) не знайдено: {1}",
        ["Firmware.BadFormat"] = "Помилка: файл не є коректним {0}: {1}",
        ["Firmware.ReadFailed"] = "Помилка: не вдалося прочитати файл прошивки: {0}",
        ["Gdb.NotFound"] = "Помилка: arm-none-eabi-gdb не знайдено.",
        ["Gdb.InstallHint"] = "Вкажіть шлях через --gdb-path або повторно запустіть інсталятор Iskra.",
        ["DryRun.Header"] = "=== DRY RUN — gdb не буде запущено ===",
        ["DryRun.CatalogSha"] = "Каталог SHA-256: {0}",
        ["DryRun.HashMatch"] = "Перевірка цілісності: ✓ співпадає",
        ["DryRun.HashMismatch"] = "Перевірка цілісності: ✗ НЕ СПІВПАДАЄ — у реальному запуску буде відмова",
        ["DryRun.HashSkipped"] = "Перевірка цілісності: пропущена (немає очікуваного SHA-256)",
        ["DryRun.Executable"] = "Виконуваний файл: {0}",
        ["Result.Error"] = "  ✗ ПОМИЛКА: {0}",
        ["Result.Details"] = "  Деталі: {0}",
        ["Result.LogWarning"] = "  УВАГА: не вдалося записати в журнал: {0}",
        ["Result.ErrorRaw"] = "Помилка: {0} — {1}",
        ["Flash.Summary"] = "Прошивка: {0} v{1} → {2} ({3})",
        ["Flash.Operator"] = "Оператор: {0} | Партія: {1} | Станція: {2}",
        ["Flash.Running"] = "Виконується...",
        ["Flash.Success"] = "  ✓ ПРОШИВКА УСПІШНА  ({0:F0} мс)",
        ["Flash.Target"] = "  Ціль: {0}",
        ["Flash.Logged"] = "  (записано в журнал: id={0}, {1})",
        ["Auth.StoreUnsupported"] = "Помилка: захищене сховище GitHub-токенів для цієї ОС ще не реалізовано. Не використовуйте незашифрований файл токенів.",
        ["Auth.ClientMissing"] = "Помилка: GitHub App Client ID не налаштовано (зверніться до розробника).",
        ["Auth.RequestCode"] = "Запит коду пристрою GitHub...",
        ["Auth.OpenBrowser"] = "  Відкрийте у браузері: {0}",
        ["Auth.EnterCode"] = "  Введіть код:          {0}",
        ["Auth.Waiting"] = "Очікування авторизації... (таймаут ~{0} хв, Ctrl+C для скасування)",
        ["Auth.Denied"] = "Авторизацію відхилено користувачем.",
        ["Auth.CodeExpired"] = "Код пристрою застарів. Запустіть --login знову.",
        ["Auth.GitHubError"] = "Помилка GitHub: {0}",
        ["Common.Cancelled"] = "Скасовано.",
        ["Auth.SaveFailed"] = "Помилка збереження токенів у {0}: {1}",
        ["Auth.AdminHint"] = "Запустіть від імені адміністратора, якщо проблема в правах доступу до %PROGRAMDATA%.",
        ["Auth.Success"] = "✓ Авторизовано. Токени збережено: {0}",
        ["Auth.AccessHours"] = "  Access token дійсний ~{0} год.",
        ["Auth.RefreshDays"] = "  Refresh token дійсний ~{0} дн.",
        ["Auth.StoreUnavailable"] = "GitHub-токени на цій ОС не зберігаються; захищене сховище ще не реалізовано.",
        ["Auth.AlreadyLoggedOut"] = "Токени не знайдено — вже не авторизовано.",
        ["Auth.DeleteFailed"] = "Помилка видалення {0}: {1}",
        ["Auth.Deleted"] = "Токени видалено: {0}",
        ["Auth.StoreCorrupt"] = "Файл токенів пошкоджено: {0}",
        ["Auth.Reauthenticate"] = "Видаліть і авторизуйтеся знову: Iskra.Cli --logout && Iskra.Cli --login",
        ["Auth.NotSignedIn"] = "Не авторизовано. Виконайте: Iskra.Cli --login",
        ["Auth.File"] = "Файл:              {0}",
        ["Auth.AccessUntil"] = "Access token до:   {0:yyyy-MM-dd HH:mm} UTC ({1})",
        ["Auth.RefreshUntil"] = "Refresh token до:  {0:yyyy-MM-dd HH:mm} UTC ({1})",
        ["Auth.CheckSkipped"] = "(пропускаю перевірку через GitHub — Client ID не налаштовано)",
        ["Auth.ParentheticalNotSignedIn"] = "(не авторизовано)",
        ["Auth.RefreshExpired"] = "Refresh token застарів — --login",
        ["Auth.RefreshFailed"] = "Не вдалося оновити токен: {0}",
        ["Auth.GitHubUser"] = "GitHub користувач: {0}",
        ["Duration.Expired"] = "застарів",
        ["Duration.Months"] = "через ~{0} міс",
        ["Duration.Days"] = "через {0} дн {1} год",
        ["Duration.Hours"] = "через {0} год {1} хв",
        ["Duration.Minutes"] = "через {0} хв",
        ["Logs.Disabled"] = "Вивантаження журналу вимкнено в налаштуваннях (LogShippingEnabled = false).",
        ["Logs.AppMissing"] = "Помилка: GitHub App для журналу не налаштовано (зверніться до розробника).",
        ["Logs.ConfigMissing"] = "(LogShipperAppId / LogShipperInstallationId порожні у GitHubAppConfig)",
        ["Logs.KeyMissing"] = "Помилка: приватний ключ GitHub App не знайдено: {0}",
        ["Logs.KeyHint"] = "Перевстановіть Iskra або вкажіть шлях через --key <path>.",
        ["Logs.Empty"] = "Журнал ще порожній ({0}). Нічого вивантажувати.",
        ["Logs.AllShipped"] = "Всі рядки вже вивантажено.",
        ["Logs.Pending"] = "Знайдено рядків для вивантаження: {0}. Журнал: {1}",
        ["Logs.AuthError"] = "Помилка авторизації GitHub App: {0}",
        ["Logs.UploadError"] = "Помилка вивантаження: {0}",
        ["Logs.Uploaded"] = "✓ Вивантажено рядків: {0}; нових файлів: {1}; оновлено файлів: {2}.",
        ["Logs.Leftover"] = "  (Залишилось рядків: {0}. Запустіть знову, щоб дослати.)",
        ["Firmware.PrivateUnsupported"] = "завантаження приватної прошивки потребує Keychain/libsecret; поки що використайте підписаний локальний каталог або sideload у лабораторії",
        ["Probe.None"] = "Програматори не знайдено.",
        ["Probe.SearchDetail"] = "(шукали USB-пристрої VID 0x1D50 PID 0x6018 — Black Magic Probe)",
        ["Probe.Interfaces"] = "Знайдено інтерфейсів: {0}",
        ["Probe.DefaultPort"] = "Стандартний GDB-порт: {0}",
        ["Doctor.Title"] = "Перевірка станції Iskra",
        ["Doctor.OperatingSystem"] = "Операційна система",
        ["Doctor.CliPathUnknown"] = "не вдалося підтвердити шлях поточного executable",
        ["Doctor.GuiMissing"] = "графічний застосунок не знайдено поруч із CLI",
        ["Doctor.GdbMissing"] = "arm-none-eabi-gdb не знайдено у PATH або за --gdb-path",
        ["Doctor.ProbeMissing"] = "GDB endpoint не знайдено; перевірте USB/udev або вкажіть --port",
        ["Doctor.ProbeMultiple"] = "знайдено GDB endpoints: {0}; виберіть --port явно",
        ["Doctor.CatalogHint"] = "вкажіть --catalog <path> або встановіть вбудований examples/catalog.json",
        ["Doctor.NotFound"] = "не знайдено: {0}",
        ["Doctor.Products"] = "продуктів: {0}; {1}",
        ["Doctor.NoSignature"] = "немає .sig файлу",
        ["Doctor.BadSignature"] = "підпис не збігається з вбудованим ключем",
        ["Doctor.NoPublicKey"] = "немає вбудованого публічного ключа",
        ["Doctor.CatalogReadFailed"] = "не вдалося прочитати catalog або .sig файл",
        ["Doctor.UnexpectedUnsigned"] = "неочікуваний непідписаний catalog",
        ["Doctor.Writable"] = "доступний для запису",
        ["Doctor.NotWritable"] = "немає доступу на запис",
        ["Doctor.NotSignedIn"] = "немає входу; виконайте Iskra.Cli --login перед завантаженням приватних прошивок",
        ["Doctor.RefreshExpired"] = "refresh token застарів; виконайте Iskra.Cli --login",
        ["Doctor.SecureStoreMissing"] = "Keychain/libsecret adapter ще не реалізовано; приватні релізи недоступні",
        ["Doctor.Pass"] = "Результат: PASS, попереджень: {0}.",
        ["Doctor.Fail"] = "Результат: FAIL, помилок: {0}, попереджень: {1}.",
        ["Help"] = UkrainianHelp,
    };

    private static Dictionary<string, string> English()
    {
        var d = new Dictionary<string, string>(Ukrainian(), StringComparer.Ordinal)
        {
            ["Language.Invalid"] = "Error: --lang supports only uk, en, or de (uk-UA, en-US, and de-DE are also accepted).",
            ["Lab.Locked"] = "Error: laboratory mode is locked. Set {0}=1 only on a laboratory station.",
            ["Catalog.Required"] = "Error: operator mode requires a signed --catalog. Manual laboratory flashing requires --allow-manual-flash and {0}=1.",
            ["Catalog.SideloadUnsigned"] = "Error: a sideload catalog is unsigned. For laboratory testing, explicitly add --allow-unsigned-catalog.",
            ["Catalog.SignatureVerified"] = "Catalog signature: ✓ (Ed25519)",
            ["Catalog.SignatureMissingLab"] = "Catalog signature: missing (laboratory mode --allow-unsigned-catalog)",
            ["Catalog.UnsignedRejected"] = "Error: the catalog is unsigned; safe mode requires an Ed25519 signature.",
            ["Catalog.BadSignature"] = "Error: the catalog signature does not match the application key.",
            ["Catalog.NoPublicKey"] = "Error: the catalog is signed, but the application has no public key.",
            ["Catalog.SignatureReadFailed"] = "Error: the catalog signature file could not be read.",
            ["Catalog.Error"] = "Catalog error: {0}",
            ["Catalog.Resolved"] = "Catalog: {0} → v{1} ({2}, {3} KB, {4})",
            ["Firmware.CacheHit"] = "  ✓ cache: {0}",
            ["Auth.Required"] = "Error: GitHub authentication is required. Run: Iskra.Cli --login",
            ["Auth.Expired"] = "Error: the GitHub session has expired (>6 months without refresh). Run: Iskra.Cli --login",
            ["Firmware.AssetMissing"] = "Error: the GitHub release does not contain the file — {0}",
            ["GitHub.ApiError"] = "GitHub API error ({0}): {1}",
            ["Firmware.DownloadError"] = "Firmware download error: {0}",
            ["Common.ErrorDetails"] = "Error: {0}",
            ["Probe.NotFound"] = "Error: Black Magic Probe was not found.",
            ["Probe.ConnectHint"] = "Connect the probe or specify --port COMxx manually.",
            ["Probe.Detected"] = "Probe detected: {0}{1}",
            ["Probe.Multiple"] = "Found {0} probes. Specify --port explicitly:",
            ["Firmware.NotFound"] = "Error: firmware file ({0}) was not found: {1}",
            ["Firmware.BadFormat"] = "Error: the file is not a valid {0}: {1}",
            ["Firmware.ReadFailed"] = "Error: the firmware file could not be read: {0}",
            ["Gdb.NotFound"] = "Error: arm-none-eabi-gdb was not found.",
            ["Gdb.InstallHint"] = "Specify its path with --gdb-path or run the Iskra installer again.",
            ["DryRun.Header"] = "=== DRY RUN — gdb will not be started ===",
            ["DryRun.CatalogSha"] = "Catalog SHA-256: {0}",
            ["DryRun.HashMatch"] = "Integrity check: ✓ matches",
            ["DryRun.HashMismatch"] = "Integrity check: ✗ DOES NOT MATCH — a real run will refuse to flash",
            ["DryRun.HashSkipped"] = "Integrity check: skipped (no expected SHA-256)",
            ["DryRun.Executable"] = "Executable: {0}",
            ["Result.Error"] = "  ✗ ERROR: {0}",
            ["Result.Details"] = "  Details: {0}",
            ["Result.LogWarning"] = "  WARNING: could not write to the log: {0}",
            ["Result.ErrorRaw"] = "Error: {0} — {1}",
            ["Flash.Summary"] = "Firmware: {0} v{1} → {2} ({3})",
            ["Flash.Operator"] = "Operator: {0} | Batch: {1} | Station: {2}",
            ["Flash.Running"] = "Running...",
            ["Flash.Success"] = "  ✓ FLASH SUCCESSFUL  ({0:F0} ms)",
            ["Flash.Target"] = "  Target: {0}",
            ["Flash.Logged"] = "  (written to log: id={0}, {1})",
            ["Auth.StoreUnsupported"] = "Error: secure GitHub token storage is not implemented for this operating system. Do not use an unencrypted token file.",
            ["Auth.ClientMissing"] = "Error: the GitHub App Client ID is not configured (contact the developer).",
            ["Auth.RequestCode"] = "Requesting a GitHub device code...",
            ["Auth.OpenBrowser"] = "  Open in your browser: {0}",
            ["Auth.EnterCode"] = "  Enter the code:       {0}",
            ["Auth.Waiting"] = "Waiting for authorization... (timeout ~{0} min, Ctrl+C to cancel)",
            ["Auth.Denied"] = "Authorization was denied by the user.",
            ["Auth.CodeExpired"] = "The device code has expired. Run --login again.",
            ["Auth.GitHubError"] = "GitHub error: {0}",
            ["Common.Cancelled"] = "Cancelled.",
            ["Auth.SaveFailed"] = "Could not save tokens to {0}: {1}",
            ["Auth.AdminHint"] = "Run as administrator if the problem is access to %PROGRAMDATA%.",
            ["Auth.Success"] = "✓ Authorized. Tokens saved to: {0}",
            ["Auth.AccessHours"] = "  Access token valid for ~{0} h.",
            ["Auth.RefreshDays"] = "  Refresh token valid for ~{0} days.",
            ["Auth.StoreUnavailable"] = "GitHub tokens are not stored on this operating system; secure storage is not implemented yet.",
            ["Auth.AlreadyLoggedOut"] = "No tokens found — already signed out.",
            ["Auth.DeleteFailed"] = "Could not delete {0}: {1}",
            ["Auth.Deleted"] = "Tokens deleted: {0}",
            ["Auth.StoreCorrupt"] = "The token file is corrupt: {0}",
            ["Auth.Reauthenticate"] = "Delete the tokens and sign in again: Iskra.Cli --logout && Iskra.Cli --login",
            ["Auth.NotSignedIn"] = "Not signed in. Run: Iskra.Cli --login",
            ["Auth.File"] = "File:                 {0}",
            ["Auth.AccessUntil"] = "Access token until:   {0:yyyy-MM-dd HH:mm} UTC ({1})",
            ["Auth.RefreshUntil"] = "Refresh token until:  {0:yyyy-MM-dd HH:mm} UTC ({1})",
            ["Auth.CheckSkipped"] = "(skipping the GitHub check — Client ID is not configured)",
            ["Auth.ParentheticalNotSignedIn"] = "(not signed in)",
            ["Auth.RefreshExpired"] = "Refresh token expired — --login",
            ["Auth.RefreshFailed"] = "Could not refresh the token: {0}",
            ["Auth.GitHubUser"] = "GitHub user: {0}",
            ["Duration.Expired"] = "expired",
            ["Duration.Months"] = "in ~{0} months",
            ["Duration.Days"] = "in {0} days {1} h",
            ["Duration.Hours"] = "in {0} h {1} min",
            ["Duration.Minutes"] = "in {0} min",
            ["Logs.Disabled"] = "Log upload is disabled in Settings (LogShippingEnabled = false).",
            ["Logs.AppMissing"] = "Error: the log GitHub App is not configured (contact the developer).",
            ["Logs.ConfigMissing"] = "(LogShipperAppId / LogShipperInstallationId are empty in GitHubAppConfig)",
            ["Logs.KeyMissing"] = "Error: the GitHub App private key was not found: {0}",
            ["Logs.KeyHint"] = "Reinstall Iskra or specify the path with --key <path>.",
            ["Logs.Empty"] = "The log is still empty ({0}). Nothing to upload.",
            ["Logs.AllShipped"] = "All rows have already been uploaded.",
            ["Logs.Pending"] = "Rows pending upload: {0}. Log: {1}",
            ["Logs.AuthError"] = "GitHub App authentication error: {0}",
            ["Logs.UploadError"] = "Upload error: {0}",
            ["Logs.Uploaded"] = "✓ Rows uploaded: {0}; files created: {1}; files updated: {2}.",
            ["Logs.Leftover"] = "  (Rows remaining: {0}. Run the command again to send them.)",
            ["Firmware.PrivateUnsupported"] = "downloading private firmware requires Keychain/libsecret; for now, use a signed local catalog or laboratory sideload",
            ["Probe.None"] = "No probes found.",
            ["Probe.SearchDetail"] = "(searched for USB devices VID 0x1D50 PID 0x6018 — Black Magic Probe)",
            ["Probe.Interfaces"] = "Interfaces found: {0}",
            ["Probe.DefaultPort"] = "Default GDB port: {0}",
            ["Doctor.Title"] = "Iskra station check",
            ["Doctor.OperatingSystem"] = "Operating system",
            ["Doctor.CliPathUnknown"] = "could not confirm the path of the current executable",
            ["Doctor.GuiMissing"] = "graphical application was not found beside the CLI",
            ["Doctor.GdbMissing"] = "arm-none-eabi-gdb was not found in PATH or at --gdb-path",
            ["Doctor.ProbeMissing"] = "GDB endpoint not found; check USB/udev or specify --port",
            ["Doctor.ProbeMultiple"] = "GDB endpoints found: {0}; select one explicitly with --port",
            ["Doctor.CatalogHint"] = "specify --catalog <path> or install the bundled examples/catalog.json",
            ["Doctor.NotFound"] = "not found: {0}",
            ["Doctor.Products"] = "products: {0}; {1}",
            ["Doctor.NoSignature"] = "the .sig file is missing",
            ["Doctor.BadSignature"] = "the signature does not match the embedded key",
            ["Doctor.NoPublicKey"] = "the embedded public key is missing",
            ["Doctor.CatalogReadFailed"] = "could not read the catalog or .sig file",
            ["Doctor.UnexpectedUnsigned"] = "unexpected unsigned catalog",
            ["Doctor.Writable"] = "writable",
            ["Doctor.NotWritable"] = "not writable",
            ["Doctor.NotSignedIn"] = "not signed in; run Iskra.Cli --login before downloading private firmware",
            ["Doctor.RefreshExpired"] = "refresh token expired; run Iskra.Cli --login",
            ["Doctor.SecureStoreMissing"] = "Keychain/libsecret adapter is not implemented; private releases are unavailable",
            ["Doctor.Pass"] = "Result: PASS, warnings: {0}.",
            ["Doctor.Fail"] = "Result: FAIL, errors: {0}, warnings: {1}.",
            ["Help"] = EnglishHelp,
        };
        return d;
    }

    private static Dictionary<string, string> German()
    {
        var d = new Dictionary<string, string>(English(), StringComparer.Ordinal)
        {
            ["Language.Invalid"] = "Fehler: --lang unterstützt nur uk, en oder de (uk-UA, en-US und de-DE werden ebenfalls akzeptiert).",
            ["Lab.Locked"] = "Fehler: Der Labormodus ist gesperrt. Setzen Sie {0}=1 ausschließlich auf einer Laborstation.",
            ["Catalog.Required"] = "Fehler: Der Bedienermodus erfordert einen signierten --catalog. Manuelles Flashen im Labor erfordert --allow-manual-flash und {0}=1.",
            ["Catalog.SideloadUnsigned"] = "Fehler: Der Sideload-Katalog ist nicht signiert. Fügen Sie für Labortests ausdrücklich --allow-unsigned-catalog hinzu.",
            ["Catalog.SignatureVerified"] = "Katalogsignatur: ✓ (Ed25519)",
            ["Catalog.SignatureMissingLab"] = "Katalogsignatur: fehlt (Labormodus --allow-unsigned-catalog)",
            ["Catalog.UnsignedRejected"] = "Fehler: Der Katalog ist nicht signiert; der sichere Modus erfordert eine Ed25519-Signatur.",
            ["Catalog.BadSignature"] = "Fehler: Die Katalogsignatur stimmt nicht mit dem Anwendungsschlüssel überein.",
            ["Catalog.NoPublicKey"] = "Fehler: Der Katalog ist signiert, aber die Anwendung enthält keinen öffentlichen Schlüssel.",
            ["Catalog.SignatureReadFailed"] = "Fehler: Die Katalogsignaturdatei konnte nicht gelesen werden.",
            ["Catalog.Error"] = "Katalogfehler: {0}",
            ["Catalog.Resolved"] = "Katalog: {0} → v{1} ({2}, {3} KB, {4})",
            ["Firmware.CacheHit"] = "  ✓ Cache: {0}",
            ["Auth.Required"] = "Fehler: Eine GitHub-Authentifizierung ist erforderlich. Führen Sie aus: Iskra.Cli --login",
            ["Auth.Expired"] = "Fehler: Die GitHub-Sitzung ist abgelaufen (>6 Monate ohne Aktualisierung). Führen Sie aus: Iskra.Cli --login",
            ["Firmware.AssetMissing"] = "Fehler: Das GitHub-Release enthält die Datei nicht — {0}",
            ["GitHub.ApiError"] = "GitHub-API-Fehler ({0}): {1}",
            ["Firmware.DownloadError"] = "Fehler beim Herunterladen der Firmware: {0}",
            ["Common.ErrorDetails"] = "Fehler: {0}",
            ["Probe.NotFound"] = "Fehler: Black Magic Probe wurde nicht gefunden.",
            ["Probe.ConnectHint"] = "Schließen Sie den Programmer an oder geben Sie --port COMxx manuell an.",
            ["Probe.Detected"] = "Programmer erkannt: {0}{1}",
            ["Probe.Multiple"] = "Es wurden {0} Programmer gefunden. Geben Sie --port ausdrücklich an:",
            ["Firmware.NotFound"] = "Fehler: Die Firmwaredatei ({0}) wurde nicht gefunden: {1}",
            ["Firmware.BadFormat"] = "Fehler: Die Datei ist keine gültige {0}-Datei: {1}",
            ["Firmware.ReadFailed"] = "Fehler: Die Firmwaredatei konnte nicht gelesen werden: {0}",
            ["Gdb.NotFound"] = "Fehler: arm-none-eabi-gdb wurde nicht gefunden.",
            ["Gdb.InstallHint"] = "Geben Sie den Pfad mit --gdb-path an oder führen Sie den Iskra-Installer erneut aus.",
            ["DryRun.Header"] = "=== TESTLAUF — gdb wird nicht gestartet ===",
            ["DryRun.CatalogSha"] = "Katalog-SHA-256: {0}",
            ["DryRun.HashMatch"] = "Integritätsprüfung: ✓ stimmt überein",
            ["DryRun.HashMismatch"] = "Integritätsprüfung: ✗ STIMMT NICHT ÜBEREIN — ein echter Lauf verweigert das Flashen",
            ["DryRun.HashSkipped"] = "Integritätsprüfung: übersprungen (kein erwarteter SHA-256-Wert)",
            ["DryRun.Executable"] = "Ausführbare Datei: {0}",
            ["Result.Error"] = "  ✗ FEHLER: {0}",
            ["Result.Details"] = "  Details: {0}",
            ["Result.LogWarning"] = "  WARNUNG: Das Protokoll konnte nicht geschrieben werden: {0}",
            ["Result.ErrorRaw"] = "Fehler: {0} — {1}",
            ["Flash.Summary"] = "Firmware: {0} v{1} → {2} ({3})",
            ["Flash.Operator"] = "Bediener: {0} | Charge: {1} | Station: {2}",
            ["Flash.Running"] = "Vorgang läuft...",
            ["Flash.Success"] = "  ✓ FLASHEN ERFOLGREICH  ({0:F0} ms)",
            ["Flash.Target"] = "  Ziel: {0}",
            ["Flash.Logged"] = "  (in das Protokoll geschrieben: id={0}, {1})",
            ["Auth.StoreUnsupported"] = "Fehler: Die sichere Speicherung von GitHub-Token ist für dieses Betriebssystem noch nicht implementiert. Verwenden Sie keine unverschlüsselte Tokendatei.",
            ["Auth.ClientMissing"] = "Fehler: Die GitHub-App-Client-ID ist nicht konfiguriert (wenden Sie sich an den Entwickler).",
            ["Auth.RequestCode"] = "GitHub-Gerätecode wird angefordert...",
            ["Auth.OpenBrowser"] = "  Im Browser öffnen: {0}",
            ["Auth.EnterCode"] = "  Code eingeben:      {0}",
            ["Auth.Waiting"] = "Autorisierung wird erwartet... (Zeitlimit ~{0} Min., Strg+C zum Abbrechen)",
            ["Auth.Denied"] = "Die Autorisierung wurde vom Benutzer abgelehnt.",
            ["Auth.CodeExpired"] = "Der Gerätecode ist abgelaufen. Führen Sie --login erneut aus.",
            ["Auth.GitHubError"] = "GitHub-Fehler: {0}",
            ["Common.Cancelled"] = "Abgebrochen.",
            ["Auth.SaveFailed"] = "Die Token konnten nicht unter {0} gespeichert werden: {1}",
            ["Auth.AdminHint"] = "Führen Sie die Anwendung als Administrator aus, wenn der Zugriff auf %PROGRAMDATA% das Problem verursacht.",
            ["Auth.Success"] = "✓ Autorisiert. Token gespeichert unter: {0}",
            ["Auth.AccessHours"] = "  Access-Token ungefähr {0} Std. gültig.",
            ["Auth.RefreshDays"] = "  Refresh-Token ungefähr {0} Tage gültig.",
            ["Auth.StoreUnavailable"] = "GitHub-Token werden auf diesem Betriebssystem nicht gespeichert; ein sicherer Speicher ist noch nicht implementiert.",
            ["Auth.AlreadyLoggedOut"] = "Keine Token gefunden — Sie sind bereits abgemeldet.",
            ["Auth.DeleteFailed"] = "{0} konnte nicht gelöscht werden: {1}",
            ["Auth.Deleted"] = "Token gelöscht: {0}",
            ["Auth.StoreCorrupt"] = "Die Tokendatei ist beschädigt: {0}",
            ["Auth.Reauthenticate"] = "Löschen Sie die Token und melden Sie sich erneut an: Iskra.Cli --logout && Iskra.Cli --login",
            ["Auth.NotSignedIn"] = "Nicht angemeldet. Führen Sie aus: Iskra.Cli --login",
            ["Auth.File"] = "Datei:                {0}",
            ["Auth.AccessUntil"] = "Access-Token bis:     {0:yyyy-MM-dd HH:mm} UTC ({1})",
            ["Auth.RefreshUntil"] = "Refresh-Token bis:    {0:yyyy-MM-dd HH:mm} UTC ({1})",
            ["Auth.CheckSkipped"] = "(GitHub-Prüfung wird übersprungen — die Client-ID ist nicht konfiguriert)",
            ["Auth.ParentheticalNotSignedIn"] = "(nicht angemeldet)",
            ["Auth.RefreshExpired"] = "Refresh-Token abgelaufen — --login",
            ["Auth.RefreshFailed"] = "Das Token konnte nicht aktualisiert werden: {0}",
            ["Auth.GitHubUser"] = "GitHub-Benutzer: {0}",
            ["Duration.Expired"] = "abgelaufen",
            ["Duration.Months"] = "in ~{0} Monaten",
            ["Duration.Days"] = "in {0} Tagen {1} Std.",
            ["Duration.Hours"] = "in {0} Std. {1} Min.",
            ["Duration.Minutes"] = "in {0} Min.",
            ["Logs.Disabled"] = "Der Protokoll-Upload ist in den Einstellungen deaktiviert (LogShippingEnabled = false).",
            ["Logs.AppMissing"] = "Fehler: Die GitHub App für das Protokoll ist nicht konfiguriert (wenden Sie sich an den Entwickler).",
            ["Logs.ConfigMissing"] = "(LogShipperAppId / LogShipperInstallationId sind in GitHubAppConfig leer)",
            ["Logs.KeyMissing"] = "Fehler: Der private Schlüssel der GitHub App wurde nicht gefunden: {0}",
            ["Logs.KeyHint"] = "Installieren Sie Iskra erneut oder geben Sie den Pfad mit --key <path> an.",
            ["Logs.Empty"] = "Das Protokoll ist noch leer ({0}). Es gibt nichts hochzuladen.",
            ["Logs.AllShipped"] = "Alle Zeilen wurden bereits hochgeladen.",
            ["Logs.Pending"] = "Ausstehende Zeilen: {0}. Protokoll: {1}",
            ["Logs.AuthError"] = "Authentifizierungsfehler der GitHub App: {0}",
            ["Logs.UploadError"] = "Upload-Fehler: {0}",
            ["Logs.Uploaded"] = "✓ Hochgeladene Zeilen: {0}; neue Dateien: {1}; aktualisierte Dateien: {2}.",
            ["Logs.Leftover"] = "  (Verbleibende Zeilen: {0}. Führen Sie den Befehl erneut aus.)",
            ["Firmware.PrivateUnsupported"] = "das Herunterladen privater Firmware erfordert Keychain/libsecret; verwenden Sie vorerst einen signierten lokalen Katalog oder Sideload im Labor",
            ["Probe.None"] = "Keine Programmer gefunden.",
            ["Probe.SearchDetail"] = "(gesucht wurden USB-Geräte VID 0x1D50 PID 0x6018 — Black Magic Probe)",
            ["Probe.Interfaces"] = "Gefundene Schnittstellen: {0}",
            ["Probe.DefaultPort"] = "Standard-GDB-Port: {0}",
            ["Doctor.Title"] = "Prüfung der Iskra-Station",
            ["Doctor.OperatingSystem"] = "Betriebssystem",
            ["Doctor.CliPathUnknown"] = "der Pfad der aktuellen ausführbaren Datei konnte nicht bestätigt werden",
            ["Doctor.GuiMissing"] = "die grafische Anwendung wurde nicht neben der CLI gefunden",
            ["Doctor.GdbMissing"] = "arm-none-eabi-gdb wurde weder in PATH noch unter --gdb-path gefunden",
            ["Doctor.ProbeMissing"] = "GDB-Endpunkt nicht gefunden; prüfen Sie USB/udev oder geben Sie --port an",
            ["Doctor.ProbeMultiple"] = "gefundene GDB-Endpunkte: {0}; wählen Sie mit --port ausdrücklich einen aus",
            ["Doctor.CatalogHint"] = "geben Sie --catalog <path> an oder installieren Sie examples/catalog.json",
            ["Doctor.NotFound"] = "nicht gefunden: {0}",
            ["Doctor.Products"] = "Produkte: {0}; {1}",
            ["Doctor.NoSignature"] = "die .sig-Datei fehlt",
            ["Doctor.BadSignature"] = "die Signatur stimmt nicht mit dem eingebetteten Schlüssel überein",
            ["Doctor.NoPublicKey"] = "der eingebettete öffentliche Schlüssel fehlt",
            ["Doctor.CatalogReadFailed"] = "der Katalog oder die .sig-Datei konnte nicht gelesen werden",
            ["Doctor.UnexpectedUnsigned"] = "unerwarteter unsignierter Katalog",
            ["Doctor.Writable"] = "beschreibbar",
            ["Doctor.NotWritable"] = "nicht beschreibbar",
            ["Doctor.NotSignedIn"] = "nicht angemeldet; führen Sie vor dem Herunterladen privater Firmware Iskra.Cli --login aus",
            ["Doctor.RefreshExpired"] = "Refresh-Token abgelaufen; führen Sie Iskra.Cli --login aus",
            ["Doctor.SecureStoreMissing"] = "Keychain/libsecret ist noch nicht implementiert; private Releases sind nicht verfügbar",
            ["Doctor.Pass"] = "Ergebnis: PASS, Warnungen: {0}.",
            ["Doctor.Fail"] = "Ergebnis: FAIL, Fehler: {0}, Warnungen: {1}.",
            ["Help"] = GermanHelp,
        };
        return d;
    }

    private const string UkrainianHelp = """
        Iskra.Cli — масова прошивка через Black Magic Probe

        Мова: [--lang {uk|en|de}] (перевизначає збережене налаштування лише для цього запуску)

        Використання (каталог, рекомендований режим для операторів):
          Iskra.Cli --catalog <path> --product <id>
                    --operator <name> --batch <id>
                    [--firmware-version <ver>] [--port <endpoint>] [...]

        Використання (sideload для лабораторних ELF/HEX):
          Iskra.Cli --allow-unsigned-catalog --sideload-dir <folder>
                    --product <id> --operator <name> --batch <id>
                    [--firmware-version <ver>] [--dry-run]

        Використання (повний ручний режим для розробки):
          Iskra.Cli --allow-manual-flash --elf <path>
                    --product <id> --target <bmp-match> --flash-kb <N>
                    --operator <name> --batch <id> [--port <endpoint>]
                    [--station-id <id>] [--firmware-version <ver>]
                    [--firmware-sha256 <hex>] [--firmware-kind {elf|hex}]
                    [--power {probe|external}] [--freq <hz>]
                    [--connect-reset] [--timeout <sec>]
                    [--gdb-path <path>] [--db-path <path>] [--dry-run]

          Iskra.Cli --list-probes    показати підключені програматори
          Iskra.Cli --doctor         перевірити готовність станції
          Iskra.Cli --help           ця довідка

        Авторизація GitHub:
          Iskra.Cli --login          авторизація OAuth Device Flow
          Iskra.Cli --logout         видалити збережені токени
          Iskra.Cli --whoami         показати GitHub-користувача та строки дії

        Хмарний журнал:
          Iskra.Cli --ship-logs-now [--key <pem>] [--db-path <db>]
                    одноразово надіслати всі несинхронізовані рядки до iskra-logs

        Безпека каталогу:
          Ed25519-підпис .sig поруч із catalog.json обов'язковий.
          --allow-unsigned-catalog і --allow-manual-flash є лабораторними
          перемикачами та також вимагають ISKRA_LAB_ALLOW_UNSIGNED_CATALOG=1.

        Обов'язкові без каталогу: --elf, --product, --target, --flash-kb,
        --operator, --batch.

        Типові значення: --power external, --freq 1000000,
        --connect-reset off, --firmware-kind elf, --timeout 15,
        --station-id <hostname>, --gdb-path <auto-detect>,
        --db-path ./flash_log.db.

        Коди виходу: 0 PASS; 1 FAIL; 2 аргументи/каталог; 3 probe/gdb;
        4 файл прошивки; 5 GitHub/завантаження.
        """;

    private const string EnglishHelp = """
        Iskra.Cli — mass flashing through Black Magic Probe

        Language: [--lang {uk|en|de}] (overrides the saved setting for this run only)

        Usage (catalog, recommended operator mode):
          Iskra.Cli --catalog <path> --product <id>
                    --operator <name> --batch <id>
                    [--firmware-version <ver>] [--port <endpoint>] [...]

        Usage (laboratory ELF/HEX sideload):
          Iskra.Cli --allow-unsigned-catalog --sideload-dir <folder>
                    --product <id> --operator <name> --batch <id>
                    [--firmware-version <ver>] [--dry-run]

        Usage (full manual development mode):
          Iskra.Cli --allow-manual-flash --elf <path>
                    --product <id> --target <bmp-match> --flash-kb <N>
                    --operator <name> --batch <id> [--port <endpoint>]
                    [--station-id <id>] [--firmware-version <ver>]
                    [--firmware-sha256 <hex>] [--firmware-kind {elf|hex}]
                    [--power {probe|external}] [--freq <hz>]
                    [--connect-reset] [--timeout <sec>]
                    [--gdb-path <path>] [--db-path <path>] [--dry-run]

          Iskra.Cli --list-probes    show connected probes
          Iskra.Cli --doctor         check station readiness
          Iskra.Cli --help           show this help

        GitHub authentication:
          Iskra.Cli --login          OAuth Device Flow authentication
          Iskra.Cli --logout         delete saved tokens
          Iskra.Cli --whoami         show the GitHub user and token validity

        Cloud log:
          Iskra.Cli --ship-logs-now [--key <pem>] [--db-path <db>]
                    send all unsynchronized rows to iskra-logs once

        Catalog security:
          An Ed25519 .sig beside catalog.json is required.
          --allow-unsigned-catalog and --allow-manual-flash are laboratory
          switches and also require ISKRA_LAB_ALLOW_UNSIGNED_CATALOG=1.

        Required without a catalog: --elf, --product, --target, --flash-kb,
        --operator, --batch.

        Defaults: --power external, --freq 1000000, --connect-reset off,
        --firmware-kind elf, --timeout 15, --station-id <hostname>,
        --gdb-path <auto-detect>, --db-path ./flash_log.db.

        Exit codes: 0 PASS; 1 FAIL; 2 arguments/catalog; 3 probe/gdb;
        4 firmware file; 5 GitHub/download.
        """;

    private const string GermanHelp = """
        Iskra.Cli — Serienprogrammierung mit Black Magic Probe

        Sprache: [--lang {uk|en|de}] (überschreibt die gespeicherte Einstellung nur für diesen Lauf)

        Verwendung (Katalog, empfohlener Bedienermodus):
          Iskra.Cli --catalog <path> --product <id>
                    --operator <name> --batch <id>
                    [--firmware-version <ver>] [--port <endpoint>] [...]

        Verwendung (ELF/HEX-Sideload im Labor):
          Iskra.Cli --allow-unsigned-catalog --sideload-dir <folder>
                    --product <id> --operator <name> --batch <id>
                    [--firmware-version <ver>] [--dry-run]

        Verwendung (vollständiger manueller Entwicklungsmodus):
          Iskra.Cli --allow-manual-flash --elf <path>
                    --product <id> --target <bmp-match> --flash-kb <N>
                    --operator <name> --batch <id> [--port <endpoint>]
                    [--station-id <id>] [--firmware-version <ver>]
                    [--firmware-sha256 <hex>] [--firmware-kind {elf|hex}]
                    [--power {probe|external}] [--freq <hz>]
                    [--connect-reset] [--timeout <sec>]
                    [--gdb-path <path>] [--db-path <path>] [--dry-run]

          Iskra.Cli --list-probes    angeschlossene Programmer anzeigen
          Iskra.Cli --doctor         Bereitschaft der Station prüfen
          Iskra.Cli --help           diese Hilfe anzeigen

        GitHub-Authentifizierung:
          Iskra.Cli --login          OAuth Device Flow-Authentifizierung
          Iskra.Cli --logout         gespeicherte Token löschen
          Iskra.Cli --whoami         GitHub-Benutzer und Tokengültigkeit anzeigen

        Cloud-Protokoll:
          Iskra.Cli --ship-logs-now [--key <pem>] [--db-path <db>]
                    alle unsynchronisierten Zeilen einmalig an iskra-logs senden

        Katalogsicherheit:
          Eine Ed25519-.sig-Datei neben catalog.json ist erforderlich.
          --allow-unsigned-catalog und --allow-manual-flash sind ausschließlich
          für das Labor vorgesehen und erfordern zusätzlich
          ISKRA_LAB_ALLOW_UNSIGNED_CATALOG=1.

        Ohne Katalog erforderlich: --elf, --product, --target, --flash-kb,
        --operator, --batch.

        Standardwerte: --power external, --freq 1000000, --connect-reset off,
        --firmware-kind elf, --timeout 15, --station-id <hostname>,
        --gdb-path <auto-detect>, --db-path ./flash_log.db.

        Exit-Codes: 0 PASS; 1 FAIL; 2 Argumente/Katalog; 3 Programmer/gdb;
        4 Firmwaredatei; 5 GitHub/Download.
        """;
}
