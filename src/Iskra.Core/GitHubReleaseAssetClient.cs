using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Iskra.Core;

public sealed class GitHubAssetNotFoundException : Exception
{
    public string Repo { get; }
    public string Tag  { get; }
    public string Asset { get; }
    public GitHubAssetNotFoundException(string repo, string tag, string asset, string detail)
        : base($"{repo}@{tag} has no asset named '{asset}' ({detail})")
    {
        Repo = repo; Tag = tag; Asset = asset;
    }
}

public sealed class GitHubApiException : Exception
{
    public int StatusCode { get; }
    public GitHubApiException(int statusCode, string message) : base(message)
        => StatusCode = statusCode;
}

/// <summary>
/// The signed-in account cannot see this firmware repository.
///
/// <para>GitHub deliberately answers 404 rather than 403 for a private
/// repository the caller has no access to, so it does not leak whether the
/// repository exists. That makes "you are not approved for this firmware" and
/// "this release tag is gone" indistinguishable at the HTTP layer — but both
/// are an access/approval question for the maintainer, and neither is a
/// network fault. Without this distinction the operator is told to check the
/// network, which is the wrong thing to go and do.</para>
/// </summary>
public sealed class GitHubRepoAccessDeniedException : Exception
{
    public string Repo { get; }
    public string Tag { get; }
    public int StatusCode { get; }

    public GitHubRepoAccessDeniedException(string repo, string tag, int statusCode, string detail)
        : base($"no access to {repo} release '{tag}' (HTTP {statusCode}): {detail}")
    {
        Repo = repo;
        Tag = tag;
        StatusCode = statusCode;
    }
}

/// <summary>
/// Pure HTTP layer for fetching GitHub release assets. No caching, no token
/// management — caller passes a fresh access token per call.
/// </summary>
public sealed class GitHubReleaseAssetClient
{
    public const string ApiBaseUrl   = "https://api.github.com";
    public const string ApiAccept    = "application/vnd.github+json";
    public const string ApiVersion   = "2022-11-28";
    public const string AssetAccept  = "application/octet-stream";

    private readonly HttpClient _http;
    private readonly string _userAgent;

    public GitHubReleaseAssetClient(HttpClient http, string userAgent = "Iskra")
    {
        if (http is null) throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(userAgent))
            throw new ArgumentException("userAgent required", nameof(userAgent));
        _http = http;
        _userAgent = userAgent;
    }

    /// <summary>
    /// Resolves <c>(repo, tag, assetName)</c> → the asset's API download URL.
    /// Throws <see cref="GitHubAssetNotFoundException"/> if the release exists
    /// but the named asset isn't attached.
    /// </summary>
    public async Task<string> GetAssetDownloadUrlAsync(
        string repo, string tag, string assetName, string accessToken, CancellationToken ct = default)
    {
        ValidateInputs(repo, tag, assetName, accessToken);

        var url = $"{ApiBaseUrl}/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";
        using var req = NewApiRequest(HttpMethod.Get, url, accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
        {
            // 401/403 = token rejected or lacking the permission; 404 = the
            // repository is invisible to this account, which for a private repo
            // is what "not approved" looks like. All three are an approval
            // question, not a transport failure.
            var status = (int)resp.StatusCode;
            if (status is 401 or 403 or 404)
                throw new GitHubRepoAccessDeniedException(repo, tag, status, Snip(body));

            throw new GitHubApiException(status,
                $"GET releases/tags/{tag} → {status} {resp.ReasonPhrase}: {Snip(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("assets", out var assetsEl) ||
            assetsEl.ValueKind != JsonValueKind.Array)
            throw new GitHubAssetNotFoundException(repo, tag, assetName, "release has no assets array");

        foreach (var a in assetsEl.EnumerateArray())
        {
            if (!a.TryGetProperty("name", out var n)) continue;
            if (!string.Equals(n.GetString(), assetName, StringComparison.Ordinal)) continue;
            if (!a.TryGetProperty("url", out var u) || u.ValueKind != JsonValueKind.String)
                throw new GitHubAssetNotFoundException(repo, tag, assetName, "asset has no url field");
            return u.GetString()!;
        }
        throw new GitHubAssetNotFoundException(repo, tag, assetName, "no asset with that name");
    }

    /// <summary>
    /// Streams an asset's bytes to <paramref name="destination"/>. The
    /// caller is responsible for re-hashing and committing the bytes —
    /// we don't write to the final cache path until SHA-256 verifies.
    /// </summary>
    public async Task DownloadAssetAsync(
        string assetApiUrl, string accessToken, Stream destination, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(assetApiUrl))
            throw new ArgumentException("assetApiUrl required", nameof(assetApiUrl));
        if (destination is null) throw new ArgumentNullException(nameof(destination));
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("accessToken required", nameof(accessToken));

        using var req = new HttpRequestMessage(HttpMethod.Get, assetApiUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(AssetAccept));
        req.Headers.UserAgent.ParseAdd(_userAgent);
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new GitHubApiException((int)resp.StatusCode,
                $"GET {assetApiUrl} → {(int)resp.StatusCode} {resp.ReasonPhrase}: {Snip(body)}");
        }
        await resp.Content.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    private HttpRequestMessage NewApiRequest(HttpMethod method, string url, string accessToken)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ApiAccept));
        req.Headers.UserAgent.ParseAdd(_userAgent);
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        return req;
    }

    private static void ValidateInputs(string repo, string tag, string asset, string token)
    {
        if (string.IsNullOrWhiteSpace(repo))  throw new ArgumentException("repo required",  nameof(repo));
        if (string.IsNullOrWhiteSpace(tag))   throw new ArgumentException("tag required",   nameof(tag));
        if (string.IsNullOrWhiteSpace(asset)) throw new ArgumentException("asset required", nameof(asset));
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("accessToken required", nameof(token));
    }

    private static string Snip(string s) => s.Length <= 200 ? s : s[..200] + "...";
}
