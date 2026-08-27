using System.Text;
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
    /// <summary>
    /// Maximum accepted UTF-8 catalog size. Four MiB leaves ample room for a
    /// large release catalog while bounding memory and parser work on hostile
    /// local input.
    /// </summary>
    public const int MaxCatalogBytes = 4 * 1024 * 1024;

    // Keep the shared parser configuration inside the trusted assembly. A
    // public mutable JsonSerializerOptions instance could be given a custom
    // Catalog converter before first use and reinterpret signature-verified
    // bytes as attacker-selected metadata.
    internal static JsonSerializerOptions DefaultOptions { get; } = new()
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
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaxCatalogBytes)
            throw TooLarge();

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
        return Freeze(c);
    }

    /// <summary>Deserializes and validates one already-captured UTF-8 snapshot.</summary>
    public static Catalog Parse(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxCatalogBytes)
            throw TooLarge();

        // File.ReadAllText historically accepted a UTF-8 BOM. Preserve that
        // compatibility while parsing directly from the verified bytes.
        if (utf8Json.Length >= 3
            && utf8Json[0] == 0xEF
            && utf8Json[1] == 0xBB
            && utf8Json[2] == 0xBF)
        {
            utf8Json = utf8Json[3..];
        }

        Catalog? c;
        try
        {
            c = JsonSerializer.Deserialize<Catalog>(utf8Json, DefaultOptions);
        }
        catch (JsonException ex)
        {
            throw new CatalogParseException($"catalog json invalid: {ex.Message}", ex);
        }

        if (c is null) throw new CatalogParseException("catalog deserialised to null");
        Validate(c);
        return Freeze(c);
    }

    public static Catalog ParseFile(string path)
    {
        try
        {
            return Parse(BoundedFileReader.ReadAllBytes(path, MaxCatalogBytes));
        }
        catch (FileNotFoundException ex)
        {
            throw new CatalogParseException($"catalog not found: {path}", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new CatalogParseException($"catalog not found: {path}", ex);
        }
        catch (FileSizeLimitExceededException ex)
        {
            throw new CatalogParseException(ex.Message, ex);
        }
    }

    public static string Write(Catalog catalog)
        => JsonSerializer.Serialize(catalog, DefaultOptions);

    public static byte[] WriteUtf8(Catalog catalog)
        => JsonSerializer.SerializeToUtf8Bytes(catalog, DefaultOptions);

    /// <summary>
    /// Deep-copies every collection into a read-only wrapper. Catalog records
    /// are immutable, but JSON normally backs <see cref="IReadOnlyList{T}"/>
    /// properties with mutable lists that a caller could cast and change after
    /// signature verification.
    /// </summary>
    internal static Catalog Freeze(Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var products = Array.AsReadOnly(catalog.Products
            .Select(product => product with
            {
                Releases = Array.AsReadOnly(product.Releases.ToArray()),
            })
            .ToArray());
        var revoked = catalog.Revoked is null
            ? null
            : Array.AsReadOnly(catalog.Revoked.ToArray());
        return catalog with { Products = products, Revoked = revoked };
    }

    /// <summary>
    /// Applies the stricter path policy used after a catalog has crossed the
    /// production signature boundary. Unsigned sideload catalogs intentionally
    /// carry absolute local paths and therefore do not call this method.
    /// </summary>
    public static void ValidateTrustedArtifactPaths(Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (var product in catalog.Products)
        {
            if (product.Target.FlashOrigin is null)
                throw new CatalogParseException(
                    $"{product.ProductId}: a trusted catalog must declare target.flash_origin");
            var flashEnd = product.Target.FlashOrigin.Value
                + checked((ulong)product.Target.FlashKb * 1024UL);
            if (flashEnd > (ulong)uint.MaxValue + 1UL)
                throw new CatalogParseException(
                    $"{product.ProductId}: target flash window exceeds the 32-bit Cortex-M address space");

            foreach (var release in product.Releases)
            {
                if (!IsPortableLeafName(release.ElfFilename))
                    throw new CatalogParseException(
                        $"{product.ProductId} v{release.Version}: trusted elf_filename must be a portable leaf name");

                if (release.ElfSource is { } source)
                {
                    if (!IsPortableLeafName(source.Tag))
                        throw new CatalogParseException(
                            $"{product.ProductId} v{release.Version}: elf_source.tag must be one portable path segment");
                    if (!IsPortableLeafName(source.Asset))
                        throw new CatalogParseException(
                            $"{product.ProductId} v{release.Version}: elf_source.asset must be a portable leaf name");
                }
            }
        }
    }

    public static void Validate(Catalog c)
    {
        if (c.SchemaVersion != CurrentSchemaVersion)
            throw new CatalogParseException(
                $"unsupported schema_version {c.SchemaVersion} (need {CurrentSchemaVersion})");
        if (c.GeneratedAt == default || c.GeneratedAt.Kind == DateTimeKind.Unspecified)
            throw new CatalogParseException("catalog.generated_at must be an ISO-8601 timestamp with timezone");
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
        if (p.Target.FrequencyHz is > FlashOptions.MaxBmpFrequencyHz)
            throw new CatalogParseException(
                $"{p.ProductId}: target.frequency_hz must be <= {FlashOptions.MaxBmpFrequencyHz}");
        if (p.Target.TimeoutSeconds is <= 0)
            throw new CatalogParseException($"{p.ProductId}: target.timeout_s must be > 0");
        if (p.Target.TimeoutSeconds is > FlashOptions.MaxTimeoutSeconds)
            throw new CatalogParseException(
                $"{p.ProductId}: target.timeout_s must be <= {FlashOptions.MaxTimeoutSeconds}");
        if (p.Target.PowerMode is { } powerMode && !Enum.IsDefined(powerMode))
            throw new CatalogParseException($"{p.ProductId}: target.power_mode invalid");

        // The optional memory map is all-or-nothing per region: a half-declared
        // RAM window would silently widen or narrow the accepted address space.
        if (p.Target.RamKb is <= 0)
            throw new CatalogParseException($"{p.ProductId}: target.ram_kb must be > 0");
        if (p.Target.RamOrigin is not null && p.Target.RamKb is null)
            throw new CatalogParseException($"{p.ProductId}: target.ram_origin requires target.ram_kb");
        if (p.Target.RamKb is not null && p.Target.RamOrigin is null)
            throw new CatalogParseException($"{p.ProductId}: target.ram_kb requires target.ram_origin");
        if (p.Target.RamOrigin is not null && p.Target.FlashOrigin is null)
            throw new CatalogParseException(
                $"{p.ProductId}: target.ram_origin requires target.flash_origin; "
                + "declaring RAM alone would leave flash addresses unchecked");

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
        if (s[..slash] is "." or ".." || s[(slash + 1)..] is "." or "..") return false;
        foreach (var c in s)
            if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/')) return false;
        return true;
    }

    private static bool IsPortableLeafName(string value)
    {
        const string invalid = "<>:\"/\\|?*";
        return !string.IsNullOrWhiteSpace(value)
            && value is not "." and not ".."
            && !Path.IsPathRooted(value)
            && !value.EndsWith(' ')
            && !value.EndsWith('.')
            && !value.Any(c => c < ' ' || invalid.Contains(c));
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static CatalogParseException TooLarge() => new(
        $"catalog exceeds the {MaxCatalogBytes}-byte limit");
}
