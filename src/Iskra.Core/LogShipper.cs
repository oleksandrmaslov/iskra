using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Iskra.Core;

/// <summary>
/// Thrown when the cloud-mirror push to <c>iskra-logs</c> fails partway.
/// Callers should leave the matching SQLite rows unsynced so the next push
/// retries naturally.
/// </summary>
public sealed class LogShipperException : Exception
{
    public int? StatusCode { get; }
    public LogShipperException(string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner) { StatusCode = statusCode; }
}

/// <summary>Summary returned from a single ship pass.</summary>
public sealed record ShipReport(
    int RowsPushed,
    int FilesCreated,
    int FilesUpdated,
    int RowsLeftover);

/// <summary>
/// Drains <see cref="SqliteLogStore.GetUnsynced"/>, groups by
/// <c>(station_id, UTC date)</c>, and append-writes each group as a JSONL
/// chunk to the path <c>stations/&lt;sanitized-station&gt;/&lt;YYYY-MM-DD&gt;.jsonl</c>
/// inside the configured GitHub repo via the Contents API.
///
/// <para>
/// One file per station per UTC day is deliberate: stations don't share
/// file paths, so two stations pushing concurrently can never race on the
/// same blob SHA. Within one station, ShipPendingAsync is serial.
/// </para>
/// </summary>
public sealed class LogShipper
{
    public const int MaxContentsResponseBytes = 32 * 1024 * 1024;
    public const int MaxErrorResponseBytes = 64 * 1024;
    public const string ApiBaseUrl = "https://api.github.com";
    public const string ApiAccept  = "application/vnd.github+json";
    public const string ApiVersion = "2022-11-28";

    private readonly SqliteLogStore _store;
    private readonly GitHubAppInstallationTokenProvider _tokens;
    private readonly HttpClient _http;
    private readonly string _owner, _repo, _userAgent;
    private readonly int _batchSize;
    private readonly Func<DateTime> _utcNow;

    public LogShipper(
        SqliteLogStore store,
        GitHubAppInstallationTokenProvider tokens,
        HttpClient http,
        string owner,
        string repo,
        string userAgent = "Iskra",
        int batchSize = 500,
        Func<DateTime>? utcNow = null)
    {
        if (store  is null) throw new ArgumentNullException(nameof(store));
        if (tokens is null) throw new ArgumentNullException(nameof(tokens));
        if (http   is null) throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))  throw new ArgumentException("repo required",  nameof(repo));
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

        _store = store;
        _tokens = tokens;
        _http = http;
        _owner = owner;
        _repo = repo;
        _userAgent = userAgent;
        _batchSize = batchSize;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Pushes up to <c>batchSize</c> unsynced rows and returns a report. Each
    /// (station, date) group is its own GitHub commit; a failure on one group
    /// leaves earlier groups' rows already-marked-synced (their commits already
    /// landed) and propagates as <see cref="LogShipperException"/>.
    /// </summary>
    public async Task<ShipReport> ShipPendingAsync(CancellationToken ct = default)
    {
        var rows = _store.GetUnsynced(_batchSize);
        if (rows.Count == 0)
            return new ShipReport(0, 0, 0, 0);

        int leftover = Math.Max(0, _store.CountUnsynced() - rows.Count);
        var token = await _tokens.GetInstallationTokenAsync(ct).ConfigureAwait(false);

        int pushed = 0, created = 0, updated = 0;
        var groups = rows
            .GroupBy(r => (r.Record.StationId, Date: r.Record.TsUtc.ToUniversalTime().Date))
            .OrderBy(g => g.Key.StationId).ThenBy(g => g.Key.Date);

        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            var stationId = g.Key.StationId;
            var date      = g.Key.Date;
            var groupList = g.OrderBy(r => r.Id).ToList();
            var path = $"stations/{SanitizePathSegment(stationId)}/{date:yyyy-MM-dd}.jsonl";

            var (existingSha, existingContent) = await GetExistingAsync(path, token, ct).ConfigureAwait(false);
            var newContent = existingContent + FlashAttemptJsonl.SerializeBatch(groupList);
            var message    = $"Iskra log: {stationId} {date:yyyy-MM-dd} (+{groupList.Count})";

            await PutAsync(path, existingSha, newContent, message, token, ct).ConfigureAwait(false);
            _store.MarkSynced(groupList.Select(r => r.Id), _utcNow());

            pushed += groupList.Count;
            if (existingSha is null) created++; else updated++;
        }

        return new ShipReport(pushed, created, updated, leftover);
    }

    private async Task<(string? Sha, string Content)> GetExistingAsync(
        string path, string token, CancellationToken ct)
    {
        var url = BuildContentsUrl(path);
        using var req = NewApiRequest(HttpMethod.Get, url, token);
        using var resp = await _http.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            return (null, "");

        var body = await BoundedHttpContent.ReadUtf8StringAsync(
            resp.Content,
            resp.IsSuccessStatusCode ? MaxContentsResponseBytes : MaxErrorResponseBytes,
            ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new LogShipperException(
                $"GET {url} -> {(int)resp.StatusCode} {resp.ReasonPhrase}: {Snip(body)}",
                (int)resp.StatusCode);

        using var doc = JsonDocument.Parse(body);
        var sha = doc.RootElement.TryGetProperty("sha", out var shaEl) && shaEl.ValueKind == JsonValueKind.String
            ? shaEl.GetString()
            : throw new LogShipperException("GET contents response missing 'sha'");
        var encoded = doc.RootElement.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String
            ? cEl.GetString()!
            : throw new LogShipperException("GET contents response missing 'content'");

        // GitHub returns base64 with embedded \n line breaks every 60 chars.
        var content = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Replace("\n", "")));
        return (sha, content);
    }

    private async Task PutAsync(
        string path, string? sha, string content, string commitMessage,
        string token, CancellationToken ct)
    {
        var url = BuildContentsUrl(path);
        var payload = new Dictionary<string, object>
        {
            ["message"] = commitMessage,
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
        };
        if (sha is not null) payload["sha"] = sha;
        var json = JsonSerializer.Serialize(payload);

        using var req = NewApiRequest(HttpMethod.Put, url, token);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await BoundedHttpContent.ReadUtf8StringAsync(
                resp.Content, MaxErrorResponseBytes, ct).ConfigureAwait(false);
            throw new LogShipperException(
                $"PUT {url} -> {(int)resp.StatusCode} {resp.ReasonPhrase}: {Snip(body)}",
                (int)resp.StatusCode);
        }
    }

    private string BuildContentsUrl(string path)
        => $"{ApiBaseUrl}/repos/{Uri.EscapeDataString(_owner)}/{Uri.EscapeDataString(_repo)}/contents/{path}";

    private HttpRequestMessage NewApiRequest(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ApiAccept));
        req.Headers.UserAgent.ParseAdd(_userAgent);
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        return req;
    }

    // Preserve ordinary station IDs for readable paths. Any value that needs
    // normalization (including '.', '..', or an overlong value) receives a
    // SHA-256 suffix. This keeps the result traversal-safe and prevents two
    // distinct station IDs such as "a/b" and "a_b" from sharing a log blob.
    public static string SanitizePathSegment(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        const int maxPlainLength = 80;
        var alreadySafe = s.Length is > 0 and <= maxPlainLength
            && s is not "." and not ".."
            && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
        if (alreadySafe) return s;

        var sb = new StringBuilder(Math.Min(s.Length, 48));
        foreach (var c in s)
        {
            if (sb.Length == 48) break;
            if (char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '-')
                sb.Append(c);
            else
                sb.Append('_');
        }
        var prefix = sb.ToString().Trim('.');
        if (string.IsNullOrWhiteSpace(prefix)) prefix = "station";
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))
            .ToLowerInvariant();
        return $"{prefix}~{digest}";
    }

    private static string Snip(string s) => s.Length <= 200 ? s : s[..200] + "...";
}
