using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Iskra.Core;

public sealed class TokenStoreException : Exception
{
    public TokenStoreException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Persistence boundary for GitHub Device Flow credentials. Platform-neutral
/// orchestration depends on this contract; each desktop platform must provide
/// an encrypted implementation (never a plaintext fallback).
/// </summary>
public interface ITokenStore
{
    string Path { get; }
    bool Exists();
    StoredTokens? Load();
    void Save(StoredTokens tokens);
    void Delete();
}

/// <summary>
/// Encrypted-on-disk snapshot of the GitHub OAuth tokens for this station.
/// Stored as JSON inside a DPAPI-encrypted blob. Refresh tokens rotate on
/// every refresh; <see cref="TokenStore.Save"/> always overwrites.
/// </summary>
public sealed record StoredTokens(
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresAtUtc,
    DateTime RefreshTokenExpiresAtUtc,
    string? Scope)
{
    /// <summary>
    /// True if the cached access token is still good for at least <paramref name="skew"/>
    /// from <paramref name="now"/>. Use a generous skew (≥ 60 s) so a request
    /// in flight never expires server-side mid-call.
    /// </summary>
    public bool AccessTokenIsFresh(DateTime now, TimeSpan skew) =>
        AccessTokenExpiresAtUtc - skew > now;

    public bool RefreshTokenIsExpired(DateTime now) =>
        now >= RefreshTokenExpiresAtUtc;

    /// <summary>
    /// Builds a <see cref="StoredTokens"/> from a fresh
    /// <see cref="TokenResponse"/> + the wall-clock at issue time.
    /// </summary>
    public static StoredTokens From(TokenResponse t, DateTime nowUtc) =>
        new(
            AccessToken:              t.AccessToken,
            RefreshToken:             t.RefreshToken,
            AccessTokenExpiresAtUtc:  nowUtc.AddSeconds(t.ExpiresIn),
            RefreshTokenExpiresAtUtc: nowUtc.AddSeconds(t.RefreshTokenExpiresIn),
            Scope:                    t.Scope);
}

/// <summary>
/// Per-machine token store backed by <c>%PROGRAMDATA%\Iskra\auth.bin</c>.
/// Uses Windows DPAPI <see cref="DataProtectionScope.LocalMachine"/> so any
/// Windows user on the station can use the cached credentials — operators
/// don't need to re-auth when Windows account-switches.
/// <para>Atomic write: serialize → encrypt → write to <c>.tmp</c> → rename.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TokenStore : ITokenStore
{
    public const string DefaultDirectoryName = "Iskra";
    public const string DefaultFileName      = "auth.bin";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; }
    public DataProtectionScope Scope { get; }

    public TokenStore(
        string? overridePath = null,
        DataProtectionScope scope = DataProtectionScope.LocalMachine)
    {
        Path = overridePath ?? DefaultPath();
        Scope = scope;
    }

    /// <summary><c>%PROGRAMDATA%\Iskra\auth.bin</c> on Windows.</summary>
    public static string DefaultPath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return System.IO.Path.Combine(programData, DefaultDirectoryName, DefaultFileName);
    }

    public bool Exists() => File.Exists(Path);

    /// <summary>
    /// Returns the decrypted snapshot, or <c>null</c> if no file is present.
    /// Throws <see cref="TokenStoreException"/> if the file exists but cannot
    /// be decrypted or deserialised — caller's contract is to delete the
    /// file and prompt for re-auth in that case.
    /// </summary>
    public StoredTokens? Load()
    {
        if (!File.Exists(Path)) return null;

        byte[] cipher;
        try { cipher = File.ReadAllBytes(Path); }
        catch (IOException ex) { throw new TokenStoreException($"could not read {Path}: {ex.Message}", ex); }
        catch (UnauthorizedAccessException ex) { throw new TokenStoreException($"access denied reading {Path}", ex); }

        byte[] plain;
        try { plain = ProtectedData.Unprotect(cipher, optionalEntropy: null, scope: Scope); }
        catch (CryptographicException ex)
        {
            throw new TokenStoreException(
                $"token blob at {Path} could not be decrypted (wrong scope, corrupted, or " +
                "machine credentials changed) — delete the file and re-authenticate", ex);
        }

        try
        {
            var v = JsonSerializer.Deserialize<StoredTokens>(plain, JsonOpts);
            if (v is null) throw new TokenStoreException($"token blob at {Path} deserialised to null");
            ValidateLoaded(v);
            return v;
        }
        catch (JsonException ex)
        {
            throw new TokenStoreException($"token blob at {Path} is not valid JSON: {ex.Message}", ex);
        }
    }

    public void Save(StoredTokens tokens)
    {
        if (tokens is null) throw new ArgumentNullException(nameof(tokens));
        ValidateLoaded(tokens);

        var json   = JsonSerializer.SerializeToUtf8Bytes(tokens, JsonOpts);
        var cipher = ProtectedData.Protect(json, optionalEntropy: null, scope: Scope);

        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = Path + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        try { File.Move(tmp, Path, overwrite: true); }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    /// <summary>
    /// Removes the on-disk blob. No-op if already absent. Use after
    /// <c>--logout</c> or when load fails with <see cref="TokenStoreException"/>.
    /// </summary>
    public void Delete()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }

    private static void ValidateLoaded(StoredTokens t)
    {
        if (string.IsNullOrEmpty(t.AccessToken))
            throw new TokenStoreException("stored tokens: access_token missing");
        if (string.IsNullOrEmpty(t.RefreshToken))
            throw new TokenStoreException("stored tokens: refresh_token missing");
        if (t.AccessTokenExpiresAtUtc.Kind != DateTimeKind.Utc &&
            t.AccessTokenExpiresAtUtc != default)
        {
            // System.Text.Json yields Unspecified for naked ISO-8601; treat
            // as UTC since that's the only kind we write.
        }
    }
}
