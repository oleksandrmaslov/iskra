using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iskra.Core;

/// <summary>
/// Reads a memory address written either as a JSON number or as a hex string
/// ("0x08000000" / "08000000"). Catalog authors copy these straight out of a
/// linker script, where hex is the only readable form, but the signed catalog
/// must round-trip losslessly — so writing always emits the hex string form.
/// </summary>
public sealed class HexOrNumberUInt64Converter : JsonConverter<ulong?>
{
    public override ulong? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                if (reader.TryGetUInt64(out var number)) return number;
                throw new JsonException("memory address is not a non-negative integer");

            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text)) return null;
                return ParseText(text.Trim());

            default:
                throw new JsonException($"memory address must be a number or string, got {reader.TokenType}");
        }
    }

    public override void Write(Utf8JsonWriter writer, ulong? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue($"0x{value.Value:X8}");
    }

    private static ulong ParseText(string text)
    {
        var span = text.AsSpan();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) span = span[2..];
        if (span.IsEmpty) throw new JsonException($"memory address '{text}' is empty");

        // Always hex, prefix or not: memory maps are never written in decimal,
        // and reading a bare 08000000 as eight million would place the check on
        // entirely the wrong side of the address space.
        if (!ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
            throw new JsonException($"memory address '{text}' is not a valid hexadecimal value");

        return parsed;
    }
}
