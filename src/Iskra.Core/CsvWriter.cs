namespace Iskra.Core;

/// <summary>
/// RFC 4180 CSV escaping plus spreadsheet-formula neutralization. Values whose
/// first non-space character is <c>=</c>, <c>+</c>, <c>-</c>, or <c>@</c>
/// receive a leading apostrophe so operator/batch/GDB text cannot execute when
/// an exported file is opened in Excel or similar software.
/// </summary>
public static class CsvWriter
{
    public static string EscapeField(string? value)
    {
        if (value is null) return "";
        if (StartsSpreadsheetFormula(value)) value = "'" + value;
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

    private static bool StartsSpreadsheetFormula(string value)
    {
        var i = 0;
        while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
        if (i >= value.Length) return false;
        return value[i] is '=' or '+' or '-' or '@';
    }

    public static string JoinRow(IEnumerable<string?> fields)
    {
        return string.Join(",", fields.Select(EscapeField));
    }
}
