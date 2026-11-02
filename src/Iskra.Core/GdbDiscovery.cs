using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Iskra.Core;

public sealed record GdbProvenanceEvidence(
    string LauncherPath,
    string CanonicalPath,
    string Sha256,
    string? VersionBanner,
    bool IsTrustedLocation,
    bool IsLabOverride);

/// <summary>
/// Locates a GDB executable capable of driving ARM Cortex-M targets. Search order:
/// (1) explicit path, (2) standard Arm GNU Toolchain install
/// directories on Windows, (3) PATH. Linux also accepts the distribution
/// <c>gdb-multiarch</c> package after preferring <c>arm-none-eabi-gdb</c>.
/// Production builds accept candidates only below administrator-controlled
/// package roots; a user-writable PATH entry or settings-selected executable
/// cannot enter the flash trust boundary. Symlinks must resolve back into an
/// allowed root as well.
/// The production installer chains the Arm GNU Toolchain MSI, so one of the
/// standard paths should exist immediately after setup.
/// </summary>
public static class GdbDiscovery
{
    public const string ExeName = "arm-none-eabi-gdb";
    public const string UntrustedLabOverrideEnvironmentVariable = "ISKRA_LAB_ALLOW_UNTRUSTED_GDB";
    private const int MaxVersionBannerChars = 4096;

    public static string? Find(string? explicitPath = null)
    {
        var platform = CurrentPlatform();
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return AcceptCandidate(explicitPath, platform);

        if (platform == OSPlatform.Windows)
        {
            var exe = ExeName + ".exe";
            foreach (var dir in WindowsToolchainRoots())
            {
                var candidate = FindUnderToolchainRoot(dir, exe);
                var accepted = candidate is null ? null : AcceptCandidate(candidate, platform);
                if (accepted is not null) return accepted;
            }
        }

        foreach (var executableName in ExecutableNamesFor(platform))
        {
            var fromPath = ProbePath(executableName, platform);
            if (fromPath is not null) return fromPath;
        }

        return null;
    }

    /// <summary>
    /// Produces bounded, reproducible toolchain evidence for station acceptance
    /// records. The hash is over the final symlink target. Version probing has a
    /// short timeout and retains at most one bounded line.
    /// </summary>
    public static GdbProvenanceEvidence Inspect(string launcherPath, TimeSpan? versionTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherPath);
        if (!File.Exists(launcherPath))
            throw new FileNotFoundException("GDB executable was not found", launcherPath);

        var platform = CurrentPlatform();
        var fullPath = Path.GetFullPath(launcherPath);
        var canonicalPath = ResolveCanonicalPath(fullPath, platform);
        using var stream = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var trusted = IsTrustedLocation(fullPath, platform)
            && IsTrustedLocation(canonicalPath, platform);
        var labOverride = !trusted && IsUntrustedLabOverrideEnabled();
        var version = TryReadVersionBanner(fullPath, versionTimeout ?? TimeSpan.FromSeconds(5));
        return new GdbProvenanceEvidence(
            fullPath,
            canonicalPath,
            sha256,
            version,
            trusted,
            labOverride);
    }

    internal static IReadOnlyList<string> ExecutableNamesFor(OSPlatform platform)
    {
        if (platform == OSPlatform.Windows)
            return [ExeName + ".exe"];

        return platform == OSPlatform.Linux
            ? [ExeName, "gdb-multiarch"]
            : [ExeName];
    }

    private static string? ProbePath(string exe, OSPlatform platform)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                var accepted = AcceptCandidate(candidate, platform);
                if (accepted is not null) return accepted;
            }
            catch { /* malformed PATH entry — skip */ }
        }
        return null;
    }

    private static string? AcceptCandidate(string candidate, OSPlatform platform)
    {
        try
        {
            if (!File.Exists(candidate)) return null;
            var fullPath = Path.GetFullPath(candidate);
            var canonicalPath = ResolveCanonicalPath(fullPath, platform);
            var trusted = IsTrustedLocation(fullPath, platform)
                && IsTrustedLocation(canonicalPath, platform);
            return trusted || IsUntrustedLabOverrideEnabled() ? fullPath : null;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return null;
        }
    }

    internal static bool IsTrustedLocation(string path, OSPlatform platform)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (platform == OSPlatform.Windows)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return false; }
            return WindowsTrustedRoots().Any(root => IsWithinRoot(fullPath, root, StringComparison.OrdinalIgnoreCase));
        }

        // This method is also exercised from Windows-hosted unit tests, so do
        // not use the host's Path.GetFullPath for POSIX strings.
        if (!path.StartsWith("/", StringComparison.Ordinal) || path.Contains('\\'))
            return false;
        if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            return false;

        var roots = platform == OSPlatform.Linux
            ? new[] { "/usr/bin", "/usr/sbin", "/bin", "/sbin" }
            : new[] { "/Applications/ArmGNUToolchain", "/Library/ArmGNUToolchain", "/usr/bin" };
        return roots.Any(root => IsWithinRoot(path, root, StringComparison.Ordinal));
    }

    internal static bool IsWithinRoot(string path, string root, StringComparison comparison)
    {
        if (string.Equals(path, root, comparison)) return true;
        var separator = root.EndsWith('/') || root.EndsWith('\\')
            ? string.Empty
            : platformSeparator(root);
        return path.StartsWith(root + separator, comparison);

        static string platformSeparator(string value) => value.Contains('\\') ? "\\" : "/";
    }

    internal static string ResolveCanonicalPath(string path, OSPlatform platform)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? (platform == OSPlatform.Windows ? throw new ArgumentException("Path has no root", nameof(path)) : "/");
        var current = root;
        var relative = fullPath[root.Length..];
        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            var candidate = Path.Combine(current, segments[index]);
            FileSystemInfo entry = index == segments.Length - 1
                ? new FileInfo(candidate)
                : new DirectoryInfo(candidate);
            var target = entry.ResolveLinkTarget(returnFinalTarget: true);
            current = target?.FullName ?? candidate;
        }
        return Path.GetFullPath(current);
    }

    internal static bool IsUntrustedLabOverrideEnabled()
    {
#if ISKRA_LAB_CATALOGS
        var value = Environment.GetEnvironmentVariable(UntrustedLabOverrideEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

    private static string? TryReadVersionBanner(string gdbPath, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) return null;
        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = gdbPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("--version");
            if (!process.Start()) return null;

            using var timeoutCts = new CancellationTokenSource(timeout);
            var stdout = ReadBoundedFirstLineAsync(process.StandardOutput, timeoutCts.Token);
            var stderr = ReadBoundedFirstLineAsync(process.StandardError, timeoutCts.Token);
            process.WaitForExitAsync(timeoutCts.Token).GetAwaiter().GetResult();
            var banner = stdout.GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(banner))
                banner = stderr.GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(banner) ? null : banner.Trim();
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or OperationCanceledException)
        {
            TryKill(process);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task<string?> ReadBoundedFirstLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(Math.Min(256, MaxVersionBannerChars));
        var buffer = new char[256];
        while (result.Length < MaxVersionBannerChars)
        {
            var remaining = Math.Min(buffer.Length, MaxVersionBannerChars - result.Length);
            var read = await reader.ReadAsync(buffer.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            var lineEnd = Array.FindIndex(buffer, 0, read, c => c is '\r' or '\n');
            var take = lineEnd >= 0 ? lineEnd : read;
            result.Append(buffer, 0, take);
            if (lineEnd >= 0) break;
        }
        return result.Length == 0 ? null : result.ToString();
    }

    private static void TryKill(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static OSPlatform CurrentPlatform() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? OSPlatform.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                ? OSPlatform.Linux
                : OSPlatform.OSX;

    private static IEnumerable<string> WindowsToolchainRoots()
    {
        var pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var root in new[] { pfx86, pf })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "Arm GNU Toolchain arm-none-eabi");
            yield return Path.Combine(root, "GNU Arm Embedded Toolchain");
            yield return Path.Combine(root, "Arm", "GNU Toolchain mingw-w64-i686-arm-none-eabi");
        }
    }

    private static IEnumerable<string> WindowsTrustedRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"),
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(root); }
            catch { continue; }
            if (roots.Add(fullPath)) yield return fullPath;
        }
    }

    /// <summary>
    /// Returns a matching gdb below a known toolchain root. Arm has used both
    /// <c>root/bin</c> and <c>root/version/bin</c> layouts across Windows
    /// installers, so check both without recursively walking Program Files.
    /// </summary>
    private static string? FindUnderToolchainRoot(string root, string exe)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            var direct = Path.Combine(root, "bin", exe);
            if (File.Exists(direct)) return direct;

            return Directory.EnumerateDirectories(root)
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .SelectMany(d => new[]
                {
                    Path.Combine(d, "bin", exe),
                    Path.Combine(d, "arm-none-eabi", "bin", exe)
                })
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }
}
