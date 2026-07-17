using System.Globalization;
using Iskra.Core;

namespace Iskra.Application.Localization;

/// <summary>
/// Shared operator-facing text that must be identical across desktop clients
/// and the CLI. Core error codes and diagnostics remain language-neutral.
/// </summary>
public static class OperatorText
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ErrorHints =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [IskraLanguages.Ukrainian] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["E_PROBE_NOT_FOUND"] = "Програматор Black Magic не знайдено. Перевірте USB-кабель і COM-порт.",
                ["E_PROBE_BUSY"] = "Програматор зайнятий іншим процесом. Закрийте інші програми та повторіть.",
                ["E_SCAN_NO_TARGET"] = "Мікроконтролер не виявлено. Перевірте підключення SWD і живлення плати.",
                ["E_MULTIPLE_TARGETS"] = "Виявлено кілька SWD-цілей. Від'єднайте зайві плати та повторіть.",
                ["E_TARGET_MISMATCH"] = "Підключено невірну плату. Зразок не відповідає прошивці.",
                ["E_ATTACH_FAILED"] = "Не вдалося приєднатися до цілі. Перевірте живлення плати.",
                ["E_LOAD_FAILED"] = "Помилка під час прошивки. Перевірте з'єднання і повторіть.",
                ["E_VERIFY_MISMATCH"] = "Перевірка прошивки не пройдена. Прошивка пошкоджена або не записалась.",
                ["E_TIMEOUT"] = "Час очікування вичерпано. Перевірте з'єднання та програматор.",
                ["E_GDB_CRASHED"] = "Внутрішня помилка gdb. Перезапустіть станцію та повторіть.",
                ["E_FW_HASH_MISMATCH"] = "Контрольна сума прошивки не співпадає з каталогом. Не прошивати.",
                ["E_BATCH_REQUIRED"] = "Введіть ID партії або вимкніть режим партій у налаштуваннях.",
                ["E_BATCH_LOCKED"] = "Партію вже почато з іншою прошивкою. Завершіть партію або змініть її ID.",
                ["E_BATCH_LOCK_CHECK_FAILED"] = "Не вдалося безпечно заблокувати партію. Прошивку зупинено; зверніться до інженера.",
                ["E_ELF_NOT_FOUND"] = "Файл прошивки (ELF) не знайдено за вказаним шляхом.",
                ["E_FW_NOT_FOUND"] = "Файл прошивки не знайдено за вказаним шляхом.",
                ["E_FW_BAD_FORMAT"] = "Файл прошивки має невірний формат для вибраного релізу.",
                ["E_FW_READ_FAILED"] = "Не вдалося прочитати файл прошивки. Перевірте права доступу та носій.",
                ["E_INTERNAL"] = "Внутрішня помилка застосунку. Перезапустіть програму.",
                ["E_NOT_SIGNED_IN"] = "Потрібен вхід GitHub. Відкрийте Налаштування → Авторизація GitHub → Увійти.",
                ["E_AUTH_EXPIRED"] = "Сесія GitHub застаріла (>6 міс). Увійдіть знову у Налаштуваннях.",
                ["E_FW_DOWNLOAD_FAILED"] = "Не вдалося завантажити прошивку з GitHub. Перевірте мережу.",
                ["E_ASSET_NOT_FOUND"] = "У релізі GitHub немає очікуваного файлу прошивки. Зверніться до інженера.",
                ["E_RELEASE_REVOKED"] = "Цю версію прошивки відкликано в каталозі. Оновіть каталог і виберіть іншу версію.",
            },
            [IskraLanguages.English] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["E_PROBE_NOT_FOUND"] = "Black Magic Probe was not found. Check the USB cable and COM port.",
                ["E_PROBE_BUSY"] = "The probe is busy in another process. Close other applications and try again.",
                ["E_SCAN_NO_TARGET"] = "No microcontroller was detected. Check the SWD connection and board power.",
                ["E_MULTIPLE_TARGETS"] = "Multiple SWD targets were detected. Disconnect extra boards and try again.",
                ["E_TARGET_MISMATCH"] = "The wrong board is connected. The device does not match the firmware.",
                ["E_ATTACH_FAILED"] = "Could not attach to the target. Check the board power.",
                ["E_LOAD_FAILED"] = "Flashing failed. Check the connection and try again.",
                ["E_VERIFY_MISMATCH"] = "Firmware verification failed. The firmware is corrupt or was not written correctly.",
                ["E_TIMEOUT"] = "The operation timed out. Check the connection and the probe.",
                ["E_GDB_CRASHED"] = "An internal gdb error occurred. Restart the station and try again.",
                ["E_FW_HASH_MISMATCH"] = "The firmware checksum does not match the catalog. Do not flash this file.",
                ["E_BATCH_REQUIRED"] = "Enter a batch ID or disable batch mode in Settings.",
                ["E_BATCH_LOCKED"] = "This batch was started with different firmware. Finish the batch or change its ID.",
                ["E_BATCH_LOCK_CHECK_FAILED"] = "The batch could not be locked safely. Flashing was stopped; contact an engineer.",
                ["E_ELF_NOT_FOUND"] = "The firmware file (ELF) was not found at the specified path.",
                ["E_FW_NOT_FOUND"] = "The firmware file was not found at the specified path.",
                ["E_FW_BAD_FORMAT"] = "The firmware file has the wrong format for the selected release.",
                ["E_FW_READ_FAILED"] = "The firmware file could not be read. Check permissions and the storage device.",
                ["E_INTERNAL"] = "An internal application error occurred. Restart the application.",
                ["E_NOT_SIGNED_IN"] = "GitHub sign-in is required. Open Settings → GitHub authentication → Sign in.",
                ["E_AUTH_EXPIRED"] = "The GitHub session has expired (>6 months). Sign in again in Settings.",
                ["E_FW_DOWNLOAD_FAILED"] = "The firmware could not be downloaded from GitHub. Check the network connection.",
                ["E_ASSET_NOT_FOUND"] = "The GitHub release does not contain the expected firmware file. Contact an engineer.",
                ["E_RELEASE_REVOKED"] = "This firmware version has been revoked in the catalog. Update the catalog and select another version.",
            },
            [IskraLanguages.German] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["E_PROBE_NOT_FOUND"] = "Black Magic Probe wurde nicht gefunden. Prüfen Sie das USB-Kabel und den COM-Port.",
                ["E_PROBE_BUSY"] = "Der Programmer wird von einem anderen Prozess verwendet. Schließen Sie andere Anwendungen und versuchen Sie es erneut.",
                ["E_SCAN_NO_TARGET"] = "Es wurde kein Mikrocontroller erkannt. Prüfen Sie die SWD-Verbindung und die Stromversorgung der Platine.",
                ["E_MULTIPLE_TARGETS"] = "Es wurden mehrere SWD-Ziele erkannt. Trennen Sie zusätzliche Platinen und versuchen Sie es erneut.",
                ["E_TARGET_MISMATCH"] = "Die falsche Platine ist angeschlossen. Das Gerät passt nicht zur Firmware.",
                ["E_ATTACH_FAILED"] = "Die Verbindung zum Ziel konnte nicht hergestellt werden. Prüfen Sie die Stromversorgung der Platine.",
                ["E_LOAD_FAILED"] = "Das Flashen ist fehlgeschlagen. Prüfen Sie die Verbindung und versuchen Sie es erneut.",
                ["E_VERIFY_MISMATCH"] = "Die Firmwareprüfung ist fehlgeschlagen. Die Firmware ist beschädigt oder wurde nicht korrekt geschrieben.",
                ["E_TIMEOUT"] = "Die Zeitüberschreitung wurde erreicht. Prüfen Sie die Verbindung und den Programmer.",
                ["E_GDB_CRASHED"] = "Ein interner gdb-Fehler ist aufgetreten. Starten Sie die Station neu und versuchen Sie es erneut.",
                ["E_FW_HASH_MISMATCH"] = "Die Firmware-Prüfsumme stimmt nicht mit dem Katalog überein. Flashen Sie diese Datei nicht.",
                ["E_BATCH_REQUIRED"] = "Geben Sie eine Chargen-ID ein oder deaktivieren Sie den Chargenmodus in den Einstellungen.",
                ["E_BATCH_LOCKED"] = "Diese Charge wurde mit einer anderen Firmware begonnen. Schließen Sie die Charge ab oder ändern Sie ihre ID.",
                ["E_BATCH_LOCK_CHECK_FAILED"] = "Die Charge konnte nicht sicher gesperrt werden. Das Flashen wurde gestoppt; wenden Sie sich an einen Techniker.",
                ["E_ELF_NOT_FOUND"] = "Die Firmwaredatei (ELF) wurde unter dem angegebenen Pfad nicht gefunden.",
                ["E_FW_NOT_FOUND"] = "Die Firmwaredatei wurde unter dem angegebenen Pfad nicht gefunden.",
                ["E_FW_BAD_FORMAT"] = "Die Firmwaredatei hat das falsche Format für das ausgewählte Release.",
                ["E_FW_READ_FAILED"] = "Die Firmwaredatei konnte nicht gelesen werden. Prüfen Sie Berechtigungen und Datenträger.",
                ["E_INTERNAL"] = "Ein interner Anwendungsfehler ist aufgetreten. Starten Sie die Anwendung neu.",
                ["E_NOT_SIGNED_IN"] = "Eine GitHub-Anmeldung ist erforderlich. Öffnen Sie Einstellungen → GitHub-Authentifizierung → Anmelden.",
                ["E_AUTH_EXPIRED"] = "Die GitHub-Sitzung ist abgelaufen (>6 Monate). Melden Sie sich in den Einstellungen erneut an.",
                ["E_FW_DOWNLOAD_FAILED"] = "Die Firmware konnte nicht von GitHub heruntergeladen werden. Prüfen Sie die Netzwerkverbindung.",
                ["E_ASSET_NOT_FOUND"] = "Das GitHub-Release enthält nicht die erwartete Firmwaredatei. Wenden Sie sich an einen Techniker.",
                ["E_RELEASE_REVOKED"] = "Diese Firmwareversion wurde im Katalog gesperrt. Aktualisieren Sie den Katalog und wählen Sie eine andere Version.",
            },
        };

    public static string ErrorHint(string? errorCode, string? languageCode = null)
    {
        var language = languageCode is null
            ? IskraLanguages.NormalizeOrDefault(CultureInfo.CurrentUICulture.Name)
            : IskraLanguages.NormalizeOrDefault(languageCode);
        var hints = ErrorHints[language];
        if (errorCode is not null && hints.TryGetValue(errorCode, out var hint))
            return hint;

        return language switch
        {
            IskraLanguages.English => "Unknown error. Contact an engineer.",
            IskraLanguages.German => "Unbekannter Fehler. Wenden Sie sich an einen Techniker.",
            _ => "Невідома помилка. Зверніться до інженера.",
        };
    }

    public static IReadOnlyCollection<string> ErrorCodes(string? languageCode = null)
    {
        var language = IskraLanguages.NormalizeOrDefault(languageCode);
        return ErrorHints[language].Keys.ToArray();
    }
}
