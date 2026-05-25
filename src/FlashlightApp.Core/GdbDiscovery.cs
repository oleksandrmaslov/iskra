using System.Runtime.InteropServices;

namespace FlashlightApp.Core;

/// <summary>
/// Locates <c>arm-none-eabi-gdb</c> on the current machine. Search order:
/// (1) explicit path, (2) PATH, (3) standard Arm GNU Toolchain install dirs on Windows.
/// Sprint 4 installer chains the ARM toolchain MSI, so the standard paths will exist.
/// </summary>
public static class GdbDiscovery
{
    public const string ExeName = "arm-none-eabi-gdb";

    public static string? Find(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? explicitPath : null;

        var exe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ExeName + ".exe"
            : ExeName;

        var fromPath = ProbePath(exe);
        if (fromPath is not null) return fromPath;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var dir in WindowsToolchainRoots())
            {
                var candidate = LatestVersionedBin(dir, exe);
                if (candidate is not null) return candidate;
            }
        }

        return null;
    }

    private static string? ProbePath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry — skip */ }
        }
        return null;
    }

    private static IEnumerable<string> WindowsToolchainRoots()
    {
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var root in new[] { pfx86, pf })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "Arm GNU Toolchain arm-none-eabi");
            yield return Path.Combine(root, "GNU Arm Embedded Toolchain");
        }
    }

    /// <summary>
    /// Returns the newest-versioned <c>&lt;root&gt;/&lt;version&gt;/bin/&lt;exe&gt;</c>, or null.
    /// </summary>
    private static string? LatestVersionedBin(string root, string exe)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateDirectories(root)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "bin", exe))
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }
}
