using System.Net;
using Iskra.Core;

namespace Iskra.Core.Tests;

public class AppUpdateClientTests
{
    [Theory]
    [InlineData("v1.2.8", 1, 2, 8)]
    [InlineData("1.2.8+build.5", 1, 2, 8)]
    [InlineData("Iskra-1.2.8-setup-x64.exe", 1, 2, 8)]
    public void TryParseVersion_accepts_release_tag_shapes(
        string text,
        int major,
        int minor,
        int patch)
    {
        Assert.True(AppUpdateClient.TryParseVersion(text, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Build);
    }

    [Fact]
    public async Task CheckLatestAsync_reports_update_available_and_asset_urls()
    {
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.OK, """
            {
              "tag_name": "v1.2.9",
              "html_url": "https://github.example/iskra/releases/tag/v1.2.9",
              "published_at": "2026-06-01T12:00:00Z",
              "assets": [
                {
                  "name": "Iskra-1.2.9-setup-x64.exe",
                  "browser_download_url": "https://github.example/download/setup.exe"
                },
                {
                  "name": "Iskra-1.2.9-x64.msi",
                  "browser_download_url": "https://github.example/download/app.msi"
                }
              ]
            }
            """));

        var result = await NewClient(http).CheckLatestAsync("1.2.8");

        Assert.Equal(AppUpdateStatus.UpdateAvailable, result.Status);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.2.9.0", result.LatestVersion);
        Assert.Equal("v1.2.9", result.TagName);
        Assert.Equal("https://github.example/iskra/releases/tag/v1.2.9", result.ReleaseUrl);
        Assert.Equal("https://github.example/download/setup.exe", result.SetupDownloadUrl);
        Assert.Equal("https://github.example/download/app.msi", result.MsiDownloadUrl);
        Assert.Equal(new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), result.PublishedAtUtc);
    }

    [Fact]
    public async Task CheckLatestAsync_reports_up_to_date_for_same_version()
    {
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.OK, """
            { "tag_name": "v1.2.8", "html_url": "https://github.example/release", "assets": [] }
            """));

        var result = await NewClient(http).CheckLatestAsync("1.2.8");

        Assert.Equal(AppUpdateStatus.UpToDate, result.Status);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckLatestAsync_maps_404_to_no_release()
    {
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.NotFound, "{}"));

        var result = await NewClient(http).CheckLatestAsync("1.2.8");

        Assert.Equal(AppUpdateStatus.NoRelease, result.Status);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckLatestAsync_rejects_release_without_parseable_version_tag()
    {
        using var http = new HttpClient(new StaticHandler(HttpStatusCode.OK, """
            { "tag_name": "latest", "html_url": "https://github.example/release", "assets": [] }
            """));

        var result = await NewClient(http).CheckLatestAsync("1.2.8");

        Assert.Equal(AppUpdateStatus.ParseError, result.Status);
        Assert.Contains("does not contain a version", result.Message);
    }

    [Fact]
    public async Task CheckLatestAsync_sends_github_headers()
    {
        var handler = new StaticHandler(HttpStatusCode.OK, """
            { "tag_name": "v1.2.8", "html_url": "https://github.example/release", "assets": [] }
            """);
        using var http = new HttpClient(handler);

        await NewClient(http).CheckLatestAsync("1.2.8");

        var req = handler.LastRequest!;
        Assert.Contains("application/vnd.github+json",
            req.Headers.Accept.Select(h => h.MediaType));
        Assert.Contains("X-GitHub-Api-Version",
            req.Headers.Select(h => h.Key));
        Assert.Contains("/repos/o/r/releases/latest", req.RequestUri!.ToString());
    }

    private static AppUpdateClient NewClient(HttpClient http) => new(http, "o", "r");

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StaticHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }
}
