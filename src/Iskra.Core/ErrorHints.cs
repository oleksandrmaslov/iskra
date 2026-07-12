namespace Iskra.Core;

/// <summary>
/// Ukrainian one-line operator hints for each E_* code. UI and CLI render these
/// when a FAIL outcome surfaces. Error codes themselves stay ASCII for logs.
/// </summary>
public static class ErrorHints
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.Ordinal)
    {
        ["E_PROBE_NOT_FOUND"]  = "Програматор Black Magic не знайдено. Перевірте USB-кабель і COM-порт.",
        ["E_PROBE_BUSY"]       = "Програматор зайнятий іншим процесом. Закрийте інші програми та повторіть.",
        ["E_SCAN_NO_TARGET"]   = "Мікроконтролер не виявлено. Перевірте підключення SWD і живлення плати.",
        ["E_MULTIPLE_TARGETS"] = "Виявлено кілька SWD-цілей. Від'єднайте зайві плати та повторіть.",
        ["E_TARGET_MISMATCH"]  = "Підключено невірну плату. Зразок не відповідає прошивці.",
        ["E_ATTACH_FAILED"]    = "Не вдалося приєднатися до цілі. Перевірте живлення плати.",
        ["E_LOAD_FAILED"]      = "Помилка під час прошивки. Перевірте з'єднання і повторіть.",
        ["E_VERIFY_MISMATCH"]  = "Перевірка прошивки не пройдена. Прошивка пошкоджена або не записалась.",
        ["E_TIMEOUT"]          = "Час очікування вичерпано. Перевірте з'єднання та програматор.",
        ["E_GDB_CRASHED"]      = "Внутрішня помилка gdb. Перезапустіть станцію та повторіть.",
        ["E_FW_HASH_MISMATCH"] = "Контрольна сума прошивки не співпадає з каталогом. Не прошивати.",
        ["E_BATCH_LOCKED"]     = "Партію вже почато з іншою прошивкою. Завершіть партію або змініть її ID.",
        ["E_BATCH_LOCK_CHECK_FAILED"] = "Не вдалося безпечно заблокувати партію. Прошивку зупинено; зверніться до інженера.",
        ["E_ELF_NOT_FOUND"]    = "Файл прошивки (ELF) не знайдено за вказаним шляхом.",
        ["E_FW_NOT_FOUND"]     = "Файл прошивки не знайдено за вказаним шляхом.",
        ["E_FW_BAD_FORMAT"]    = "Файл прошивки має невірний формат для вибраного релізу.",
        ["E_FW_READ_FAILED"]   = "Не вдалося прочитати файл прошивки. Перевірте права доступу та носій.",
        ["E_INTERNAL"]         = "Внутрішня помилка застосунку. Перезапустіть програму.",
        ["E_NOT_SIGNED_IN"]    = "Потрібен вхід GitHub. Відкрийте Налаштування → Авторизація GitHub → Увійти.",
        ["E_AUTH_EXPIRED"]     = "Сесія GitHub застаріла (>6 міс). Увійдіть знову у Налаштуваннях.",
        ["E_FW_DOWNLOAD_FAILED"] = "Не вдалося завантажити прошивку з GitHub. Перевірте мережу.",
        ["E_ASSET_NOT_FOUND"]  = "У релізі GitHub немає очікуваного файлу прошивки. Зверніться до інженера.",
        ["E_RELEASE_REVOKED"]  = "Цю версію прошивки відкликано в каталозі. Оновіть каталог і виберіть іншу версію.",
    };

    public static string For(string? errorCode)
        => errorCode is not null && Map.TryGetValue(errorCode, out var hint)
            ? hint
            : "Невідома помилка. Зверніться до інженера.";
}
