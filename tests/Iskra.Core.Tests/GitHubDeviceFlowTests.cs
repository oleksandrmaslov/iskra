using System.Net;
using System.Net.Http;
using System.Text;
using Iskra.Core;

namespace Iskra.Core.Tests;

public class GitHubDeviceFlowTests
{
    private const string DeviceCodeOk = """
        {
          "device_code":      "dev-123",
          "user_code":        "WDJB-MJHT",
          "verification_uri": "https://github.com/login/device",
          "expires_in":       900,
          "interval":         5
        }
        """;

    private const string TokenOk = """
        {
          "access_token":              "gho_AAAA",
          "token_type":                "bearer",
          "expires_in":                28800,
          "refresh_token":             "ghr_BBBB",
          "refresh_token_expires_in":  15897600,
          "scope":                     ""
        }
        """;

    private static GitHubDeviceFlow Flow(StubHandler handler, string clientId = "Iv23liTEST") =>
        new(new HttpClient(handler), clientId, delay: (_, _) => Task.CompletedTask);

    [Fact]
    public async Task RequestDeviceCode_returns_parsed_response_and_posts_client_id()
    {
        var h = new StubHandler(JsonResp(DeviceCodeOk));
        var flow = Flow(h);

        var resp = await flow.RequestDeviceCodeAsync();

        Assert.Equal("dev-123", resp.DeviceCode);
        Assert.Equal("WDJB-MJHT", resp.UserCode);
        Assert.Equal("https://github.com/login/device", resp.VerificationUri);
        Assert.Equal(900, resp.ExpiresIn);
        Assert.Equal(5, resp.Interval);

        var sent = await h.Requests.Single().Content!.ReadAsStringAsync();
        Assert.Contains("client_id=Iv23liTEST", sent);
    }

    [Fact]
    public async Task RequestDeviceCode_throws_on_non_2xx()
    {
        var h = new StubHandler(JsonResp("{\"error\":\"server_error\"}", HttpStatusCode.InternalServerError));
        var flow = Flow(h);

        var ex = await Assert.ThrowsAsync<GitHubAuthException>(() => flow.RequestDeviceCodeAsync());
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task RequestDeviceCode_throws_on_missing_field()
    {
        var bad = DeviceCodeOk.Replace("\"device_code\":      \"dev-123\",", "\"device_code\": \"\",");
        var h = new StubHandler(JsonResp(bad));
        var flow = Flow(h);

        var ex = await Assert.ThrowsAsync<GitHubAuthException>(() => flow.RequestDeviceCodeAsync());
        Assert.Contains("device_code missing", ex.Message);
    }

    [Fact]
    public async Task PollForToken_returns_token_on_success()
    {
        var h = new StubHandler(JsonResp(TokenOk));
        var flow = Flow(h);
        var code = SampleDeviceCode();

        var token = await flow.PollForTokenAsync(code);

        Assert.Equal("gho_AAAA", token.AccessToken);
        Assert.Equal("ghr_BBBB", token.RefreshToken);
        Assert.Equal(28800, token.ExpiresIn);
        Assert.Equal(15897600, token.RefreshTokenExpiresIn);

        var sent = await h.Requests.Single().Content!.ReadAsStringAsync();
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", sent);
        Assert.Contains("device_code=dev-123", sent);
    }

    [Fact]
    public async Task PollForToken_retries_on_authorization_pending()
    {
        var h = new StubHandler(
            JsonResp("{\"error\":\"authorization_pending\"}"),
            JsonResp("{\"error\":\"authorization_pending\"}"),
            JsonResp(TokenOk));
        var flow = Flow(h);

        var token = await flow.PollForTokenAsync(SampleDeviceCode());

        Assert.Equal("gho_AAAA", token.AccessToken);
        Assert.Equal(3, h.Requests.Count);
    }

    [Fact]
    public async Task PollForToken_bumps_interval_on_slow_down()
    {
        var delays = new List<TimeSpan>();
        var http = new HttpClient(new StubHandler(
            JsonResp("{\"error\":\"slow_down\",\"interval\":10}"),
            JsonResp(TokenOk)));
        var flow = new GitHubDeviceFlow(http, "Iv23liTEST",
            delay: (t, _) => { delays.Add(t); return Task.CompletedTask; });

        await flow.PollForTokenAsync(SampleDeviceCode());

        Assert.Equal(2, delays.Count);
        Assert.Equal(TimeSpan.FromSeconds(5),  delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), delays[1]);
    }

    [Fact]
    public async Task PollForToken_slow_down_without_interval_falls_back_to_plus_5()
    {
        var delays = new List<TimeSpan>();
        var http = new HttpClient(new StubHandler(
            JsonResp("{\"error\":\"slow_down\"}"),
            JsonResp(TokenOk)));
        var flow = new GitHubDeviceFlow(http, "Iv23liTEST",
            delay: (t, _) => { delays.Add(t); return Task.CompletedTask; });

        await flow.PollForTokenAsync(SampleDeviceCode());

        Assert.Equal(TimeSpan.FromSeconds(5),  delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(10), delays[1]);
    }

    [Fact]
    public async Task PollForToken_expired_token_throws_with_code()
    {
        var h = new StubHandler(JsonResp("{\"error\":\"expired_token\"}"));
        var ex = await Assert.ThrowsAsync<GitHubAuthException>(
            () => Flow(h).PollForTokenAsync(SampleDeviceCode()));
        Assert.Equal("expired_token", ex.ErrorCode);
    }

    [Fact]
    public async Task PollForToken_access_denied_throws_with_code()
    {
        var h = new StubHandler(JsonResp("{\"error\":\"access_denied\"}"));
        var ex = await Assert.ThrowsAsync<GitHubAuthException>(
            () => Flow(h).PollForTokenAsync(SampleDeviceCode()));
        Assert.Equal("access_denied", ex.ErrorCode);
    }

    [Fact]
    public async Task PollForToken_unknown_error_includes_description()
    {
        var h = new StubHandler(
            JsonResp("{\"error\":\"unsupported_grant_type\",\"error_description\":\"bad grant\"}"));
        var ex = await Assert.ThrowsAsync<GitHubAuthException>(
            () => Flow(h).PollForTokenAsync(SampleDeviceCode()));
        Assert.Equal("unsupported_grant_type", ex.ErrorCode);
        Assert.Contains("bad grant", ex.Message);
    }

    [Fact]
    public async Task PollForToken_local_deadline_throws_expired_token_after_window()
    {
        // ExpiresIn = 0 makes the loop fail the deadline check before the first poll.
        var code = SampleDeviceCode() with { ExpiresIn = 0 };
        var h = new StubHandler(); // never hit
        var ex = await Assert.ThrowsAsync<GitHubAuthException>(
            () => Flow(h).PollForTokenAsync(code));
        Assert.Equal("expired_token", ex.ErrorCode);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task PollForToken_honours_cancellation()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var h = new StubHandler(JsonResp(TokenOk));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => Flow(h).PollForTokenAsync(SampleDeviceCode(), cts.Token));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task RefreshToken_returns_new_token_pair()
    {
        var h = new StubHandler(JsonResp(TokenOk));
        var token = await Flow(h).RefreshTokenAsync("ghr_OLD");

        Assert.Equal("gho_AAAA", token.AccessToken);
        Assert.Equal("ghr_BBBB", token.RefreshToken);

        var sent = await h.Requests.Single().Content!.ReadAsStringAsync();
        Assert.Contains("grant_type=refresh_token", sent);
        Assert.Contains("refresh_token=ghr_OLD", sent);
    }

    [Fact]
    public async Task RefreshToken_propagates_protocol_error()
    {
        var h = new StubHandler(JsonResp("{\"error\":\"bad_refresh_token\"}"));
        var ex = await Assert.ThrowsAsync<GitHubAuthException>(
            () => Flow(h).RefreshTokenAsync("ghr_DEAD"));
        Assert.Equal("bad_refresh_token", ex.ErrorCode);
    }

    [Fact]
    public async Task RefreshToken_rejects_empty_input()
    {
        var h = new StubHandler();
        await Assert.ThrowsAsync<ArgumentException>(
            () => Flow(h).RefreshTokenAsync(""));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public void Constructor_rejects_empty_client_id()
    {
        Assert.Throws<ArgumentException>(
            () => new GitHubDeviceFlow(new HttpClient(new StubHandler()), ""));
    }

    [Fact]
    public void GitHubAppConfig_is_unconfigured_until_client_id_set()
    {
        Assert.False(GitHubAppConfig.IsConfigured);
    }

    // --- helpers ---------------------------------------------------------

    private static DeviceCodeResponse SampleDeviceCode() =>
        new("dev-123", "WDJB-MJHT", "https://github.com/login/device", 900, 5);

    private static HttpResponseMessage JsonResp(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Capture the body now — the framework disposes the request after SendAsync returns.
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                var copy = new HttpRequestMessage(request.Method, request.RequestUri)
                {
                    Content = new StringContent(body, Encoding.UTF8,
                        request.Content.Headers.ContentType?.MediaType ?? "application/x-www-form-urlencoded"),
                };
                Requests.Add(copy);
            }
            else
            {
                Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));
            }

            if (_responses.Count == 0)
                throw new InvalidOperationException("StubHandler ran out of canned responses");
            return _responses.Dequeue();
        }
    }
}
