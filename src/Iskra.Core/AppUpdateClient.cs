using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Iskra.Core;

public enum AppUpdateStatus
{
    UpdateAvailable,
    UpToDate,
    NoRelease,
    NetworkError,
    ParseError,
}

public sealed record AppUpdateCheckResult(
    AppUpdateStatus Status,
    string CurrentVersion,
    string? LatestVersion,
    string? TagName,
    bool IsUpdateAvailable,
    string? ReleaseUrl,
    string? SetupDownloadUrl,
    string? MsiDownloadUrl,
    DateTime? PublishedAtUtc,
    string? Message);

/// <summary>
/// Anonymous GitHub Releases checker for the Iskra app itself. It reports
/// whether a newer installer exists; installation remains a manual operator /
/// maintainer action outside the running app.
/// </summary>
public sealed class AppUpdateClient
{
    public const string ApiBaseUrl = "https://api.github.com";
    public const string ApiAccept  = "application/vnd.github+json";
    public const string ApiVersion = "2022-11-28";

    private static readonly Regex VersionPattern = new(
        @"\d+(?:\.\d+){0,3}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;

    public AppUpdateClient(HttpClient http, string owner, string repo)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))  throw new ArgumentException("repo required", nameof(repo));
        _owner = owner;
        _repo  = repo;
    }

    public async Task<AppUpdateCheckResult> CheckLatestAsync(
        string currentVersion,
        CancellationToken ct = default)
    {
        var currentDisplay = string.IsNullOrWhiteSpace(currentVersion)
            ? "0.0.0"
            : currentVersion.Trim();

        if (!TryParseVersion(currentDisplay, out var current))
            current = new Version(0, 0, 0);

        var url = $"{ApiBaseUrl}/repos/{_owner}/{_repo}/releases/latest";
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(NewApiRequest(HttpMethod.Get, url), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return Failure(AppUpdateStatus.NetworkError, currentDisplay, ex.Message);
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return Failure(AppUpdateStatus.NoRelease, currentDisplay,
                    $"{_owner}/{_repo} has no releases yet");

            if (!resp.IsSuccessStatusCode)
                return Failure(AppUpdateStatus.NetworkError, currentDisplay,
                    $"GET releases/latest -> {(int)resp.StatusCode} {resp.ReasonPhrase}");

            string body;
            try
            {
                body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Failure(AppUpdateStatus.NetworkError, currentDisplay, ex.Message);
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var tagName = root.TryGetProperty("tag_name", out var tagEl)
                    ? tagEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(tagName))
                    return Failure(AppUpdateStatus.ParseError, currentDisplay, "release has no tag_name");

                if (!TryParseVersion(tagName, out var latest))
                    return Failure(AppUpdateStatus.ParseError, currentDisplay,
                        $"release tag '{tagName}' does not contain a version");

                var htmlUrl = root.TryGetProperty("html_url", out var htmlEl)
                    ? htmlEl.GetString()
                    : null;

                DateTime? publishedAt = null;
                if (root.TryGetProperty("published_at", out var pubEl)
                    && pubEl.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(pubEl.GetString(), out var parsedPublished))
                {
                    publishedAt = parsedPublished.ToUniversalTime();
                }

                string? setupUrl = null;
                string? msiUrl = null;
                if (root.TryGetProperty("assets", out var assetsEl)
                    && assetsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsEl.EnumerateArray())
                    {
                        var name = asset.TryGetProperty("name", out var nameEl)
                            ? nameEl.GetString()
                            : null;
                        var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlEl)
                            ? urlEl.GetString()
                            : null;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                            continue;

                        if (name.EndsWith("-setup-x64.exe", StringComparison.OrdinalIgnoreCase))
                            setupUrl ??= downloadUrl;
                        else if (name.EndsWith("-x64.msi", StringComparison.OrdinalIgnoreCase))
                            msiUrl ??= downloadUrl;
                    }
                }

                var updateAvailable = CompareVersionParts(latest, current) > 0;
                return new AppUpdateCheckResult(
                    Status: updateAvailable ? AppUpdateStatus.UpdateAvailable : AppUpdateStatus.UpToDate,
                    CurrentVersion: currentDisplay,
                    LatestVersion: latest.ToString(),
                    TagName: tagName,
                    IsUpdateAvailable: updateAvailable,
                    ReleaseUrl: htmlUrl,
                    SetupDownloadUrl: setupUrl,
                    MsiDownloadUrl: msiUrl,
                    PublishedAtUtc: publishedAt,
                    Message: null);
            }
            catch (JsonException ex)
            {
                return Failure(AppUpdateStatus.ParseError, currentDisplay, ex.Message);
            }
        }
    }

    public static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var match = VersionPattern.Match(text);
        if (!match.Success) return false;

        var parts = match.Value.Split('.');
        var nums = new int[4];
        for (int i = 0; i < nums.Length; i++) nums[i] = 0;
        for (int i = 0; i < parts.Length && i < nums.Length; i++)
        {
            if (!int.TryParse(parts[i], out nums[i])) return false;
        }

        version = new Version(nums[0], nums[1], nums[2], nums[3]);
        return true;
    }

    private static int CompareVersionParts(Version a, Version b)
    {
        var ac = new[] { a.Major, a.Minor, Math.Max(0, a.Build), Math.Max(0, a.Revision) };
        var bc = new[] { b.Major, b.Minor, Math.Max(0, b.Build), Math.Max(0, b.Revision) };
        for (int i = 0; i < ac.Length; i++)
        {
            var cmp = ac[i].CompareTo(bc[i]);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    private HttpRequestMessage NewApiRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ApiAccept));
        req.Headers.UserAgent.ParseAdd("Iskra");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        return req;
    }

    private static AppUpdateCheckResult Failure(
        AppUpdateStatus status,
        string currentVersion,
        string message) => new(
            Status: status,
            CurrentVersion: currentVersion,
            LatestVersion: null,
            TagName: null,
            IsUpdateAvailable: false,
            ReleaseUrl: null,
            SetupDownloadUrl: null,
            MsiDownloadUrl: null,
            PublishedAtUtc: null,
            Message: message);
}
