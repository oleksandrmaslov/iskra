using System.Net.Http;
using Iskra.Application;
using Iskra.Core;

namespace Iskra.Desktop;

/// <summary>
/// Cross-platform remote-firmware adapter for the Avalonia frontend.
///
/// Windows reuses the WPF/CLI DPAPI store, Linux uses Secret Service through
/// <c>secret-tool</c>, and macOS uses the login Keychain through
/// <c>/usr/bin/security</c>. <see cref="PlatformTokenStoreFactory"/> returns
/// <c>null</c> when the encrypted OS store is unavailable, so this still fails
/// closed instead of ever falling back to a plaintext token file. Local
/// releases never reach this adapter.
/// </summary>
internal sealed class DesktopRemoteFirmwareProvider : IRemoteFirmwareProvider
{
    /// <summary>
    /// Read-only classification of the stored credential state. The shared auth
    /// workflow loads the native-store value but never refreshes it; a stale or
    /// rejected token still fails closed inside the workflow with
    /// E_AUTH_EXPIRED.
    /// </summary>
    public static bool CanFetchRemoteFirmware()
    {
        try
        {
            var store = PlatformTokenStoreFactory.Create();
            return store is not null
                && new AuthWorkflow(store).Evaluate().CanFetchRemoteFirmware;
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

        var store = PlatformTokenStoreFactory.Create();
        if (store is null)
        {
            throw new PlatformNotSupportedException(
                "remote firmware download needs the encrypted OS credential store; "
                + "install secret-tool on Linux or unlock Keychain on macOS");
        }

        using var http = new HttpClient();
        var flow = new GitHubDeviceFlow(http, GitHubAppConfig.ClientId);
        var provider = new AccessTokenProvider(store, flow);
        var api = new GitHubReleaseAssetClient(http);
        var cache = new FirmwareCache(api, provider.GetFreshAccessTokenAsync);
        return await cache
            .GetOrDownloadAsync(release.ElfSource, release.ElfSha256, cancellationToken)
            .ConfigureAwait(false);
    }
}
