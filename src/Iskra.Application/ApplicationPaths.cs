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
        if (!string.IsNullOrWhiteSpace(settings.DbPath)) return settings.DbPath;

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Iskra");
        if (ensureDirectory) Directory.CreateDirectory(directory);
        return Path.Combine(directory, "flash_log.db");
    }
}
