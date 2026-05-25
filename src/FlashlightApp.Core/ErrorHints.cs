namespace FlashlightApp.Core;

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
        ["E_TARGET_MISMATCH"]  = "Підключено невірну плату. Зразок не відповідає прошивці.",
        ["E_ATTACH_FAILED"]    = "Не вдалося приєднатися до цілі. Перевірте живлення плати.",
        ["E_LOAD_FAILED"]      = "Помилка під час прошивки. Перевірте з'єднання і повторіть.",
        ["E_VERIFY_MISMATCH"]  = "Перевірка прошивки не пройдена. Прошивка пошкоджена або не записалась.",
        ["E_TIMEOUT"]          = "Час очікування вичерпано. Перевірте з'єднання та програматор.",
        ["E_GDB_CRASHED"]      = "Внутрішня помилка gdb. Перезапустіть станцію та повторіть.",
        ["E_FW_HASH_MISMATCH"] = "Контрольна сума прошивки не співпадає з каталогом. Не прошивати.",
    };

    public static string For(string? errorCode)
        => errorCode is not null && Map.TryGetValue(errorCode, out var hint)
            ? hint
            : "Невідома помилка. Зверніться до інженера.";
}
