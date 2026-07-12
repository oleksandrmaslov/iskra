namespace Iskra.Core;

public sealed class NotSignedInException : Exception
{
    public NotSignedInException() : base("not signed in — run `iskra --login` first") { }
}

public sealed class RefreshTokenExpiredException : Exception
{
    public RefreshTokenExpiredException()
        : base("GitHub refresh token expired (>6 months since last login) — sign in again") { }
}

/// <summary>
/// One-stop "give me a valid access token, refreshing-and-saving if needed."
/// Composition of <see cref="ITokenStore"/> and <see cref="GitHubDeviceFlow"/>:
/// loads stored tokens, returns the cached access token if still fresh,
/// otherwise calls <c>refresh_token</c>, persists the *rotated* refresh
/// token + new access token, and returns the access token.
/// </summary>
public sealed class AccessTokenProvider
{
    private static readonly TimeSpan DefaultRefreshSkew = TimeSpan.FromMinutes(5);

    private readonly ITokenStore _store;
    private readonly GitHubDeviceFlow _flow;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _refreshSkew;

    public AccessTokenProvider(
        ITokenStore store,
        GitHubDeviceFlow flow,
        Func<DateTime>? now = null,
        TimeSpan? refreshSkew = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _flow  = flow  ?? throw new ArgumentNullException(nameof(flow));
        _now   = now   ?? (() => DateTime.UtcNow);
        _refreshSkew = refreshSkew ?? DefaultRefreshSkew;
    }

    public async Task<string> GetFreshAccessTokenAsync(CancellationToken ct = default)
    {
        var stored = _store.Load()
            ?? throw new NotSignedInException();

        var now = _now();
        if (stored.AccessTokenIsFresh(now, _refreshSkew))
            return stored.AccessToken;

        if (stored.RefreshTokenIsExpired(now))
        {
            _store.Delete();
            throw new RefreshTokenExpiredException();
        }

        TokenResponse refreshed;
        try { refreshed = await _flow.RefreshTokenAsync(stored.RefreshToken, ct).ConfigureAwait(false); }
        catch (GitHubAuthException ex) when (ex.ErrorCode is "bad_refresh_token" or "invalid_grant")
        {
            // GitHub considers the refresh token unusable — drop the file and force re-auth.
            _store.Delete();
            throw new RefreshTokenExpiredException();
        }

        var rotated = StoredTokens.From(refreshed, now);
        _store.Save(rotated);
        return rotated.AccessToken;
    }
}
