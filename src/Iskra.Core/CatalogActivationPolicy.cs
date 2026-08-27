using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Iskra.Core;

public enum CatalogActivationStatus
{
    Accepted,
    RollbackRejected,
    FutureRejected,
    StateError,
}

public sealed record CatalogActivationResult(
    CatalogActivationStatus Status,
    DateTime IncomingGeneratedAtUtc,
    DateTime? PreviousFloorUtc,
    string? Diagnostic)
{
    public bool IsAccepted => Status == CatalogActivationStatus.Accepted;
}

/// <summary>
/// Enforces the signed catalog anti-rollback floor whenever a catalog becomes
/// active, not only while downloading it. The floor update is serialized across
/// Iskra processes and committed atomically in the same directory.
/// </summary>
public static class CatalogActivationPolicy
{
    public static CatalogActivationResult ValidateAndAdvance(
        DateTime generatedAt,
        string? floorPathOverride = null,
        DateTime? nowUtc = null,
        bool requireNewer = false,
        string? catalogSha256 = null,
        bool advance = true)
    {
        var incoming = generatedAt.ToUniversalTime();
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        catalogSha256 = NormalizeDigest(catalogSha256);
        var floorPath = floorPathOverride ?? Path.Combine(
            RemoteCatalogClient.DefaultCacheDir(),
            RemoteCatalogClient.GeneratedAtFileName);

        if (incoming > now.AddHours(24))
            return Failure(CatalogActivationStatus.FutureRejected, incoming, null,
                $"catalog generated_at {incoming:O} is implausibly far in the future");

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(floorPath))
                ?? throw new IOException("catalog rollback floor has no parent directory");
            Directory.CreateDirectory(directory);
            using var interprocessLock = new FileStream(
                floorPath + ".lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            DateTime? floor = null;
            string? floorDigest = null;
            if (File.Exists(floorPath))
            {
                var text = Encoding.UTF8.GetString(
                    BoundedFileReader.ReadAllBytes(floorPath, 512)).Trim();
                if (!TryParseState(text, out var parsed, out floorDigest))
                {
                    return Failure(CatalogActivationStatus.StateError, incoming, null,
                        "catalog anti-rollback floor is malformed");
                }
                floor = parsed.ToUniversalTime();
            }

            var sameDigest = floorDigest is not null
                && catalogSha256 is not null
                && CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(floorDigest),
                    Convert.FromHexString(catalogSha256));

            if (floor is { } existing
                && (incoming < existing
                    || (requireNewer && incoming == existing && !sameDigest)))
            {
                return Failure(CatalogActivationStatus.RollbackRejected, incoming, existing,
                    $"catalog generated_at {incoming:O} <= cached floor {existing:O} (rollback refused)");
            }

            if (floor is { } sameTimestamp
                && incoming == sameTimestamp
                && floorDigest is not null
                && catalogSha256 is not null
                && !sameDigest)
            {
                return Failure(CatalogActivationStatus.RollbackRejected, incoming, sameTimestamp,
                    "catalog generated_at matches the rollback floor but its signed digest differs");
            }

            if (advance && (floor is null
                || incoming > floor.Value
                || (incoming == floor.Value && floorDigest is null && catalogSha256 is not null)))
            {
                WriteAtomic(floorPath, SerializeState(incoming, catalogSha256));
            }

            return new CatalogActivationResult(
                CatalogActivationStatus.Accepted, incoming, floor, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failure(CatalogActivationStatus.StateError, incoming, null, ex.Message);
        }
    }

    private static CatalogActivationResult Failure(
        CatalogActivationStatus status,
        DateTime incoming,
        DateTime? floor,
        string diagnostic) => new(status, incoming, floor, diagnostic);

    public static string ComputeCatalogSha256(ReadOnlySpan<byte> catalogBytes) =>
        Convert.ToHexString(SHA256.HashData(catalogBytes)).ToLowerInvariant();

    private static string? NormalizeDigest(string? digest)
    {
        if (digest is null) return null;
        var normalized = digest.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("catalog SHA-256 must be exactly 64 hexadecimal characters", nameof(digest));
        return normalized;
    }

    private static bool TryParseState(string text, out DateTime generatedAtUtc, out string? digest)
    {
        generatedAtUtc = default;
        digest = null;
        if (text.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                var root = document.RootElement;
                if (!root.TryGetProperty("generated_at_utc", out var generatedAt)
                    || generatedAt.ValueKind != JsonValueKind.String
                    || !DateTime.TryParse(
                        generatedAt.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out generatedAtUtc))
                {
                    return false;
                }

                if (root.TryGetProperty("catalog_sha256", out var hash)
                    && hash.ValueKind == JsonValueKind.String)
                {
                    digest = NormalizeDigest(hash.GetString());
                }
                return true;
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException)
            {
                generatedAtUtc = default;
                digest = null;
                return false;
            }
        }

        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out generatedAtUtc);
    }

    private static byte[] SerializeState(DateTime generatedAtUtc, string? digest) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 2,
            generated_at_utc = generatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            catalog_sha256 = digest,
        });

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4_096,
                       FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }
}
