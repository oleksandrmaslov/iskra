using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;

namespace Iskra.Core;

/// <summary>
/// Thrown for any failure in the GitHub App auth flow used by Sprint 5's log
/// shipper: PEM loading, JWT minting, installation-token exchange. Distinct
/// from <see cref="GitHubAuthException"/> (which covers user Device Flow).
/// </summary>
public sealed class GitHubAppAuthException : Exception
{
    public string? ErrorCode { get; }
    public GitHubAppAuthException(string message, string? errorCode = null, Exception? inner = null)
        : base(message, inner) { ErrorCode = errorCode; }
}

/// <summary>
/// Mints installation access tokens for a GitHub App installation. Two-step:
/// (1) sign a 10-minute RS256 JWT with the App's private key, (2) POST that
/// to <c>/app/installations/{id}/access_tokens</c> and cache the returned
/// installation token until it nears expiry.
///
/// <para>
/// The PEM key is loaded lazily via the <c>loadKey</c> delegate so callers
/// can inject either a file-backed loader (production) or an in-memory RSA
/// (tests). Each mint disposes the returned RSA — minting only happens on
/// cache miss (~hourly), so the cost is negligible.
/// </para>
/// </summary>
public sealed class GitHubAppInstallationTokenProvider
{
    public const int MaxTokenResponseBytes = 64 * 1024;
    public const int MaxPrivateKeyBytes = 64 * 1024;
    public const string ApiBaseUrl  = "https://api.github.com";
    public const string ApiAccept   = "application/vnd.github+json";
    public const string ApiVersion  = "2022-11-28";

    private readonly HttpClient _http;
    private readonly string _appId;
    private readonly string _installationId;
    private readonly Func<RSA> _loadKey;
    private readonly Func<DateTime> _utcNow;
    private readonly string _userAgent;
    private readonly TimeSpan _skew = TimeSpan.FromMinutes(5);

    private string? _cachedToken;
    private DateTime _cachedExpiry = DateTime.MinValue;

    public GitHubAppInstallationTokenProvider(
        HttpClient http,
        string appId,
        string installationId,
        Func<RSA> loadKey,
        string userAgent = "Iskra",
        Func<DateTime>? utcNow = null)
    {
        if (http is null) throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(appId))
            throw new ArgumentException("appId required", nameof(appId));
        if (string.IsNullOrWhiteSpace(installationId))
            throw new ArgumentException("installationId required", nameof(installationId));
        if (loadKey is null) throw new ArgumentNullException(nameof(loadKey));

        _http = http;
        _appId = appId;
        _installationId = installationId;
        _loadKey = loadKey;
        _userAgent = userAgent;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Returns a valid installation access token. Cached across calls until
    /// within <see cref="_skew"/> of expiry.
    /// </summary>
    public async Task<string> GetInstallationTokenAsync(CancellationToken ct = default)
    {
        if (_cachedToken is not null && _cachedExpiry - _utcNow() > _skew)
            return _cachedToken;

        var jwt = MintAppJwt();
        var url = $"{ApiBaseUrl}/app/installations/{Uri.EscapeDataString(_installationId)}/access_tokens";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ApiAccept));
        req.Headers.UserAgent.ParseAdd(_userAgent);
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);

        using var resp = await _http.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await BoundedHttpContent.ReadUtf8StringAsync(
            resp.Content, MaxTokenResponseBytes, ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new GitHubAppAuthException(
                $"installation token request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}: {Snip(body)}",
                ((int)resp.StatusCode).ToString());

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("token", out var tokenEl) ||
            tokenEl.ValueKind != JsonValueKind.String)
            throw new GitHubAppAuthException("installation token response missing 'token'");
        if (!doc.RootElement.TryGetProperty("expires_at", out var expEl) ||
            expEl.ValueKind != JsonValueKind.String)
            throw new GitHubAppAuthException("installation token response missing 'expires_at'");

        _cachedToken  = tokenEl.GetString();
        _cachedExpiry = DateTime.Parse(expEl.GetString()!).ToUniversalTime();
        return _cachedToken!;
    }

    /// <summary>
    /// Mints the short-lived (10 min) JWT GitHub requires to authenticate as
    /// the App itself. <c>iat</c> is backdated 60 s to absorb mild clock skew.
    /// </summary>
    public string MintAppJwt()
    {
        var now = _utcNow();
        var iat = ToUnixSeconds(now.AddSeconds(-60));
        var exp = ToUnixSeconds(now.AddMinutes(10));
        var header  = Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}""");
        var payload = Encoding.UTF8.GetBytes($$"""{"iat":{{iat}},"exp":{{exp}},"iss":"{{_appId}}"}""");
        var signingInput = Base64Url(header) + "." + Base64Url(payload);

        byte[] sig;
        using (var rsa = _loadKey())
        {
            sig = rsa.SignData(
                Encoding.UTF8.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        return signingInput + "." + Base64Url(sig);
    }

    /// <summary>
    /// Loads a PKCS#1 or PKCS#8 PEM-encoded RSA private key from disk via
    /// BouncyCastle and returns it as a .NET <see cref="RSA"/>. Throws
    /// <see cref="GitHubAppAuthException"/> for missing or unsupported files.
    /// </summary>
    public static RSA LoadPemKey(string pemPath)
    {
        if (string.IsNullOrWhiteSpace(pemPath))
            throw new ArgumentException("pemPath required", nameof(pemPath));
        if (!File.Exists(pemPath))
            throw new GitHubAppAuthException($"GitHub App private key not found: {pemPath}");

        object? obj;
        try
        {
            var pem = Encoding.UTF8.GetString(
                BoundedFileReader.ReadAllBytes(pemPath, MaxPrivateKeyBytes));
            using var sr = new StringReader(pem);
            obj = new PemReader(sr).ReadObject();
        }
        catch (Exception ex)
        {
            throw new GitHubAppAuthException($"failed to read PEM at {pemPath}: {ex.Message}", inner: ex);
        }
        if (obj is AsymmetricCipherKeyPair kp) obj = kp.Private;
        if (obj is RsaPrivateCrtKeyParameters rsaParams)
            return ConvertRsa(rsaParams);
        throw new GitHubAppAuthException(
            $"unsupported PEM key type at {pemPath}: {obj?.GetType().FullName ?? "null"}");
    }

    // Manual conversion of BouncyCastle's RsaPrivateCrtKeyParameters to a
    // .NET RSA. Sidesteps the Windows-only DotNetUtilities.ToRSA helper.
    // .NET's RSAParameters demands each CRT component be left-padded to the
    // expected size (Modulus = keysize, P/Q/DP/DQ/QInv/D = matching halves
    // or full); BouncyCastle's ToByteArrayUnsigned drops leading zeros, so
    // we re-pad here.
    private static RSA ConvertRsa(RsaPrivateCrtKeyParameters p)
    {
        int modLen  = (p.Modulus.BitLength + 7) / 8;
        int halfLen = (modLen + 1) / 2;
        var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus  = PadLeft(p.Modulus.ToByteArrayUnsigned(),         modLen),
            Exponent = p.PublicExponent.ToByteArrayUnsigned(),
            D        = PadLeft(p.Exponent.ToByteArrayUnsigned(),        modLen),
            P        = PadLeft(p.P.ToByteArrayUnsigned(),               halfLen),
            Q        = PadLeft(p.Q.ToByteArrayUnsigned(),               halfLen),
            DP       = PadLeft(p.DP.ToByteArrayUnsigned(),              halfLen),
            DQ       = PadLeft(p.DQ.ToByteArrayUnsigned(),              halfLen),
            InverseQ = PadLeft(p.QInv.ToByteArrayUnsigned(),            halfLen),
        });
        return rsa;
    }

    private static byte[] PadLeft(byte[] bytes, int targetLen)
    {
        if (bytes.Length >= targetLen) return bytes;
        var padded = new byte[targetLen];
        Buffer.BlockCopy(bytes, 0, padded, targetLen - bytes.Length, bytes.Length);
        return padded;
    }

    private static long ToUnixSeconds(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Snip(string s) => s.Length <= 200 ? s : s[..200] + "...";
}
