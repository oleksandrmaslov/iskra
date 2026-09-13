using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Iskra.Core;

namespace Iskra.Core.Tests;

/// <summary>
/// The in-app toolchain install downloads a vendor installer and then runs it,
/// so the SHA-256 pin is the only thing standing between a station and
/// executing whatever the network returned. These tests pin that behaviour and
/// guard the pin itself against drifting from the packaged installer's copy.
/// </summary>
public class ToolchainInstallerTests : IDisposable
{
    private readonly string _cacheDir;

    public ToolchainInstallerTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"iskra-toolchain-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true);
    }

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task A_download_that_misses_the_pin_is_discarded_and_never_cached()
    {
        var served = Encoding.UTF8.GetBytes("this is not the arm toolchain");
        var handler = new StubHandler(served);
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<ToolchainHashMismatchException>(() =>
            ToolchainInstaller.AcquireVerifiedInstallerAsync(
                "https://example.invalid/toolchain.msi",
                expectedSha256: new string('a', 64),
                _cacheDir,
                "toolchain.msi",
                progress: null,
                http,
                CancellationToken.None));

        Assert.Contains("does not match the pinned SHA-256", ex.Message);
        // Nothing executable may survive a failed verification, including the
        // partial temp file.
        Assert.Empty(Directory.GetFiles(_cacheDir));
    }

    [Fact]
    public async Task A_matching_download_is_cached_and_reported()
    {
        var served = Encoding.UTF8.GetBytes("pretend installer payload");
        var handler = new StubHandler(served);
        using var http = new HttpClient(handler);

        var path = await ToolchainInstaller.AcquireVerifiedInstallerAsync(
            "https://example.invalid/toolchain.msi",
            Sha256Of(served),
            _cacheDir,
            "toolchain.msi",
            progress: null,
            http,
            CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(served, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task An_already_verified_cache_entry_is_not_downloaded_again()
    {
        var served = Encoding.UTF8.GetBytes("pretend installer payload");
        Directory.CreateDirectory(_cacheDir);
        await File.WriteAllBytesAsync(Path.Combine(_cacheDir, "toolchain.msi"), served);

        var handler = new StubHandler(served);
        using var http = new HttpClient(handler);

        var path = await ToolchainInstaller.AcquireVerifiedInstallerAsync(
            "https://example.invalid/toolchain.msi",
            Sha256Of(served),
            _cacheDir,
            "toolchain.msi",
            progress: null,
            http,
            CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task A_tampered_cache_entry_is_replaced_rather_than_trusted()
    {
        var served = Encoding.UTF8.GetBytes("pretend installer payload");
        Directory.CreateDirectory(_cacheDir);
        await File.WriteAllBytesAsync(
            Path.Combine(_cacheDir, "toolchain.msi"),
            Encoding.UTF8.GetBytes("swapped out on disk"));

        var handler = new StubHandler(served);
        using var http = new HttpClient(handler);

        var path = await ToolchainInstaller.AcquireVerifiedInstallerAsync(
            "https://example.invalid/toolchain.msi",
            Sha256Of(served),
            _cacheDir,
            "toolchain.msi",
            progress: null,
            http,
            CancellationToken.None);

        Assert.Equal(served, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Progress_is_reported_while_downloading()
    {
        var served = new byte[200_000];
        Random.Shared.NextBytes(served);
        var handler = new StubHandler(served);
        using var http = new HttpClient(handler);

        var reports = new List<ToolchainDownloadProgress>();
        await ToolchainInstaller.AcquireVerifiedInstallerAsync(
            "https://example.invalid/toolchain.msi",
            Sha256Of(served),
            _cacheDir,
            "toolchain.msi",
            new Progress<ToolchainDownloadProgress>(reports.Add),
            http,
            CancellationToken.None);

        // Progress is delivered asynchronously; the final byte count is what
        // matters, so wait briefly for the queue to drain.
        for (var i = 0; i < 50 && (reports.Count == 0 || reports[^1].BytesRead != served.Length); i++)
            await Task.Delay(10);

        Assert.NotEmpty(reports);
        Assert.Equal(served.Length, reports[^1].BytesRead);
    }

    [Fact]
    public void Pins_match_the_packaged_installer_pins()
    {
        // installer/arm-toolchain.pins.ps1 is what the setup EXE bundles. If the
        // two drift, the in-app install and the packaged install would deliver
        // different compilers under the same version claim.
        var pinsFile = Path.Combine(RepoRoot(), "installer", "arm-toolchain.pins.ps1");
        Assert.True(File.Exists(pinsFile), $"pins file not found at {pinsFile}");
        var text = File.ReadAllText(pinsFile);

        Assert.Equal(ArmToolchainPins.Version, PsValue(text, "ArmToolchainVersion"));
        Assert.Equal(ArmToolchainPins.WindowsFileName, PsValue(text, "ArmToolchainFileName"));
        Assert.Equal(ArmToolchainPins.WindowsUrl, PsValue(text, "ArmToolchainUrl"));
        Assert.Equal(
            ArmToolchainPins.WindowsSha256,
            PsValue(text, "ArmToolchainSha256"),
            ignoreCase: true);
    }

    [Fact]
    public void The_pinned_url_is_https_and_names_the_pinned_file()
    {
        Assert.StartsWith("https://", ArmToolchainPins.WindowsUrl, StringComparison.Ordinal);
        Assert.EndsWith(ArmToolchainPins.WindowsFileName, ArmToolchainPins.WindowsUrl, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", ArmToolchainPins.WindowsSha256);
    }

    private static string PsValue(string script, string variable)
    {
        var match = Regex.Match(
            script,
            $@"^\s*\${Regex.Escape(variable)}\s*=\s*""(?<value>[^""]*)""",
            RegexOptions.Multiline);
        Assert.True(match.Success, $"${variable} not found in the pins script");
        return match.Groups["value"].Value;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Iskra.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private sealed class StubHandler(byte[] payload) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
        }
    }
}
