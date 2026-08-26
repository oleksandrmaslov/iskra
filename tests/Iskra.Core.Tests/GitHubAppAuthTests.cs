using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iskra.Core;

namespace Iskra.Core.Tests;

public class GitHubAppAuthTests
{
    private const string AppId = "123456";
    private const string InstallationId = "987654";

    // The provider's _loadKey closure must return a *fresh* RSA each call
    // (provider disposes it inside MintAppJwt). Cloning via ExportParameters
    // is the simplest way to share a single keypair across mints without
    // re-importing PEM.
    private static (Func<RSA> loader, RSA verifier) MakeRsaPair()
    {
        var seed = RSA.Create(2048);
        var parameters = seed.ExportParameters(includePrivateParameters: true);
        var pub = RSA.Create();
        pub.ImportParameters(seed.ExportParameters(includePrivateParameters: false));
        Func<RSA> loader = () =>
        {
            var r = RSA.Create();
            r.ImportParameters(parameters);
            return r;
        };
        seed.Dispose();
        return (loader, pub);
    }

    [Fact]
    public void Mint_jwt_has_three_segments_with_rs256_header()
    {
        var (load, _) = MakeRsaPair();
        var sut = new GitHubAppInstallationTokenProvider(
            new HttpClient(new StubHandler()), AppId, InstallationId, load);
        var jwt = sut.MintAppJwt();
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);

        var header = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[0])));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT",   header.RootElement.GetProperty("typ").GetString());
    }

    [Fact]
    public void Mint_jwt_payload_carries_iat_exp_iss()
    {
        var (load, _) = MakeRsaPair();
        var fixedNow = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var sut = new GitHubAppInstallationTokenProvider(
            new HttpClient(new StubHandler()), AppId, InstallationId, load, utcNow: () => fixedNow);
        var jwt = sut.MintAppJwt();
        var parts = jwt.Split('.');
        var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
        var expectedIat = new DateTimeOffset(fixedNow.AddSeconds(-60)).ToUnixTimeSeconds();
        var expectedExp = new DateTimeOffset(fixedNow.AddMinutes(10)).ToUnixTimeSeconds();
        Assert.Equal(expectedIat, payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(expectedExp, payload.RootElement.GetProperty("exp").GetInt64());
        Assert.Equal(AppId, payload.RootElement.GetProperty("iss").GetString());
    }

    [Fact]
    public void Mint_jwt_signature_verifies_with_public_key()
    {
        var (load, pub) = MakeRsaPair();
        var sut = new GitHubAppInstallationTokenProvider(
            new HttpClient(new StubHandler()), AppId, InstallationId, load);
        var jwt = sut.MintAppJwt();
        var parts = jwt.Split('.');
        var signingInput = Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]);
        var sig = Base64UrlDecode(parts[2]);
        Assert.True(pub.VerifyData(signingInput, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task Get_installation_token_caches_until_near_expiry()
    {
        var (load, _) = MakeRsaPair();
        var now = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var handler = new StubHandler(
            MakeTokenResponse("ghs_first",  now.AddHours(1)),
            MakeTokenResponse("ghs_second", now.AddHours(2)));
        var sut = new GitHubAppInstallationTokenProvider(
            new HttpClient(handler), AppId, InstallationId, load, utcNow: () => now);

        var a = await sut.GetInstallationTokenAsync();
        var b = await sut.GetInstallationTokenAsync();
        Assert.Equal("ghs_first", a);
        Assert.Equal("ghs_first", b);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Get_installation_token_refreshes_when_within_skew()
    {
        var (load, _) = MakeRsaPair();
        var now = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var clock = now;
        var handler = new StubHandler(
            MakeTokenResponse("ghs_first",  now.AddHours(1)),
            MakeTokenResponse("ghs_second", now.AddHours(2)));
        var sut = new GitHubAppInstallationTokenProvider(
            new HttpClient(handler), AppId, InstallationId, load, utcNow: () => clock);

        var a = await sut.GetInstallationTokenAsync();
        Assert.Equal("ghs_first", a);
        // Advance to within the 5-minute skew window → must refresh.
        clock = now.AddHours(1).AddMinutes(-3);
        var b = await sut.GetInstallationTokenAsync();
        Assert.Equal("ghs_second", b);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Get_installation_token_throws_on_http_error()
    {
        var (load, _) = MakeRsaPair();
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"message":"Bad credentials"}""")
        });
        var sut = new GitHubAppInstallationTokenProvider(
            new HttpClient(handler), AppId, InstallationId, load);

        var ex = await Assert.ThrowsAsync<GitHubAppAuthException>(() => sut.GetInstallationTokenAsync());
        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task Get_installation_token_rejects_an_oversized_response()
    {
        var (load, _) = MakeRsaPair();
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x',
                GitHubAppInstallationTokenProvider.MaxTokenResponseBytes + 1)),
        });
        var sut = new GitHubAppInstallationTokenProvider(
            new HttpClient(handler), AppId, InstallationId, load);

        await Assert.ThrowsAsync<HttpContentSizeLimitException>(() =>
            sut.GetInstallationTokenAsync());
    }

    [Fact]
    public void Load_pem_key_throws_when_file_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"no-such-key-{Guid.NewGuid():N}.pem");
        var ex = Assert.Throws<GitHubAppAuthException>(() => GitHubAppInstallationTokenProvider.LoadPemKey(path));
        Assert.Contains(path, ex.Message);
    }

    private static HttpResponseMessage MakeTokenResponse(string token, DateTime expiresAt)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"token":"{{token}}","expires_at":"{{expiresAt:yyyy-MM-ddTHH:mm:ssZ}}"}""")
        };

    private static byte[] Base64UrlDecode(string s)
    {
        var pad = s.Length % 4 == 0 ? "" : new string('=', 4 - s.Length % 4);
        return Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + pad);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();
        public StubHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var snap = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) snap.Headers.TryAddWithoutValidation(h.Key, h.Value);
            Requests.Add(snap);
            if (_responses.Count == 0)
                throw new InvalidOperationException("StubHandler ran out of canned responses");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
