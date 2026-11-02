using Iskra.Core;

namespace Iskra.Application;

/// <summary>
/// Shared application-level paths. Frontends must use these helpers so WPF,
/// Avalonia, and the flash transaction never inspect different databases.
/// </summary>
public static class ApplicationPaths
{
    public static string ResolveDatabasePath(AppSettings settings, bool ensureDirectory = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var requested = !string.IsNullOrWhiteSpace(settings.DbPath)
            ? settings.DbPath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Iskra",
                "flash_log.db");
        var path = AuditDatabasePathPolicy.ValidateAndNormalize(requested);
        if (ensureDirectory)
        {
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("audit database has no parent directory");
            Directory.CreateDirectory(directory);
        }
        return path;
    }
}
