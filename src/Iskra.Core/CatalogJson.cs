using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iskra.Core;

public sealed class CatalogParseException : Exception
{
    public CatalogParseException(string msg) : base(msg) { }
    public CatalogParseException(string msg, Exception inner) : base(msg, inner) { }
}

/// <summary>
/// Reads and validates <c>catalog.json</c>. Schema is snake_case JSON; primary-constructor
/// records bind by parameter name through System.Text.Json's case-insensitive matcher.
/// </summary>
public static class CatalogJson
{
    public const int CurrentSchemaVersion = 1;

    public static JsonSerializerOptions DefaultOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static Catalog Parse(string json)
    {
        Catalog? c;
        try
        {
            c = JsonSerializer.Deserialize<Catalog>(json, DefaultOptions);
        }
        catch (JsonException ex)
        {
            throw new CatalogParseException($"catalog json invalid: {ex.Message}", ex);
        }
        if (c is null) throw new CatalogParseException("catalog deserialised to null");
        Validate(c);
        return c;
    }

    public static Catalog ParseFile(string path)
    {
        if (!File.Exists(path))
            throw new CatalogParseException($"catalog not found: {path}");
        return Parse(File.ReadAllText(path));
    }

    public static string Write(Catalog catalog)
        => JsonSerializer.Serialize(catalog, DefaultOptions);

    public static byte[] WriteUtf8(Catalog catalog)
        => JsonSerializer.SerializeToUtf8Bytes(catalog, DefaultOptions);

    public static void Validate(Catalog c)
    {
        if (c.SchemaVersion != CurrentSchemaVersion)
            throw new CatalogParseException(
                $"unsupported schema_version {c.SchemaVersion} (need {CurrentSchemaVersion})");
        if (c.Products is null) throw new CatalogParseException("catalog.products missing");
        if (c.Products.Count == 0) throw new CatalogParseException("catalog has no products");

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in c.Products) ValidateProduct(p, seenIds);

        if (c.Revoked is not null)
        {
            var seenRev = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in c.Revoked)
            {
                if (string.IsNullOrWhiteSpace(r.ProductId))
                    throw new CatalogParseException("revoked entry missing product_id");
                if (string.IsNullOrWhiteSpace(r.Version))
                    throw new CatalogParseException($"revoked entry for '{r.ProductId}' missing version");
                if (!seenRev.Add($"{r.ProductId}|{r.Version}"))
                    throw new CatalogParseException(
                        $"duplicate revocation for {r.ProductId} v{r.Version}");
            }
        }
    }

    private static void ValidateProduct(Product p, HashSet<string> seenIds)
    {
        if (string.IsNullOrWhiteSpace(p.ProductId))
            throw new CatalogParseException("product.product_id missing");
        if (!seenIds.Add(p.ProductId))
            throw new CatalogParseException($"duplicate product_id '{p.ProductId}'");
        if (string.IsNullOrWhiteSpace(p.DisplayName))
            throw new CatalogParseException($"{p.ProductId}: display_name missing");

        if (p.Target is null)
            throw new CatalogParseException($"{p.ProductId}: target missing");
        if (string.IsNullOrWhiteSpace(p.Target.BmpMatch))
            throw new CatalogParseException($"{p.ProductId}: target.bmp_match missing");
        if (string.IsNullOrWhiteSpace(p.Target.PartNumber))
            throw new CatalogParseException($"{p.ProductId}: target.part_number missing");
        if (p.Target.FlashKb <= 0)
            throw new CatalogParseException($"{p.ProductId}: target.flash_kb must be > 0");
        if (p.Target.FrequencyHz is <= 0)
            throw new CatalogParseException($"{p.ProductId}: target.frequency_hz must be > 0");
        if (p.Target.TimeoutSeconds is <= 0)
            throw new CatalogParseException($"{p.ProductId}: target.timeout_s must be > 0");

        if (p.Releases is null || p.Releases.Count == 0)
            throw new CatalogParseException($"{p.ProductId}: no releases");

        var seenVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in p.Releases) ValidateRelease(p.ProductId, r, seenVersions);

        if (string.IsNullOrWhiteSpace(p.DefaultRelease))
            throw new CatalogParseException($"{p.ProductId}: default_release missing");
        if (p.FindRelease(p.DefaultRelease) is null)
            throw new CatalogParseException(
                $"{p.ProductId}: default_release '{p.DefaultRelease}' does not match any release");
    }

    private static void ValidateRelease(string productId, FirmwareRelease r, HashSet<string> seenVersions)
    {
        if (string.IsNullOrWhiteSpace(r.Version))
            throw new CatalogParseException($"{productId}: release missing version");
        if (!seenVersions.Add(r.Version))
            throw new CatalogParseException(
                $"{productId}: duplicate release version '{r.Version}'");
        if (string.IsNullOrWhiteSpace(r.ElfFilename))
            throw new CatalogParseException(
                $"{productId} v{r.Version}: elf_filename missing");
        if (string.IsNullOrWhiteSpace(r.ElfSha256))
            throw new CatalogParseException(
                $"{productId} v{r.Version}: elf_sha256 missing");
        if (r.ElfSha256.Length != 64 || !r.ElfSha256.All(IsHex))
            throw new CatalogParseException(
                $"{productId} v{r.Version}: elf_sha256 must be 64 hex chars (got {r.ElfSha256.Length})");
        if (!Enum.IsDefined(typeof(FirmwareKind), r.FirmwareKind))
            throw new CatalogParseException(
                $"{productId} v{r.Version}: firmware_kind invalid");
        if (r.ElfSource is not null) ValidateElfSource(productId, r.Version, r.ElfSource);
    }

    private static void ValidateElfSource(string productId, string version, GitHubReleaseRef src)
    {
        if (string.IsNullOrWhiteSpace(src.Repo))
            throw new CatalogParseException(
                $"{productId} v{version}: elf_source.repo missing");
        if (!IsValidRepoSlug(src.Repo))
            throw new CatalogParseException(
                $"{productId} v{version}: elf_source.repo must be 'owner/name' (got '{src.Repo}')");
        if (string.IsNullOrWhiteSpace(src.Tag))
            throw new CatalogParseException(
                $"{productId} v{version}: elf_source.tag missing");
        if (string.IsNullOrWhiteSpace(src.Asset))
            throw new CatalogParseException(
                $"{productId} v{version}: elf_source.asset missing");
    }

    private static bool IsValidRepoSlug(string s)
    {
        int slash = s.IndexOf('/');
        if (slash <= 0 || slash != s.LastIndexOf('/') || slash == s.Length - 1) return false;
        foreach (var c in s)
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/')) return false;
        return true;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
