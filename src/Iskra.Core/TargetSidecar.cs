using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iskra.Core;

public sealed class TargetSidecarException : Exception
{
    public TargetSidecarException(string msg, Exception? inner = null) : base(msg, inner) { }
}

/// <summary>
/// One <c>target.json</c> sidecar — published alongside an ELF as a GitHub
/// release asset. The catalog generator (Sprint 3.5) collects these across
/// every <c>*-firmware</c> repo and stitches them into a signed
/// <c>catalog.json</c>.
/// <para>Fields with no obvious default are required; <c>DisplayName</c> /
/// <c>ReleasedAt</c> / <c>Notes</c> are optional.</para>
/// </summary>
public sealed record TargetSidecar(
    string ProductId,
    string Version,
    string PartNumber,
    string BmpMatch,
    int FlashKb,
    string ElfSha256,
    string? DisplayName = null,
    DateTime? ReleasedAt = null,
    string? Notes = null,
    FirmwareKind FirmwareKind = FirmwareKind.Elf,
    int? FrequencyHz = null,
    PowerMode? PowerMode = null,
    bool? ConnectReset = null,
    [property: JsonPropertyName("timeout_s")] int? TimeoutSeconds = null)
{
    public static JsonSerializerOptions JsonOpts { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static TargetSidecar Parse(string json)
    {
        TargetSidecar? s;
        try { s = JsonSerializer.Deserialize<TargetSidecar>(json, JsonOpts); }
        catch (JsonException ex) { throw new TargetSidecarException($"target.json invalid: {ex.Message}", ex); }
        if (s is null) throw new TargetSidecarException("target.json deserialised to null");
        Validate(s);
        return s;
    }

    public static TargetSidecar ParseFile(string path)
    {
        if (!File.Exists(path))
            throw new TargetSidecarException($"target.json not found: {path}");
        try { return Parse(File.ReadAllText(path)); }
        catch (TargetSidecarException ex)
        {
            throw new TargetSidecarException($"{path}: {ex.Message}", ex.InnerException);
        }
    }

    private static void Validate(TargetSidecar s)
    {
        if (string.IsNullOrWhiteSpace(s.ProductId))  throw new TargetSidecarException("product_id missing");
        if (string.IsNullOrWhiteSpace(s.Version))    throw new TargetSidecarException("version missing");
        if (string.IsNullOrWhiteSpace(s.PartNumber)) throw new TargetSidecarException("part_number missing");
        if (string.IsNullOrWhiteSpace(s.BmpMatch))   throw new TargetSidecarException("bmp_match missing");
        if (s.FlashKb <= 0)                           throw new TargetSidecarException("flash_kb must be > 0");
        if (!FirmwareIntegrity.IsValidSha256Hex(s.ElfSha256))
            throw new TargetSidecarException("elf_sha256 must be 64 hex chars");
        if (!Enum.IsDefined(typeof(FirmwareKind), s.FirmwareKind))
            throw new TargetSidecarException("firmware_kind invalid");
        if (s.FrequencyHz is <= 0)   throw new TargetSidecarException("frequency_hz must be > 0");
        if (s.TimeoutSeconds is <= 0) throw new TargetSidecarException("timeout_s must be > 0");
    }
}
