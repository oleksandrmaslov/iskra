using System.Net.Http;
using Iskra.Application;
using Iskra.Core;

namespace Iskra.Desktop;

/// <summary>
/// Cross-platform remote-firmware adapter for the Avalonia frontend.
///
/// Windows reuses the DPAPI-backed <see cref="TokenStore"/>, which is the same
/// credential store the shipping WPF station uses, so a station can be switched
/// between the two frontends without re-authenticating. Linux and macOS have no
/// encrypted <see cref="ITokenStore"/> implementation yet, so this fails closed
/// instead of falling back to a plaintext token file. Local (non-remote)
/// releases never reach this adapter, so sideload and file-backed catalogs keep
/// working on every platform.
/// </summary>
internal sealed class DesktopRemoteFirmwareProvider : IRemoteFirmwareProvider
{
    /// <summary>
    /// Cheap, read-only probe of the station credential state so the Flash tab
    /// can hide the sign-in hint when a remote release is already fetchable. It
    /// never decrypts or refreshes; a stale or rejected token still fails
    /// closed inside the workflow with E_AUTH_EXPIRED.
    /// </summary>
    public static bool CanFetchRemoteFirmware()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return new TokenStore().Exists();
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> AcquireAsync(
        FirmwareRelease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (release.ElfSource is null)
            throw new InvalidOperationException("release.ElfSource is null but IsRemote is true");

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "remote firmware download needs an encrypted token store; "
                + "only Windows DPAPI is implemented. Use a local or sideload catalog "
                + "on this platform.");
        }

        using var http = new HttpClient();
        var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);
        var store = new TokenStore();
        var provider = new AccessTokenProvider(store, flow);
        var api = new GitHubReleaseAssetClient(http);
        var cache = new FirmwareCache(api, provider.GetFreshAccessTokenAsync);
        return await cache
            .GetOrDownloadAsync(release.ElfSource, release.ElfSha256, cancellationToken)
            .ConfigureAwait(false);
    }
}
