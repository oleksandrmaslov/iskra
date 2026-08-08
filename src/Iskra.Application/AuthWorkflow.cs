using Iskra.Core;

namespace Iskra.Application;

public enum AuthStatus
{
    /// <summary>No encrypted credential store exists for this platform.</summary>
    SecureStoreUnavailable,
    /// <summary>The build carries no GitHub App client ID.</summary>
    ClientNotConfigured,
    /// <summary>Credentials exist on disk but could not be decrypted.</summary>
    TokenStoreCorrupt,
    NotSignedIn,
    /// <summary>The refresh token has expired; a new sign-in is required.</summary>
    SessionExpired,
    SignedIn,
}

public sealed record AuthSnapshot(
    AuthStatus Status,
    bool AccessTokenFresh,
    DateTime? AccessTokenExpiresAtUtc,
    DateTime? RefreshTokenExpiresAtUtc,
    string? Diagnostic)
{
    /// <summary>Offering sign-in is pointless without a store or a client ID.</summary>
    public bool CanSignIn =>
        Status is not (AuthStatus.SecureStoreUnavailable or AuthStatus.ClientNotConfigured);

    /// <summary>There is something on disk worth deleting.</summary>
    public bool CanSignOut =>
        Status is AuthStatus.TokenStoreCorrupt or AuthStatus.SessionExpired or AuthStatus.SignedIn;

    /// <summary>
    /// Whether a remote firmware download can currently succeed. A stale access
    /// token still counts: the provider refreshes it. An expired refresh token
    /// does not.
    /// </summary>
    public bool CanFetchRemoteFirmware => Status == AuthStatus.SignedIn;
}

/// <summary>
/// Classifies the station's stored GitHub credentials for presentation, shared
/// by WPF and Avalonia so both frontends agree on what "signed in" means.
///
/// <para>The store is injected rather than constructed: it is Windows-only
/// today, and a platform without an encrypted implementation must pass
/// <c>null</c> and get <see cref="AuthStatus.SecureStoreUnavailable"/> instead of
/// a plaintext fallback.</para>
/// </summary>
public sealed class AuthWorkflow
{
    /// <summary>
    /// An access token expiring within this window is treated as needing a
    /// refresh, so the UI never claims "valid" for a token that will be replaced
    /// on the next call.
    /// </summary>
    public static readonly TimeSpan DefaultRefreshSkew = TimeSpan.FromMinutes(5);

    private readonly ITokenStore? _store;
    private readonly bool _clientConfigured;
    private readonly TimeSpan _refreshSkew;

    public AuthWorkflow(ITokenStore? store, bool? clientConfigured = null, TimeSpan? refreshSkew = null)
    {
        _store = store;
        _clientConfigured = clientConfigured ?? GitHubAppConfig.IsConfigured;
        _refreshSkew = refreshSkew ?? DefaultRefreshSkew;
    }

    public AuthSnapshot Evaluate(DateTime? nowUtc = null)
    {
        if (_store is null) return Simple(AuthStatus.SecureStoreUnavailable);
        if (!_clientConfigured) return Simple(AuthStatus.ClientNotConfigured);

        StoredTokens? stored;
        try
        {
            stored = _store.Load();
        }
        catch (TokenStoreException ex)
        {
            return new AuthSnapshot(AuthStatus.TokenStoreCorrupt, false, null, null, ex.Message);
        }
        catch (Exception ex)
        {
            // A frontend refreshing its status bar must never crash on a
            // damaged or unreadable credential blob.
            return new AuthSnapshot(AuthStatus.TokenStoreCorrupt, false, null, null, ex.Message);
        }

        if (stored is null) return Simple(AuthStatus.NotSignedIn);

        var now = nowUtc ?? DateTime.UtcNow;
        if (stored.RefreshTokenIsExpired(now))
        {
            return new AuthSnapshot(
                AuthStatus.SessionExpired,
                false,
                stored.AccessTokenExpiresAtUtc,
                stored.RefreshTokenExpiresAtUtc,
                null);
        }

        return new AuthSnapshot(
            AuthStatus.SignedIn,
            stored.AccessTokenIsFresh(now, _refreshSkew),
            stored.AccessTokenExpiresAtUtc,
            stored.RefreshTokenExpiresAtUtc,
            null);
    }

    /// <summary>
    /// Deletes the stored credentials and returns the resulting state. A delete
    /// failure is reported rather than thrown so the caller can surface it
    /// inline next to the status it was already showing.
    /// </summary>
    public AuthSnapshot SignOut()
    {
        if (_store is null) return Simple(AuthStatus.SecureStoreUnavailable);

        try
        {
            _store.Delete();
        }
        catch (Exception ex)
        {
            return new AuthSnapshot(AuthStatus.TokenStoreCorrupt, false, null, null, ex.Message);
        }

        return Evaluate();
    }

    private static AuthSnapshot Simple(AuthStatus status) => new(status, false, null, null, null);
}
