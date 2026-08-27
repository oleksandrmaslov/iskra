using System.Net;
using System.Net.Http;
using System.Text;
using Iskra.Core;

namespace Iskra.Core.Tests;

public class AccessTokenProviderTests
{
    private readonly MemoryTokenStore _store = new();

    private (AccessTokenProvider Provider, StubHandler Handler) BuildProvider(
        DateTime fixedNow, params HttpResponseMessage[] httpResponses)
    {
        var h = new StubHandler(httpResponses);
        var flow = new GitHubDeviceFlow(new HttpClient(h), "Iv23liTEST",
            delay: (_, _) => Task.CompletedTask);
        var p = new AccessTokenProvider(_store, flow, now: () => fixedNow);
        return (p, h);
    }

    private static StoredTokens FreshStored(DateTime now, string accessToken = "gho_FRESH") =>
        new(accessToken, "ghr_REFRESH",
            AccessTokenExpiresAtUtc:  now.AddHours(8),
            RefreshTokenExpiresAtUtc: now.AddMonths(6),
            Scope: "");

    [Fact]
    public async Task Returns_cached_access_token_when_still_fresh()
    {
        var now = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        _store.Save(FreshStored(now));
        var (p, h) = BuildProvider(now);

        var token = await p.GetFreshAccessTokenAsync();
        Assert.Equal("gho_FRESH", token);
        Assert.Empty(h.Requests); // no network call
    }

    [Fact]
    public async Task Throws_NotSignedIn_when_no_stored_tokens()
    {
        var (p, h) = BuildProvider(new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc));
        await Assert.ThrowsAsync<NotSignedInException>(() => p.GetFreshAccessTokenAsync());
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Refreshes_when_access_token_stale_then_saves_rotated_pair()
    {
        var issuedAt = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        _store.Save(FreshStored(issuedAt));

        var refreshTime = issuedAt.AddHours(9); // access token now expired (was 8h)
        var (p, h) = BuildProvider(refreshTime, JsonResp("""
            {
              "access_token":              "gho_NEW",
              "token_type":                "bearer",
              "expires_in":                28800,
              "refresh_token":             "ghr_ROTATED",
              "refresh_token_expires_in":  15897600,
              "scope":                     ""
            }
            """));

        var token = await p.GetFreshAccessTokenAsync();
        Assert.Equal("gho_NEW", token);
        Assert.Single(h.Requests);

        var saved = _store.Load()!;
        Assert.Equal("gho_NEW",     saved.AccessToken);
        Assert.Equal("ghr_ROTATED", saved.RefreshToken); // rotation persisted
        Assert.Equal(refreshTime.AddSeconds(28800),   saved.AccessTokenExpiresAtUtc);
        Assert.Equal(refreshTime.AddSeconds(15897600), saved.RefreshTokenExpiresAtUtc);
    }

    [Fact]
    public async Task Deletes_blob_and_throws_when_refresh_token_locally_expired()
    {
        var issuedAt = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        _store.Save(FreshStored(issuedAt));
        var afterRefreshExpired = issuedAt.AddMonths(7);
        var (p, h) = BuildProvider(afterRefreshExpired);

        await Assert.ThrowsAsync<RefreshTokenExpiredException>(() => p.GetFreshAccessTokenAsync());
        Assert.Empty(h.Requests);
        Assert.False(_store.Exists());
    }

    [Fact]
    public async Task Deletes_blob_and_throws_when_GitHub_rejects_refresh_token()
    {
        var issuedAt = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        _store.Save(FreshStored(issuedAt));
        var (p, h) = BuildProvider(issuedAt.AddHours(9),
            JsonResp("{\"error\":\"bad_refresh_token\"}"));

        await Assert.ThrowsAsync<RefreshTokenExpiredException>(() => p.GetFreshAccessTokenAsync());
        Assert.False(_store.Exists());
    }

    [Fact]
    public async Task Refresh_skew_triggers_proactive_refresh()
    {
        var issuedAt = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        _store.Save(FreshStored(issuedAt));

        // 30 s before expiry, with a 5-minute skew → considered stale.
        var nearExpiry = issuedAt.AddHours(8).AddSeconds(-30);
        var (p, h) = BuildProvider(nearExpiry, JsonResp("""
            {
              "access_token": "gho_PROACTIVE", "token_type": "bearer",
              "expires_in":   28800,
              "refresh_token":"ghr_R2", "refresh_token_expires_in": 15897600,
              "scope": ""
            }
            """));

        var token = await p.GetFreshAccessTokenAsync();
        Assert.Equal("gho_PROACTIVE", token);
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_rotating_refresh()
    {
        var issuedAt = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        _store.Save(FreshStored(issuedAt));
        var refreshTime = issuedAt.AddHours(9);
        var response = JsonResp("""
            {
              "access_token": "gho_ONCE", "token_type": "bearer",
              "expires_in": 28800, "refresh_token": "ghr_ONCE",
              "refresh_token_expires_in": 15897600, "scope": ""
            }
            """);
        var (provider, handler) = BuildProvider(refreshTime, response);

        var tokens = await Task.WhenAll(
            provider.GetFreshAccessTokenAsync(),
            provider.GetFreshAccessTokenAsync());

        Assert.Equal(new[] { "gho_ONCE", "gho_ONCE" }, tokens);
        Assert.Single(handler.Requests);
        Assert.Equal("ghr_ONCE", _store.Load()!.RefreshToken);
    }

    [Fact]
    public async Task Rejected_stale_refresh_does_not_delete_newer_credentials()
    {
        var issuedAt = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        var old = FreshStored(issuedAt);
        var replacement = FreshStored(issuedAt.AddHours(9), "gho_OTHER") with
        {
            RefreshToken = "ghr_OTHER",
        };
        var store = new ReplacingTokenStore(old, replacement);
        var handler = new StubHandler(JsonResp("{\"error\":\"bad_refresh_token\"}"));
        var flow = new GitHubDeviceFlow(new HttpClient(handler), "Iv23liTEST",
            delay: (_, _) => Task.CompletedTask);
        var provider = new AccessTokenProvider(store, flow, now: () => issuedAt.AddHours(9));

        var token = await provider.GetFreshAccessTokenAsync();

        Assert.Equal("gho_OTHER", token);
        Assert.False(store.DeleteCalled);
        Assert.Equal("ghr_OTHER", store.Load()!.RefreshToken);
    }

    [Fact]
    public async Task Cross_process_refresh_lock_wait_honors_cancellation()
    {
        var issuedAt = new DateTime(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);
        _store.Save(FreshStored(issuedAt));
        var refreshTime = issuedAt.AddHours(9);
        var (provider, handler) = BuildProvider(refreshTime, JsonResp("""
            {
              "access_token": "gho_NEVER", "token_type": "bearer",
              "expires_in": 28800, "refresh_token": "ghr_NEVER",
              "refresh_token_expires_in": 15897600, "scope": ""
            }
            """));

        var mutexName = AccessTokenProvider.RefreshMutexName(_store.Path);
        await using var held = await SystemWideMutexLease.AcquireAsync(
            mutexName, currentUserOnly: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetFreshAccessTokenAsync(cancellation.Token));
        Assert.Empty(handler.Requests);
        Assert.Equal("ghr_REFRESH", _store.Load()!.RefreshToken);
    }

    [Fact]
    public async Task Cross_process_refresh_lock_serializes_distinct_leases()
    {
        var mutexName = $"Iskra.Tests.TokenRefresh.{Guid.NewGuid():N}";
        await using var first = await SystemWideMutexLease.AcquireAsync(
            mutexName, currentUserOnly: true);

        Assert.Null(SystemWideMutexLease.TryAcquire(mutexName, currentUserOnly: true));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SystemWideMutexLease.AcquireAsync(
                mutexName, currentUserOnly: true, cancellation.Token));
    }

    // --- helpers ---------------------------------------------------------

    private static HttpResponseMessage JsonResp(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class MemoryTokenStore : ITokenStore
    {
        private StoredTokens? _tokens;

        public string Path => "memory-token-store";
        public bool Exists() => _tokens is not null;
        public StoredTokens? Load() => _tokens;
        public void Save(StoredTokens tokens) => _tokens = tokens;
        public void Delete() => _tokens = null;
    }

    private sealed class ReplacingTokenStore(
        StoredTokens original,
        StoredTokens replacement) : ITokenStore
    {
        private StoredTokens? _tokens = original;
        private int _loads;

        public string Path => $"memory-token-store-{Guid.NewGuid():N}";
        public bool DeleteCalled { get; private set; }
        public bool Exists() => _tokens is not null;
        public StoredTokens? Load()
        {
            _loads++;
            if (_loads == 2)
                _tokens = replacement;
            return _tokens;
        }
        public void Save(StoredTokens tokens) => _tokens = tokens;
        public void Delete()
        {
            DeleteCalled = true;
            _tokens = null;
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(params HttpResponseMessage[] r)
            => _responses = new Queue<HttpResponseMessage>(r);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));
            if (_responses.Count == 0)
                throw new InvalidOperationException("ran out of responses");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
