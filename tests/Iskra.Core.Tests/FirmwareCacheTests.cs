using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Iskra.Core;

namespace Iskra.Core.Tests;

public class FirmwareCacheTests : IDisposable
{
    private readonly string _root;
    private static readonly GitHubReleaseRef Src =
        new("o/r", "v1.0.0", "ci-clop_v1.0.0_PY32F002Ax5.elf");

    public FirmwareCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"iskra-fwcache-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (FirmwareCache Cache, StubHandler Handler, List<int> TokenCalls) NewCache(
        params HttpResponseMessage[] responses)
    {
        var h = new StubHandler(responses);
        var api = new GitHubReleaseAssetClient(new HttpClient(h));
        var calls = new List<int>();
        var cache = new FirmwareCache(
            api,
            getAccessToken: _ => { calls.Add(1); return Task.FromResult("tok"); },
            rootOverride: _root);
        return (cache, h, calls);
    }

    private static (byte[] Bytes, string Sha256Hex) SyntheticElf(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash  = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return (bytes, hash);
    }

    private static string ReleaseJson(string assetName, string apiUrl) => $$"""
        {
          "tag_name": "v1.0.0",
          "assets": [
            { "name": "{{assetName}}", "url": "{{apiUrl}}" }
          ]
        }
        """;

    [Fact]
    public void PathFor_returns_root_owner_repo_tag_asset_layout()
    {
        var (cache, _, _) = NewCache();
        var p = cache.PathFor(Src);
        Assert.Equal(Path.Combine(_root, "o_r", "v1.0.0", Src.Asset), p);
    }

    [Fact]
    public void PathFor_canonicalizes_noncanonical_root_override()
    {
        var api = new GitHubReleaseAssetClient(new HttpClient(new StubHandler()));
        var cache = new FirmwareCache(
            api,
            _ => Task.FromResult("tok"),
            Path.Combine(_root, "unused", ".."));

        Assert.Equal(
            Path.Combine(Path.GetFullPath(_root), "o_r", Src.Tag, Src.Asset),
            cache.PathFor(Src));
    }

    [Theory]
    [InlineData("../r", "v1.0.0", "firmware.elf")]
    [InlineData("o\\r", "v1.0.0", "firmware.elf")]
    [InlineData("o/r/extra", "v1.0.0", "firmware.elf")]
    [InlineData("o/r", "..", "firmware.elf")]
    [InlineData("o/r", "release/v1.0.0", "firmware.elf")]
    [InlineData("o/r", "release\\v1.0.0", "firmware.elf")]
    [InlineData("o/r", ".. ", "firmware.elf")]
    [InlineData("o/r", "v1.0.0", "..")]
    [InlineData("o/r", "v1.0.0", "nested/firmware.elf")]
    [InlineData("o/r", "v1.0.0", "nested\\firmware.elf")]
    [InlineData("o/r", "v1.0.0", "firmware.elf.")]
    [InlineData("o/r", "v1.0.0", "C:\\outside.elf")]
    public void PathFor_rejects_unsafe_repo_tag_or_asset(
        string repo, string tag, string asset)
    {
        var (cache, _, _) = NewCache();

        Assert.Throws<ArgumentException>(() =>
            cache.PathFor(new GitHubReleaseRef(repo, tag, asset)));
    }

    [Fact]
    public async Task Traversal_asset_is_rejected_without_deleting_file_outside_root()
    {
        var (cache, h, calls) = NewCache();
        var outside = Path.Combine(
            Path.GetDirectoryName(_root)!, $"iskra-fwcache-marker-{Guid.NewGuid():N}.elf");

        // Make the intermediate directories exist so the old
        // root/o_r/tag/../../../marker path would resolve to this file.
        Directory.CreateDirectory(Path.Combine(_root, "o_r", Src.Tag));
        File.WriteAllText(outside, "must-survive");
        try
        {
            var traversal = Path.Combine("..", "..", "..", Path.GetFileName(outside));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                cache.GetOrDownloadAsync(
                    new GitHubReleaseRef(Src.Repo, Src.Tag, traversal),
                    new string('a', 64)));

            Assert.Equal("must-survive", File.ReadAllText(outside));
            Assert.Empty(h.Requests);
            Assert.Empty(calls);
        }
        finally
        {
            if (File.Exists(outside)) File.Delete(outside);
        }
    }

    [Fact]
    public async Task Cache_miss_downloads_then_verifies_then_returns_path()
    {
        var (bytes, sha) = SyntheticElf("hello-world-elf-payload");
        var (cache, h, calls) = NewCache(
            JsonResp(ReleaseJson(Src.Asset, "https://api/asset/1")),
            BinaryResp(bytes));

        var localPath = await cache.GetOrDownloadAsync(Src, sha);

        Assert.Equal(cache.PathFor(Src), localPath);
        Assert.Equal(bytes, File.ReadAllBytes(localPath));
        Assert.Equal(2, h.Requests.Count); // list + download
        Assert.Single(calls);              // one fresh-token request per call
    }

    [Fact]
    public async Task Cache_hit_with_matching_hash_skips_network()
    {
        var (bytes, sha) = SyntheticElf("cached-elf");
        var (cache, h, calls) = NewCache();
        var dest = cache.PathFor(Src);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, bytes);

        var localPath = await cache.GetOrDownloadAsync(Src, sha);

        Assert.Equal(dest, localPath);
        Assert.Empty(h.Requests);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Cache_hit_with_mismatched_hash_re_downloads()
    {
        var (newBytes, newSha) = SyntheticElf("new-version");
        var (cache, h, _) = NewCache(
            JsonResp(ReleaseJson(Src.Asset, "https://api/asset/1")),
            BinaryResp(newBytes));

        var dest = cache.PathFor(Src);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, Encoding.UTF8.GetBytes("STALE-CONTENT-DIFFERENT-HASH"));

        var localPath = await cache.GetOrDownloadAsync(Src, newSha);

        Assert.Equal(newBytes, File.ReadAllBytes(localPath));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Hash_mismatch_after_download_deletes_temp_and_throws()
    {
        var (bytes, _) = SyntheticElf("garbage");
        var wrongExpected = new string('0', 63) + "1";
        var (cache, _, _) = NewCache(
            JsonResp(ReleaseJson(Src.Asset, "https://api/asset/1")),
            BinaryResp(bytes));

        var ex = await Assert.ThrowsAsync<FirmwareCacheException>(() =>
            cache.GetOrDownloadAsync(Src, wrongExpected));
        Assert.Contains("did not match catalog", ex.Message);

        Assert.False(File.Exists(cache.PathFor(Src) + ".tmp"));
        Assert.False(File.Exists(cache.PathFor(Src)));
        AssertNoDownloadTemps();
    }

    [Fact]
    public async Task Failed_replacement_preserves_existing_mismatched_destination()
    {
        var staleBytes = Encoding.UTF8.GetBytes("stale-but-must-survive");
        var (downloadedBytes, _) = SyntheticElf("downloaded-with-wrong-hash");
        var (_, expectedSha) = SyntheticElf("the-expected-replacement");
        var (cache, _, _) = NewCache(
            JsonResp(ReleaseJson(Src.Asset, "https://api/asset/1")),
            BinaryResp(downloadedBytes));
        var dest = cache.PathFor(Src);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, staleBytes);

        await Assert.ThrowsAsync<FirmwareCacheException>(() =>
            cache.GetOrDownloadAsync(Src, expectedSha));

        Assert.Equal(staleBytes, File.ReadAllBytes(dest));
        AssertNoDownloadTemps();
    }

    [Fact]
    public async Task Download_does_not_use_or_remove_predictable_destination_temp_file()
    {
        var (bytes, sha) = SyntheticElf("verified-replacement");
        var (cache, _, _) = NewCache(
            JsonResp(ReleaseJson(Src.Asset, "https://api/asset/1")),
            BinaryResp(bytes));
        var dest = cache.PathFor(Src);
        var legacyTemp = dest + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllText(legacyTemp, "unrelated-sentinel");

        await cache.GetOrDownloadAsync(Src, sha);

        Assert.Equal("unrelated-sentinel", File.ReadAllText(legacyTemp));
        AssertNoDownloadTemps();
    }

    [Fact]
    public async Task Asset_not_in_release_propagates_AssetNotFound()
    {
        var (cache, _, _) = NewCache(
            JsonResp("""{ "tag_name": "v1.0.0", "assets": [] }"""));
        var sha = new string('a', 64);

        await Assert.ThrowsAsync<GitHubAssetNotFoundException>(() =>
            cache.GetOrDownloadAsync(Src, sha));
    }

    [Fact]
    public async Task Api_404_listing_release_wraps_as_FirmwareCacheException()
    {
        var (cache, _, _) = NewCache(
            JsonResp("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound));
        var sha = new string('a', 64);

        var ex = await Assert.ThrowsAsync<FirmwareCacheException>(() =>
            cache.GetOrDownloadAsync(Src, sha));
        Assert.Contains("could not list assets", ex.Message);
        Assert.IsType<GitHubApiException>(ex.InnerException);
    }

    [Fact]
    public async Task Invalid_expected_hash_throws_argument()
    {
        var (cache, _, _) = NewCache();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            cache.GetOrDownloadAsync(Src, "not-hex"));
    }

    [Fact]
    public async Task Cache_hit_recomputes_hash_every_call()
    {
        // Save a valid cache entry; then mutate the file on disk to simulate
        // tampering. Next call must detect the mismatch and re-download.
        var (origBytes, origSha) = SyntheticElf("original");
        var (newBytes, newSha)   = SyntheticElf("replacement");

        var (cache, h, _) = NewCache(
            JsonResp(ReleaseJson(Src.Asset, "https://api/asset/1")),
            BinaryResp(newBytes));
        var dest = cache.PathFor(Src);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, origBytes);

        // First call: cache hit, no network.
        var p1 = await cache.GetOrDownloadAsync(Src, origSha);
        Assert.Empty(h.Requests);

        // Now expected hash changes (catalog update). Cache is stale.
        var p2 = await cache.GetOrDownloadAsync(Src, newSha);
        Assert.Equal(newBytes, File.ReadAllBytes(p2));
        Assert.Equal(2, h.Requests.Count);
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

    private void AssertNoDownloadTemps()
    {
        var tempRoot = Path.Combine(_root, ".tmp");
        if (Directory.Exists(tempRoot))
            Assert.Empty(Directory.EnumerateFiles(tempRoot, "*", SearchOption.AllDirectories));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(params HttpResponseMessage[] r)
            => _responses = new Queue<HttpResponseMessage>(r);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new HttpRequestMessage(request.Method, request.RequestUri));
            if (_responses.Count == 0)
                throw new InvalidOperationException("ran out of responses");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
