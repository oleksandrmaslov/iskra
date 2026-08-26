using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iskra.Core;

namespace Iskra.Core.Tests;

public class LogShipperTests
{
    private const string Owner = "oleksandrmaslov";
    private const string Repo  = "iskra-logs";
    private const string AppId = "100";
    private const string InstallId = "200";

    private static FlashAttemptRecord Make(string station = "BENCH-1",
                                            string batch = "B-001",
                                            DateTime? ts = null,
                                            FlashResult result = FlashResult.Pass)
        => new(
            TsUtc:           ts ?? new DateTime(2026, 5, 25, 14, 30, 0, DateTimeKind.Utc),
            Operator:        "Iryna",
            StationId:       station,
            BatchId:         batch,
            ProductId:       "ci-clop",
            FirmwareVersion: "1.0.0",
            FirmwareSha256:  "abcdef",
            TargetBmpMatch:  "PY32Fxxx",
            TargetDetected:  "PY32Fxxx M0+",
            TargetFlashKb:   32,
            ComPort:         "COM30",
            ProbeSerial:     null,
            Power:           PowerMode.External,
            ConnectRst:      false,
            BmpFrequencyHz:  1_000_000,
            Result:          result,
            ErrorCode:       result == FlashResult.Fail ? "E_TIMEOUT" : null,
            ErrorMessage:    result == FlashResult.Fail ? "details" : null,
            DurationMs:      820,
            GdbTail:         "Section .text ... matched.");

    private static GitHubAppInstallationTokenProvider MakeTokens(HttpClient http)
        => new(http, AppId, InstallId, () => RSA.Create(2048));

    [Fact]
    public async Task Empty_store_returns_zero_report_and_makes_no_http_calls()
    {
        using var store = new SqliteLogStore(":memory:");
        var handler = new StubHandler();
        var sut = new LogShipper(store, MakeTokens(new HttpClient(handler)),
            new HttpClient(handler), Owner, Repo);

        var report = await sut.ShipPendingAsync();
        Assert.Equal(0, report.RowsPushed);
        Assert.Equal(0, report.FilesCreated);
        Assert.Equal(0, report.FilesUpdated);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task First_push_creates_file_when_get_returns_404()
    {
        using var store = new SqliteLogStore(":memory:");
        store.Append(Make());

        var tokenHandler = new StubHandler(TokenResponse("ghs_test"));
        var apiHandler = new StubHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found") },
            new HttpResponseMessage(HttpStatusCode.Created)  { Content = new StringContent("{}") });

        var sut = new LogShipper(store, MakeTokens(new HttpClient(tokenHandler)),
            new HttpClient(apiHandler), Owner, Repo);

        var report = await sut.ShipPendingAsync();
        Assert.Equal(1, report.RowsPushed);
        Assert.Equal(1, report.FilesCreated);
        Assert.Equal(0, report.FilesUpdated);
        Assert.Equal(0, store.CountUnsynced());

        Assert.Equal(2, apiHandler.Requests.Count);
        Assert.Equal(HttpMethod.Get, apiHandler.Requests[0].Method);
        Assert.Equal(HttpMethod.Put, apiHandler.Requests[1].Method);
        Assert.Contains("stations/BENCH-1/2026-05-25.jsonl", apiHandler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Existing_file_is_appended_via_sha_and_old_content_preserved()
    {
        using var store = new SqliteLogStore(":memory:");
        store.Append(Make());

        var existingJsonl = "{\"schema_version\":1,\"local_id\":999,\"ts_utc\":\"prior\"}\n";
        var getResp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
                {"sha":"abc123","content":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(existingJsonl))}}"}
                """)
        };
        var putHandler = new RecordingPutHandler(
            getResp,
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var sut = new LogShipper(store, MakeTokens(new HttpClient(new StubHandler(TokenResponse("ghs_test")))),
            new HttpClient(putHandler), Owner, Repo);

        var report = await sut.ShipPendingAsync();
        Assert.Equal(1, report.RowsPushed);
        Assert.Equal(1, report.FilesUpdated);
        Assert.Equal(0, report.FilesCreated);

        var putBody = putHandler.LastPutBody!;
        using var bodyDoc = JsonDocument.Parse(putBody);
        Assert.Equal("abc123", bodyDoc.RootElement.GetProperty("sha").GetString());
        var newContent = Encoding.UTF8.GetString(Convert.FromBase64String(
            bodyDoc.RootElement.GetProperty("content").GetString()!));
        Assert.StartsWith(existingJsonl, newContent);
        Assert.Contains("\"local_id\":1", newContent);
    }

    [Fact]
    public async Task Rows_are_grouped_by_station_and_utc_date()
    {
        using var store = new SqliteLogStore(":memory:");
        var d1 = new DateTime(2026, 5, 25, 14, 0, 0, DateTimeKind.Utc);
        var d2 = new DateTime(2026, 5, 26, 9,  0, 0, DateTimeKind.Utc);
        store.Append(Make("BENCH-1", ts: d1));
        store.Append(Make("BENCH-1", ts: d1));
        store.Append(Make("BENCH-1", ts: d2));
        store.Append(Make("BENCH-2", ts: d1));

        // 3 distinct (station, date) groups → 3 GET + 3 PUT = 6 calls
        var apiHandler = new StubHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") },
            new HttpResponseMessage(HttpStatusCode.Created)  { Content = new StringContent("{}") },
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") },
            new HttpResponseMessage(HttpStatusCode.Created)  { Content = new StringContent("{}") },
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") },
            new HttpResponseMessage(HttpStatusCode.Created)  { Content = new StringContent("{}") });

        var sut = new LogShipper(store, MakeTokens(new HttpClient(new StubHandler(TokenResponse("ghs_test")))),
            new HttpClient(apiHandler), Owner, Repo);

        var report = await sut.ShipPendingAsync();
        Assert.Equal(4, report.RowsPushed);
        Assert.Equal(3, report.FilesCreated);
        Assert.Equal(6, apiHandler.Requests.Count);
    }

    [Fact]
    public async Task Put_failure_throws_and_leaves_rows_unsynced()
    {
        using var store = new SqliteLogStore(":memory:");
        store.Append(Make());

        var apiHandler = new StubHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") },
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"denied\"}") });

        var sut = new LogShipper(store, MakeTokens(new HttpClient(new StubHandler(TokenResponse("ghs_test")))),
            new HttpClient(apiHandler), Owner, Repo);

        await Assert.ThrowsAsync<LogShipperException>(() => sut.ShipPendingAsync());
        Assert.Equal(1, store.CountUnsynced());
    }

    [Fact]
    public void Sanitize_keeps_plain_safe_ids_and_hashes_every_normalized_value()
    {
        Assert.Equal("BENCH-1",     LogShipper.SanitizePathSegment("BENCH-1"));
        Assert.Equal("a.b_c-d",     LogShipper.SanitizePathSegment("a.b_c-d"));

        var normalized = LogShipper.SanitizePathSegment("Station 42");
        Assert.StartsWith("Station_42~", normalized);
        Assert.Equal(75, normalized.Length);
        Assert.DoesNotContain('/', normalized);
        Assert.DoesNotContain('\\', normalized);

        Assert.NotEqual(
            LogShipper.SanitizePathSegment("a/b"),
            LogShipper.SanitizePathSegment("a_b"));
        Assert.NotEqual(".", LogShipper.SanitizePathSegment("."));
        Assert.NotEqual("..", LogShipper.SanitizePathSegment(".."));
        Assert.StartsWith("station~", LogShipper.SanitizePathSegment(""));
        Assert.NotEqual(
            LogShipper.SanitizePathSegment(""),
            LogShipper.SanitizePathSegment("   "));
    }

    private static HttpResponseMessage TokenResponse(string token)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"token":"{{token}}","expires_at":"2099-01-01T00:00:00Z"}""")
        };

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

    private sealed class RecordingPutHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public string? LastPutBody { get; private set; }
        public RecordingPutHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Put && request.Content is not null)
                LastPutBody = await request.Content.ReadAsStringAsync(ct);
            if (_responses.Count == 0)
                throw new InvalidOperationException("RecordingPutHandler out of canned responses");
            return _responses.Dequeue();
        }
    }
}
