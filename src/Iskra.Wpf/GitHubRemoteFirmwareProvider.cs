using System.Net.Http;
using Iskra.Application;
using Iskra.Core;

namespace Iskra.Wpf;

/// <summary>
/// Windows remote-firmware adapter used by the supported WPF frontend. Token
/// storage remains DPAPI-backed here while the shared workflow stays portable.
/// </summary>
internal sealed class GitHubRemoteFirmwareProvider : IRemoteFirmwareProvider
{
    public async Task<string> AcquireAsync(
        FirmwareRelease release,
        CancellationToken cancellationToken)
    {
        if (release.ElfSource is null)
            throw new InvalidOperationException("release.ElfSource is null but IsRemote is true");

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
