using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;

namespace Iskra.Core;

public enum ToolchainInstallStatus
{
    /// <summary>A trusted GDB was already present; nothing was downloaded.</summary>
    AlreadyInstalled,
    /// <summary>The toolchain was installed and a trusted GDB is now discoverable.</summary>
    Installed,
    /// <summary>The operator dismissed the elevation prompt or cancelled the run.</summary>
    Cancelled,
    /// <summary>The pinned installer could not be fetched.</summary>
    DownloadFailed,
    /// <summary>The download did not match the pinned SHA-256 and was discarded.</summary>
    HashMismatch,
    /// <summary>The vendor installer ran but reported failure.</summary>
    InstallerFailed,
    /// <summary>
    /// The installer finished successfully but no trusted GDB appeared, e.g. it
    /// was redirected to a user-writable location that cannot be trusted.
    /// </summary>
    InstalledButNotDiscoverable,
    /// <summary>No unattended install path exists for this operating system yet.</summary>
    UnsupportedPlatform,
}

public sealed record ToolchainInstallResult(
    ToolchainInstallStatus Status,
    string? GdbPath,
    string Message)
{
    public bool IsUsable => Status is ToolchainInstallStatus.AlreadyInstalled
        or ToolchainInstallStatus.Installed;
}

public sealed record ToolchainDownloadProgress(long BytesRead, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0 ? (double)BytesRead / TotalBytes.Value : null;
}

/// <summary>
/// Installs the pinned Arm GNU Toolchain so an operator never has to open a
/// terminal, find a vendor download page, or run a package manager by hand.
///
/// <para><b>Why this elevates instead of installing per-user.</b>
/// <see cref="GdbDiscovery"/> accepts GDB only from administrator-controlled
/// roots, because GDB is the process that writes firmware: a debugger sitting
/// in a user-writable directory could be replaced by anyone with the
/// operator's login. Installing into <c>%LOCALAPPDATA%</c> would avoid the
/// consent prompt and then produce a toolchain the app refuses to use. So the
/// vendor MSI is run normally and Windows shows a single UAC dialog — no
/// command line, no manual download, one click.</para>
///
/// <para><b>Trust.</b> The download is verified against
/// <see cref="ArmToolchainPins.WindowsSha256"/> before anything is executed. A
/// mismatched file is deleted, never run.</para>
/// </summary>
public static class ToolchainInstaller
{
    /// <summary>MSI exit code for "user declined the elevation or cancelled".</summary>
    private const int MsiErrorInstallUserExit = 1602;
    /// <summary>MSI exit code for "succeeded, reboot required".</summary>
    private const int MsiErrorSuccessRebootRequired = 3010;

    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Where the verified installer is cached, so a retry after a declined
    /// prompt does not download hundreds of megabytes again.
    /// </summary>
    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Iskra",
        "toolchain-cache");

    public static string CachedInstallerPath =>
        Path.Combine(CacheDirectory, ArmToolchainPins.WindowsFileName);

    /// <summary>
    /// Ensures a trusted GDB is present, downloading and running the pinned
    /// vendor installer if it is not.
    /// </summary>
    public static async Task<ToolchainInstallResult> EnsureInstalledAsync(
        IProgress<ToolchainDownloadProgress>? progress = null,
        HttpClient? http = null,
        CancellationToken cancellationToken = default)
    {
        var existing = SafeFind();
        if (existing is not null)
        {
            return new ToolchainInstallResult(
                ToolchainInstallStatus.AlreadyInstalled,
                existing,
                $"arm-none-eabi-gdb is already installed at {existing}");
        }

        if (!IsSupported)
        {
            return new ToolchainInstallResult(
                ToolchainInstallStatus.UnsupportedPlatform,
                null,
                UnsupportedPlatformHint());
        }

        string installerPath;
        try
        {
            installerPath = await AcquireVerifiedInstallerAsync(progress, http, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ToolchainInstallResult(
                ToolchainInstallStatus.Cancelled, null, "toolchain download was cancelled");
        }
        catch (ToolchainHashMismatchException ex)
        {
            return new ToolchainInstallResult(ToolchainInstallStatus.HashMismatch, null, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return new ToolchainInstallResult(
                ToolchainInstallStatus.DownloadFailed,
                null,
                $"could not download the Arm GNU Toolchain: {ex.Message}");
        }

        return RunInstaller(installerPath);
    }

    /// <summary>
    /// Downloads the pinned installer if it is not already cached and verified.
    /// Returns the path to a file whose SHA-256 matches the pin.
    /// </summary>
    internal static Task<string> AcquireVerifiedInstallerAsync(
        IProgress<ToolchainDownloadProgress>? progress,
        HttpClient? http,
        CancellationToken cancellationToken)
        => AcquireVerifiedInstallerAsync(
            ArmToolchainPins.WindowsUrl,
            ArmToolchainPins.WindowsSha256,
            CacheDirectory,
            ArmToolchainPins.WindowsFileName,
            progress,
            http,
            cancellationToken);

    internal static async Task<string> AcquireVerifiedInstallerAsync(
        string url,
        string expectedSha256,
        string cacheDirectory,
        string fileName,
        IProgress<ToolchainDownloadProgress>? progress,
        HttpClient? http,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cacheDirectory);
        var target = Path.Combine(cacheDirectory, fileName);

        if (File.Exists(target)
            && await MatchesAsync(target, expectedSha256, cancellationToken).ConfigureAwait(false))
        {
            return target;
        }

        var owned = http is null;
        var client = http ?? new HttpClient();
        var tmp = target + ".tmp";
        try
        {
            using var response = await client
                .GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var declared = response.Content.Headers.ContentLength;
            if (declared > ArmToolchainPins.MaxInstallerBytes)
                throw new IOException(
                    $"toolchain installer declares {declared} bytes, over the "
                    + $"{ArmToolchainPins.MaxInstallerBytes} byte limit");

            await using (var body = await response.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(
                tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > ArmToolchainPins.MaxInstallerBytes)
                        throw new IOException("toolchain installer exceeded the size limit mid-transfer");
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new ToolchainDownloadProgress(total, declared));
                }
            }

            if (!await MatchesAsync(tmp, expectedSha256, cancellationToken).ConfigureAwait(false))
            {
                var actual = await Sha256Async(tmp, cancellationToken).ConfigureAwait(false);
                TryDelete(tmp);
                throw new ToolchainHashMismatchException(
                    "the downloaded Arm GNU Toolchain does not match the pinned SHA-256 and was "
                    + $"discarded (expected {expectedSha256}, got {actual})");
            }

            File.Move(tmp, target, overwrite: true);
            return target;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
        finally
        {
            if (owned) client.Dispose();
        }
    }

    private static ToolchainInstallResult RunInstaller(string installerPath)
    {
        // UseShellExecute lets Windows raise the standard UAC dialog. /qb keeps
        // the vendor's own progress bar visible, so the operator sees what is
        // happening without a console window.
        var startInfo = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            UseShellExecute = true,
        };
        startInfo.ArgumentList.Add("/i");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("/qb");
        startInfo.ArgumentList.Add("EULA=1");

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ToolchainInstallResult(
                    ToolchainInstallStatus.InstallerFailed, null, "could not start msiexec");
            }

            process.WaitForExit();

            if (process.ExitCode is MsiErrorInstallUserExit)
            {
                return new ToolchainInstallResult(
                    ToolchainInstallStatus.Cancelled,
                    null,
                    "the toolchain installation was cancelled");
            }

            if (process.ExitCode is not 0 and not MsiErrorSuccessRebootRequired)
            {
                return new ToolchainInstallResult(
                    ToolchainInstallStatus.InstallerFailed,
                    null,
                    $"the Arm GNU Toolchain installer failed with exit code {process.ExitCode}");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Declining the UAC prompt surfaces here rather than as an exit code.
            return new ToolchainInstallResult(
                ToolchainInstallStatus.Cancelled,
                null,
                $"the toolchain installation was not authorised: {ex.Message}");
        }

        var installed = SafeFind();
        return installed is not null
            ? new ToolchainInstallResult(
                ToolchainInstallStatus.Installed,
                installed,
                $"arm-none-eabi-gdb installed at {installed}")
            : new ToolchainInstallResult(
                ToolchainInstallStatus.InstalledButNotDiscoverable,
                null,
                "the installer reported success but no trusted arm-none-eabi-gdb was found; "
                + "Iskra only accepts GDB from an administrator-controlled location");
    }

    internal static async Task<bool> MatchesAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
        => string.Equals(
            await Sha256Async(path, cancellationToken).ConfigureAwait(false),
            expectedSha256,
            StringComparison.OrdinalIgnoreCase);

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string? SafeFind()
    {
        try { return GdbDiscovery.Find(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string UnsupportedPlatformHint()
    {
        if (OperatingSystem.IsLinux())
        {
            return "Iskra cannot install the toolchain unattended on Linux yet. "
                + "Install it from your distribution so it lands in a system location: "
                + "'sudo apt install gdb-multiarch' (Debian/Ubuntu) or "
                + "'sudo dnf install arm-none-eabi-gdb' (Fedora).";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "Iskra cannot install the toolchain unattended on macOS yet. "
                + "Install it with 'brew install --cask gcc-arm-embedded'.";
        }

        return "no unattended Arm GNU Toolchain install is available for this operating system";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }
}

public sealed class ToolchainHashMismatchException(string message) : Exception(message);
