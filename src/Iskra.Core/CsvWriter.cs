namespace FlashlightApp.Core;

/// <summary>
/// RFC 4180 CSV escaping. Fields are quoted only when they contain a comma,
/// double-quote, CR, or LF; embedded double-quotes are doubled.
/// </summary>
public static class CsvWriter
{
    public static string EscapeField(string? value)
    {
        if (value is null) return "";
        bool needsQuotes = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == ',' || c == '"' || c == '\r' || c == '\n')
            {
                needsQuotes = true;
                break;
            }
        }
        if (!needsQuotes) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string JoinRow(IEnumerable<string?> fields)
    {
        return string.Join(",", fields.Select(EscapeField));
    }
}
