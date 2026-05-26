using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Iskra.Core;

/// <summary>
/// Thrown for any failure of the GitHub Device Flow auth path. <see cref="ErrorCode"/>
/// is populated for protocol-level errors GitHub returns explicitly
/// (<c>authorization_pending</c>, <c>slow_down</c>, <c>expired_token</c>,
/// <c>access_denied</c>, ...) — see GitHub's "OAuth Device Flow" docs.
/// </summary>
public sealed class GitHubAuthException : Exception
{
    public string? ErrorCode { get; }
    public GitHubAuthException(string message, string? errorCode = null, Exception? inner = null)
        : base(message, inner) { ErrorCode = errorCode; }
}

/// <summary>
/// Response from POST /login/device/code. Field names match the GitHub
/// snake_case wire format via the case-insensitive deserializer.
/// </summary>
public sealed record DeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int Interval);

/// <summary>
/// Successful response from POST /login/oauth/access_token (both the initial
/// device-code exchange and refresh-token grants). For GitHub Apps:
/// access tokens last 8h, refresh tokens last 6 months, and refresh tokens
/// rotate on every refresh — <see cref="TokenStore"/> (chunk 3) must always
/// overwrite, never append.
/// </summary>
public sealed record TokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string RefreshToken,
    int RefreshTokenExpiresIn,
    string? Scope);

/// <summary>
/// GitHub OAuth Device Flow client. Pure protocol, no token persistence —
/// see <see cref="TokenStore"/> (chunk 3) for that. HttpClient is injected
/// so tests can stub responses with a custom <see cref="HttpMessageHandler"/>;
/// the delay function is injected so the poll loop can run instantly in tests.
/// </summary>
public sealed class GitHubDeviceFlow
{
    public const string DeviceCodeEndpoint = "https://github.com/login/device/code";
    public const string TokenEndpoint      = "https://github.com/login/oauth/access_token";
    public const string DeviceGrantType    = "urn:ietf:params:oauth:grant-type:device_code";
    public const string RefreshGrantType   = "refresh_token";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GitHubDeviceFlow(
        HttpClient http,
        string clientId,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (http is null) throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("clientId required", nameof(clientId));
        _http = http;
        _clientId = clientId;
        _delay = delay ?? Task.Delay;
    }

    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken ct = default)
    {
        var body = await PostFormAsync(DeviceCodeEndpoint, ct,
            ("client_id", _clientId));
        var parsed = DeserializeOrThrow<DeviceCodeResponse>(body, "device code");
        ValidateDeviceCode(parsed);
        return parsed;
    }

    /// <summary>
    /// Polls <c>/login/oauth/access_token</c> at the cadence GitHub specified
    /// in the device-code response. Honors <c>slow_down</c> by bumping the
    /// interval. Throws <see cref="GitHubAuthException"/> on
    /// <c>expired_token</c>, <c>access_denied</c>, or any other terminal
    /// error; loops on <c>authorization_pending</c>.
    /// </summary>
    public async Task<TokenResponse> PollForTokenAsync(
        DeviceCodeResponse code,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresIn);
        var interval = TimeSpan.FromSeconds(code.Interval);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow >= deadline)
                throw new GitHubAuthException(
                    "device code expired before the user authorised the app",
                    "expired_token");

            await _delay(interval, ct).ConfigureAwait(false);

            var body = await PostFormAsync(TokenEndpoint, ct,
                ("client_id",   _clientId),
                ("device_code", code.DeviceCode),
                ("grant_type",  DeviceGrantType));

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errEl))
            {
                var err = errEl.GetString() ?? "unknown_error";
                switch (err)
                {
                    case "authorization_pending":
                        continue;
                    case "slow_down":
                        interval = root.TryGetProperty("interval", out var iEl)
                            && iEl.TryGetInt32(out var newInt)
                                ? TimeSpan.FromSeconds(newInt)
                                : interval + TimeSpan.FromSeconds(5);
                        continue;
                    case "expired_token":
                        throw new GitHubAuthException("device code expired", "expired_token");
                    case "access_denied":
                        throw new GitHubAuthException("user denied authorisation", "access_denied");
                    default:
                        var desc = root.TryGetProperty("error_description", out var dEl)
                            ? dEl.GetString() : null;
                        throw new GitHubAuthException(
                            desc is null ? $"token poll failed: {err}" : $"token poll failed: {err} — {desc}",
                            err);
                }
            }

            var token = DeserializeOrThrow<TokenResponse>(body, "token");
            ValidateToken(token);
            return token;
        }
    }

    public async Task<TokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new ArgumentException("refreshToken required", nameof(refreshToken));

        var body = await PostFormAsync(TokenEndpoint, ct,
            ("client_id",     _clientId),
            ("grant_type",    RefreshGrantType),
            ("refresh_token", refreshToken));

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var errEl))
        {
            var err  = errEl.GetString() ?? "unknown_error";
            var desc = doc.RootElement.TryGetProperty("error_description", out var dEl)
                ? dEl.GetString() : null;
            throw new GitHubAuthException(
                desc is null ? $"token refresh failed: {err}" : $"token refresh failed: {err} — {desc}",
                err);
        }

        var token = DeserializeOrThrow<TokenResponse>(body, "token");
        ValidateToken(token);
        return token;
    }

    private async Task<string> PostFormAsync(
        string endpoint, CancellationToken ct, params (string Key, string Value)[] fields)
    {
        var form = new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new GitHubAuthException(
                $"{endpoint} returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
        return body;
    }

    private static T DeserializeOrThrow<T>(string body, string what)
    {
        try
        {
            var v = JsonSerializer.Deserialize<T>(body, JsonOpts);
            if (v is null) throw new GitHubAuthException($"{what} response was null");
            return v;
        }
        catch (JsonException ex)
        {
            throw new GitHubAuthException(
                $"{what} response was not valid JSON: {ex.Message}", inner: ex);
        }
    }

    private static void ValidateDeviceCode(DeviceCodeResponse r)
    {
        if (string.IsNullOrEmpty(r.DeviceCode))
            throw new GitHubAuthException("device code response: device_code missing");
        if (string.IsNullOrEmpty(r.UserCode))
            throw new GitHubAuthException("device code response: user_code missing");
        if (string.IsNullOrEmpty(r.VerificationUri))
            throw new GitHubAuthException("device code response: verification_uri missing");
        if (r.ExpiresIn <= 0)
            throw new GitHubAuthException("device code response: expires_in must be > 0");
        if (r.Interval <= 0)
            throw new GitHubAuthException("device code response: interval must be > 0");
    }

    private static void ValidateToken(TokenResponse t)
    {
        if (string.IsNullOrEmpty(t.AccessToken))
            throw new GitHubAuthException("token response: access_token missing");
        if (string.IsNullOrEmpty(t.RefreshToken))
            throw new GitHubAuthException("token response: refresh_token missing (GitHub App required)");
        if (t.ExpiresIn <= 0)
            throw new GitHubAuthException("token response: expires_in must be > 0");
    }
}
