using System.Security.Cryptography;
using System.Text;

namespace Iskra.Core;

public enum LegacyMachineTokenCleanupStatus
{
    Absent,
    Removed,
    CleanupRequired,
}

public sealed record LegacyMachineTokenCleanupResult(
    LegacyMachineTokenCleanupStatus Status,
    string Path,
    string? Diagnostic = null);

/// <summary>
/// Removes the pre-2.2.1 machine-wide DPAPI credential without ever decrypting
/// or migrating it. A shared bearer credential cannot safely be attributed to
/// whichever Windows account happens to launch the upgraded application.
/// </summary>
public static class LegacyMachineTokenCleanup
{
    public const string LegacyDirectoryName = "Iskra";
    public const string LegacyFileName = "auth.bin";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public static string DefaultPath()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return System.IO.Path.Combine(common, LegacyDirectoryName, LegacyFileName);
    }

    public static LegacyMachineTokenCleanupResult EnsureRemoved()
    {
        var path = DefaultPath();
        return OperatingSystem.IsWindows()
            ? EnsureRemovedAtPath(path)
            : new LegacyMachineTokenCleanupResult(
                LegacyMachineTokenCleanupStatus.Absent,
                path);
    }

    internal static LegacyMachineTokenCleanupResult EnsureRemovedAtPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            return new LegacyMachineTokenCleanupResult(
                LegacyMachineTokenCleanupStatus.Absent,
                path);
        }

        var fullPath = System.IO.Path.GetFullPath(path);
        if (!string.Equals(
                System.IO.Path.GetFileName(fullPath),
                LegacyFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("legacy token path must end in auth.bin", nameof(path));
        }

        using var timeout = new CancellationTokenSource(LockTimeout);
        try
        {
            using var systemLock = SystemWideMutexLease.AcquireAsync(
                    MutexName(fullPath),
                    currentUserOnly: false,
                    timeout.Token)
                .GetAwaiter().GetResult();
            return RemoveExactFile(fullPath);
        }
        catch (Exception ex) when (ex is OperationCanceledException
            or InvalidOperationException
            or UnauthorizedAccessException
            or IOException
            or NotSupportedException)
        {
            return CleanupRequired(fullPath, ex.Message);
        }
    }

    private static LegacyMachineTokenCleanupResult RemoveExactFile(string fullPath)
    {
        var parent = System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent))
            return CleanupRequired(fullPath, "legacy token path has no parent directory");

        try
        {
            var parentAttributes = File.GetAttributes(parent);
            if ((parentAttributes & FileAttributes.ReparsePoint) != 0)
            {
                return CleanupRequired(
                    fullPath,
                    "legacy credential directory is a reparse point");
            }
        }
        catch (DirectoryNotFoundException)
        {
            return Absent(fullPath);
        }
        catch (FileNotFoundException)
        {
            return Absent(fullPath);
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (FileNotFoundException)
        {
            return Absent(fullPath);
        }
        catch (DirectoryNotFoundException)
        {
            return Absent(fullPath);
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            return CleanupRequired(
                fullPath,
                "legacy credential path is not a regular file");
        }

        File.Delete(fullPath);

        try
        {
            _ = File.GetAttributes(fullPath);
            return CleanupRequired(fullPath, "legacy credential still exists after deletion");
        }
        catch (FileNotFoundException)
        {
            return new LegacyMachineTokenCleanupResult(
                LegacyMachineTokenCleanupStatus.Removed,
                fullPath);
        }
        catch (DirectoryNotFoundException)
        {
            return new LegacyMachineTokenCleanupResult(
                LegacyMachineTokenCleanupStatus.Removed,
                fullPath);
        }
    }

    private static LegacyMachineTokenCleanupResult Absent(string path) =>
        new(LegacyMachineTokenCleanupStatus.Absent, path);

    private static LegacyMachineTokenCleanupResult CleanupRequired(string path, string diagnostic) =>
        new(LegacyMachineTokenCleanupStatus.CleanupRequired, path, diagnostic);

    private static string MutexName(string fullPath)
    {
        var normalized = fullPath.ToUpperInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return $"Iskra.LegacyMachineTokenCleanup.v1.{Convert.ToHexString(digest)}";
    }
}
