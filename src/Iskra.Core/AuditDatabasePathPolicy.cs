namespace Iskra.Core;

/// <summary>
/// Separates production audit databases from SQLite's transient and URI data
/// sources. Test code may still construct <see cref="SqliteLogStore"/> with
/// <c>:memory:</c> directly; application and CLI paths must pass this policy.
/// </summary>
public static class AuditDatabasePathPolicy
{
    private static readonly HashSet<string> WindowsDeviceNames = new(
        new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        },
        StringComparer.OrdinalIgnoreCase);

    public static string ValidateAndNormalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var value = path.Trim();
        if (string.Equals(value, ":memory:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "the audit database must be a persistent filesystem path, not a SQLite memory/URI source",
                nameof(path));
        }

        var fullPath = Path.GetFullPath(value);
        if (Directory.Exists(fullPath))
            throw new ArgumentException("the audit database path names a directory", nameof(path));

        if (OperatingSystem.IsWindows())
        {
            if (fullPath.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(@"\\?\GLOBALROOT", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Windows device paths are not valid audit databases", nameof(path));
            }

            var root = Path.GetPathRoot(fullPath) ?? string.Empty;
            foreach (var segment in fullPath[root.Length..]
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = segment.TrimEnd(' ', '.');
                var stem = normalized.Split('.', 2)[0];
                if (WindowsDeviceNames.Contains(stem))
                    throw new ArgumentException("Windows device names are not valid audit database paths", nameof(path));
            }
        }

        return fullPath;
    }
}
