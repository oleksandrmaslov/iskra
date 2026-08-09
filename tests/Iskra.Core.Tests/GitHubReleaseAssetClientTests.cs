using System.Net;
using System.Net.Http;
using System.Text;
using Iskra.Core;

namespace Iskra.Core.Tests;

public class GitHubReleaseAssetClientTests
{
    private const string ReleaseJsonWithAsset = """
        {
          "tag_name": "v1.0.0",
          "assets": [
            {
              "name": "other-file.elf",
              "url":  "https://api.github.com/repos/o/r/releases/assets/111"
            },
            {
              "name": "ci-clop_v1.0.0_PY32F002Ax5.elf",
              "url":  "https://api.github.com/repos/o/r/releases/assets/222"
            }
          ]
        }
        """;

    private const string ReleaseJsonEmpty = """
        { "tag_name": "v1.0.0", "assets": [] }
        """;

    private static GitHubReleaseAssetClient NewClient(StubHandler h) =>
        new(new HttpClient(h));

    [Fact]
    public async Task GetAssetDownloadUrl_finds_matching_asset_by_name()
    {
        var h = new StubHandler(JsonResp(ReleaseJsonWithAsset));
        var url = await NewClient(h).GetAssetDownloadUrlAsync(
            "o/r", "v1.0.0", "ci-clop_v1.0.0_PY32F002Ax5.elf", "tok");

        Assert.Equal("https://api.github.com/repos/o/r/releases/assets/222", url);

        var req = h.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Contains("/repos/o/r/releases/tags/v1.0.0", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("tok",    req.Headers.Authorization.Parameter);
        Assert.Contains(req.Headers.Accept, h => h.MediaType == "application/vnd.github+json");
        Assert.True(req.Headers.UserAgent.Count > 0);
        Assert.Contains("X-GitHub-Api-Version", req.Headers.Select(kv => kv.Key));
    }

    [Fact]
    public async Task GetAssetDownloadUrl_throws_AssetNotFound_when_asset_missing()
    {
        var h = new StubHandler(JsonResp(ReleaseJsonEmpty));
        var ex = await Assert.ThrowsAsync<GitHubAssetNotFoundException>(() =>
            NewClient(h).GetAssetDownloadUrlAsync("o/r", "v1.0.0", "missing.elf", "tok"));
        Assert.Equal("missing.elf", ex.Asset);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.Forbidden, 403)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    public async Task GetAssetDownloadUrl_reports_an_access_problem_not_a_transport_failure(
        HttpStatusCode status, int expected)
    {
        // GitHub answers 404 rather than 403 for a private repo the caller
        // cannot see, so all three mean "this account is not approved" far more
        // often than they mean "the network is broken". Reporting them as a
        // generic API/download failure sends the operator to check the network.
        var h = new StubHandler(JsonResp("{\"message\":\"Not Found\"}", status));

        var ex = await Assert.ThrowsAsync<GitHubRepoAccessDeniedException>(() =>
            NewClient(h).GetAssetDownloadUrlAsync("o/r", "v9.9.9", "x.elf", "tok"));

        Assert.Equal(expected, ex.StatusCode);
        Assert.Equal("o/r", ex.Repo);
        Assert.Equal("v9.9.9", ex.Tag);
    }

    [Fact]
    public async Task GetAssetDownloadUrl_still_reports_a_real_server_error_as_an_api_error()
    {
        // A 5xx is genuinely not an approval problem and must stay distinct.
        var h = new StubHandler(JsonResp("{\"message\":\"boom\"}", HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<GitHubApiException>(() =>
            NewClient(h).GetAssetDownloadUrlAsync("o/r", "v9.9.9", "x.elf", "tok"));

        Assert.Equal(500, ex.StatusCode);
    }

    [Fact]
    public async Task DownloadAsset_streams_body_to_destination_with_octet_stream_accept()
    {
        var bytes = Encoding.UTF8.GetBytes("ELF-stub-bytes");
        var h = new StubHandler(BinaryResp(bytes));
        var dest = new MemoryStream();

        await NewClient(h).DownloadAssetAsync(
            "https://api.github.com/repos/o/r/releases/assets/222", "tok", dest);

        Assert.Equal(bytes, dest.ToArray());
        var req = h.Requests.Single();
        Assert.Contains(req.Headers.Accept, h => h.MediaType == "application/octet-stream");
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task DownloadAsset_throws_on_non_2xx()
    {
        var h = new StubHandler(JsonResp("{\"message\":\"Bad token\"}", HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<GitHubApiException>(() =>
            NewClient(h).DownloadAssetAsync("https://api/x", "tok", new MemoryStream()));
    }

    [Fact]
    public async Task Rejects_empty_inputs()
    {
        var client = NewClient(new StubHandler());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetAssetDownloadUrlAsync("", "v1.0.0", "x.elf", "tok"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetAssetDownloadUrlAsync("o/r", "", "x.elf", "tok"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetAssetDownloadUrlAsync("o/r", "v1.0.0", "", "tok"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.GetAssetDownloadUrlAsync("o/r", "v1.0.0", "x.elf", ""));
    }

    // --- helpers ---------------------------------------------------------

    private static HttpResponseMessage JsonResp(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage BinaryResp(byte[] body)
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        resp.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        return resp;
    }

    public sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(params HttpResponseMessage[] responses)
            => _responses = new Queue<HttpResponseMessage>(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Capture a snapshot of headers/uri before SendAsync disposes things.
            var snap = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) snap.Headers.TryAddWithoutValidation(h.Key, h.Value);
            Requests.Add(snap);

            if (_responses.Count == 0)
                throw new InvalidOperationException("StubHandler ran out of canned responses");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
