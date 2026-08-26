using System.Globalization;
using System.Text;

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
        bool requireNewer = false)
    {
        var incoming = generatedAt.ToUniversalTime();
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
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
            if (File.Exists(floorPath))
            {
                var text = Encoding.UTF8.GetString(
                    BoundedFileReader.ReadAllBytes(floorPath, 128)).Trim();
                if (!DateTime.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
                {
                    return Failure(CatalogActivationStatus.StateError, incoming, null,
                        "catalog anti-rollback floor is malformed");
                }
                floor = parsed.ToUniversalTime();
            }

            if (floor is { } existing
                && (incoming < existing || (requireNewer && incoming <= existing)))
            {
                return Failure(CatalogActivationStatus.RollbackRejected, incoming, existing,
                    $"catalog generated_at {incoming:O} <= cached floor {existing:O} (rollback refused)");
            }

            if (floor is null || incoming > floor.Value)
                WriteAtomic(floorPath, Encoding.UTF8.GetBytes(incoming.ToString("O", CultureInfo.InvariantCulture)));

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

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(temporary, bytes);
        try { File.Move(temporary, path, overwrite: true); }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }
}
