using System.Net;
using System.Net.Http;
using System.Text;
using Iskra.Core;

namespace Iskra.Core.Tests;

public class RemoteCatalogClientTests : IDisposable
{
    private readonly string _cacheDir;
    private readonly CatalogSignature.Keypair _kp;

    public RemoteCatalogClientTests()
    {
        _cacheDir = Path.Combine(Path.GetTempPath(), $"iskra-remotecat-{Guid.NewGuid():N}");
        _kp = CatalogSignature.GenerateKeypair();
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir)) Directory.Delete(_cacheDir, recursive: true);
    }

    // Tests run against a stub HTTP handler with a fictitious owner; the
    // allowlist would refuse construction. The generated keypair keeps the
    // signature tests hermetic and exercises the same verification path.
    private RemoteCatalogClient NewClient(StubHandler h)
        => new(new HttpClient(h), owner: "o", repo: "iskra-catalog",
               cacheDirOverride: _cacheDir, enforceAllowlist: false,
               verificationPublicKey: _kp.PublicKey);

    private const string SampleCatalogJson = """
        {
          "schema_version": 1,
          "generated_at": "2026-05-26T12:00:00Z",
          "products": [
            {
              "product_id": "ci-clop",
              "display_name": "CI-CLOP",
              "target": { "bmp_match": "PY32Fxxx", "part_number": "PY32F002Ax5", "flash_kb": 32 },
              "releases": [
                {
                  "version": "1.0.0",
                  "elf_filename": "ci-clop_v1.0.0_PY32F002Ax5.elf",
                  "elf_sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "elf_url": null,
                  "released_at": "2026-05-26T12:00:00Z",
                  "notes": null
                }
              ],
              "default_release": "1.0.0"
            }
          ]
        }
        """;

    private static readonly byte[] SampleCatalogBytes = Encoding.UTF8.GetBytes(SampleCatalogJson);

    private static byte[] CatalogBytesAt(DateTime generatedAt)
    {
        var json = SampleCatalogJson.Replace(
            "\"generated_at\": \"2026-05-26T12:00:00Z\"",
            $"\"generated_at\": \"{generatedAt.ToUniversalTime():O}\"",
            StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(json);
    }

    private static string ReleaseJson(string tag = "catalog-20260526-090000") => $$"""
        {
          "tag_name": "{{tag}}",
          "assets": [
            { "name": "catalog.json",     "browser_download_url": "https://dl/catalog.json" },
            { "name": "catalog.json.sig", "browser_download_url": "https://dl/catalog.json.sig" }
          ]
        }
        """;

    [Fact]
    public async Task FetchAsync_returns_NoRelease_on_404()
    {
        var h = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"message\":\"Not Found\"}", Encoding.UTF8, "application/json"),
        });
        var r = await NewClient(h).FetchAsync();
        Assert.Equal(RemoteCatalogStatus.NoRelease, r.Status);
        Assert.Null(r.Catalog);
    }

    [Fact]
    public async Task FetchAsync_returns_NetworkError_on_5xx()
    {
        var h = new StubHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var r = await NewClient(h).FetchAsync();
        Assert.Equal(RemoteCatalogStatus.NetworkError, r.Status);
    }

    [Fact]
    public async Task FetchAsync_returns_AssetsMissing_when_release_lacks_catalog_files()
    {
        var releaseWithoutAssets = """{ "tag_name": "catalog-test", "assets": [] }""";
        var h = new StubHandler(JsonResp(releaseWithoutAssets));
        var r = await NewClient(h).FetchAsync();
        Assert.Equal(RemoteCatalogStatus.AssetsMissing, r.Status);
    }

    [Fact]
    public async Task FetchAsync_commits_valid_signed_catalog_and_rollback_floor()
    {
        var generatedAt = DateTime.UtcNow.AddHours(-1);
        var catalogBytes = CatalogBytesAt(generatedAt);
        const string tag = "catalog-valid-signed";
        var client = NewClient(SignedCatalogHandler(catalogBytes, tag));

        var r = await client.FetchAsync();

        Assert.Equal(RemoteCatalogStatus.Updated, r.Status);
        Assert.True(r.ChangedFromCached);
        Assert.Equal(tag, r.TagName);
        Assert.Equal(client.CatalogPath, r.LocalCatalogPath);
        Assert.Equal(client.SignaturePath, r.LocalSignaturePath);
        Assert.Equal("ci-clop", r.Catalog!.Products.Single().ProductId);
        Assert.Equal(catalogBytes, File.ReadAllBytes(client.CatalogPath));
        Assert.Equal(tag, File.ReadAllText(client.TagPath));
        Assert.Equal(generatedAt, client.CachedGeneratedAt());
        Assert.NotNull(client.LoadCached());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task FetchAsync_rejects_valid_signed_catalog_at_or_below_cached_floor(
        int minutesFromFloor)
    {
        var floor = DateTime.UtcNow.AddHours(-2);
        var cached = SeedCache(floor.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var catalogBytes = CatalogBytesAt(floor.AddMinutes(minutesFromFloor));
        var client = NewClient(SignedCatalogHandler(catalogBytes, "catalog-rollback"));

        var r = await client.FetchAsync();

        Assert.Equal(RemoteCatalogStatus.RollbackRejected, r.Status);
        Assert.Contains("<= cached floor", r.Message ?? "");
        AssertCacheUnchanged(cached);
    }

    [Fact]
    public async Task FetchAsync_rejects_implausibly_future_generated_at_without_changing_cache()
    {
        var cached = SeedCache(DateTime.UtcNow.AddDays(-1).ToString(
            "O", System.Globalization.CultureInfo.InvariantCulture));
        var catalogBytes = CatalogBytesAt(DateTime.UtcNow.AddDays(2));
        var client = NewClient(SignedCatalogHandler(catalogBytes, "catalog-future"));

        var r = await client.FetchAsync();

        Assert.Equal(RemoteCatalogStatus.RollbackRejected, r.Status);
        Assert.Contains("implausibly far in the future", r.Message ?? "");
        AssertCacheUnchanged(cached);
    }

    [Fact]
    public async Task FetchAsync_fails_closed_on_corrupt_floor_without_changing_cache()
    {
        var cached = SeedCache("not-a-timestamp");
        var catalogBytes = CatalogBytesAt(DateTime.UtcNow.AddHours(-1));
        var client = NewClient(SignedCatalogHandler(catalogBytes, "catalog-corrupt-floor"));

        var r = await client.FetchAsync();

        Assert.Equal(RemoteCatalogStatus.RollbackRejected, r.Status);
        Assert.Equal(DateTime.MaxValue, client.CachedGeneratedAt());
        AssertCacheUnchanged(cached);
    }

    [Fact]
    public async Task FetchAsync_rejects_oversized_release_metadata_without_changing_cache()
    {
        var cached = SeedCache();
        var oversizedMetadata = new string('x', RemoteCatalogClient.MaxReleaseMetadataBytes + 1);
        var h = new StubHandler(JsonResp(oversizedMetadata));

        var r = await NewClient(h).FetchAsync();

        Assert.Equal(RemoteCatalogStatus.NetworkError, r.Status);
        Assert.Contains("limit", r.Message ?? "");
        Assert.Single(h.Requests);
        AssertCacheUnchanged(cached);
    }

    [Fact]
    public async Task FetchAsync_rejects_oversized_catalog_without_changing_cache()
    {
        var cached = SeedCache();
        var h = new StubHandler(
            JsonResp(ReleaseJson("catalog-oversized")),
            BinaryResp(new byte[RemoteCatalogClient.MaxCatalogBytes + 1]));

        var r = await NewClient(h).FetchAsync();

        Assert.Equal(RemoteCatalogStatus.NetworkError, r.Status);
        Assert.Contains("limit", r.Message ?? "");
        Assert.Equal(2, h.Requests.Count);
        AssertCacheUnchanged(cached);
    }

    [Fact]
    public async Task FetchAsync_rejects_oversized_signature_without_changing_cache()
    {
        var cached = SeedCache();
        var h = new StubHandler(
            JsonResp(ReleaseJson("catalog-oversized-signature")),
            BinaryResp(SampleCatalogBytes),
            BinaryResp(new byte[RemoteCatalogClient.MaxSignatureBytes + 1]));

        var r = await NewClient(h).FetchAsync();

        Assert.Equal(RemoteCatalogStatus.NetworkError, r.Status);
        Assert.Contains("limit", r.Message ?? "");
        Assert.Equal(3, h.Requests.Count);
        AssertCacheUnchanged(cached);
    }

    [Fact]
    public async Task FetchAsync_returns_BadSignature_when_signature_does_not_verify()
    {
        var wrongKeypair = CatalogSignature.GenerateKeypair();
        var sig = CatalogSignature.Sign(SampleCatalogBytes, wrongKeypair.PrivateKey);
        var sigB64 = Convert.ToBase64String(sig);

        var h = new StubHandler(
            JsonResp(ReleaseJson()),
            BinaryResp(SampleCatalogBytes),
            BinaryResp(Encoding.UTF8.GetBytes(sigB64)));

        var r = await NewClient(h).FetchAsync();
        Assert.Equal(RemoteCatalogStatus.BadSignature, r.Status);
        Assert.Null(r.Catalog);
        Assert.False(File.Exists(Path.Combine(_cacheDir, RemoteCatalogClient.CatalogFileName)));
    }

    [Fact]
    public async Task FetchAsync_returns_BadSignature_when_signature_blob_is_not_base64()
    {
        var h = new StubHandler(
            JsonResp(ReleaseJson()),
            BinaryResp(SampleCatalogBytes),
            BinaryResp(Encoding.UTF8.GetBytes("this is not base64!!!@#$")));

        var r = await NewClient(h).FetchAsync();
        Assert.Equal(RemoteCatalogStatus.BadSignature, r.Status);
    }

    [Fact]
    public async Task FetchAsync_uses_cached_catalog_when_tag_unchanged()
    {
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllText(Path.Combine(_cacheDir, RemoteCatalogClient.TagFileName), "catalog-X");

        var sig = CatalogSignature.Sign(SampleCatalogBytes, _kp.PrivateKey);
        File.WriteAllBytes(Path.Combine(_cacheDir, RemoteCatalogClient.CatalogFileName), SampleCatalogBytes);
        File.WriteAllText(Path.Combine(_cacheDir, RemoteCatalogClient.SignatureFileName),
            Convert.ToBase64String(sig));

        var h = new StubHandler(JsonResp(ReleaseJson("catalog-X")));

        var r = await NewClient(h).FetchAsync();

        Assert.Equal(RemoteCatalogStatus.AlreadyUpToDate, r.Status);
        Assert.False(r.ChangedFromCached);
        Assert.Single(h.Requests);
    }

    [Fact]
    public void LoadCached_returns_null_when_nothing_cached()
    {
        Assert.Null(NewClient(new StubHandler()).LoadCached());
    }

    [Fact]
    public void LoadCached_returns_null_when_signature_does_not_match_injected_key()
    {
        Directory.CreateDirectory(_cacheDir);
        var wrongKeypair = CatalogSignature.GenerateKeypair();
        var sig = CatalogSignature.Sign(SampleCatalogBytes, wrongKeypair.PrivateKey);
        File.WriteAllBytes(Path.Combine(_cacheDir, RemoteCatalogClient.CatalogFileName), SampleCatalogBytes);
        File.WriteAllText(Path.Combine(_cacheDir, RemoteCatalogClient.SignatureFileName),
            Convert.ToBase64String(sig));

        Assert.Null(NewClient(new StubHandler()).LoadCached());
    }

    [Fact]
    public void LoadCached_returns_null_for_malformed_or_oversized_cached_signature()
    {
        var client = NewClient(new StubHandler());
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllBytes(client.CatalogPath, SampleCatalogBytes);
        File.WriteAllText(client.SignaturePath, "not-base64");
        Assert.Null(client.LoadCached());

        File.WriteAllBytes(client.SignaturePath,
            new byte[RemoteCatalogClient.MaxSignatureBytes + 1]);
        Assert.Null(client.LoadCached());
    }

    [Fact]
    public void CachedTag_reads_tag_file_contents()
    {
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllText(Path.Combine(_cacheDir, RemoteCatalogClient.TagFileName),
            "  catalog-20260526-090000  \n");
        Assert.Equal("catalog-20260526-090000", NewClient(new StubHandler()).CachedTag());
    }

    [Fact]
    public void CachedTag_returns_null_when_no_file()
    {
        Assert.Null(NewClient(new StubHandler()).CachedTag());
    }

    [Fact]
    public void DefaultCacheDir_is_under_LocalAppData_Iskra_catalog()
    {
        var d = RemoteCatalogClient.DefaultCacheDir();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(local, d);
        Assert.EndsWith(@"Iskra\catalog", d);
    }

    [Fact]
    public async Task FetchAsync_sends_user_agent_and_api_version_headers()
    {
        var h = new StubHandler(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        await NewClient(h).FetchAsync();
        var req = h.Requests.Single();
        Assert.True(req.Headers.UserAgent.Count > 0);
        Assert.Contains("X-GitHub-Api-Version", req.Headers.Select(kv => kv.Key));
    }

    // --- helpers ---------------------------------------------------------

    private StubHandler SignedCatalogHandler(byte[] catalogBytes, string tag)
    {
        var sig = CatalogSignature.Sign(catalogBytes, _kp.PrivateKey);
        return new StubHandler(
            JsonResp(ReleaseJson(tag)),
            BinaryResp(catalogBytes),
            BinaryResp(Encoding.UTF8.GetBytes(Convert.ToBase64String(sig))));
    }

    private IReadOnlyDictionary<string, byte[]> SeedCache(string? floor = null)
    {
        Directory.CreateDirectory(_cacheDir);
        var files = new Dictionary<string, byte[]>
        {
            [RemoteCatalogClient.CatalogFileName] = Encoding.UTF8.GetBytes("cached-catalog-sentinel"),
            [RemoteCatalogClient.SignatureFileName] = Encoding.UTF8.GetBytes("cached-signature-sentinel"),
            [RemoteCatalogClient.TagFileName] = Encoding.UTF8.GetBytes("catalog-cached"),
            [RemoteCatalogClient.GeneratedAtFileName] = Encoding.UTF8.GetBytes(
                floor ?? DateTime.UtcNow.AddDays(-2).ToString(
                    "O", System.Globalization.CultureInfo.InvariantCulture)),
        };

        foreach (var (name, bytes) in files)
            File.WriteAllBytes(Path.Combine(_cacheDir, name), bytes);

        return files;
    }

    private void AssertCacheUnchanged(IReadOnlyDictionary<string, byte[]> expected)
    {
        foreach (var (name, bytes) in expected)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_cacheDir, name)));
    }

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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler(params HttpResponseMessage[] r)
            => _responses = new Queue<HttpResponseMessage>(r);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var snap = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) snap.Headers.TryAddWithoutValidation(h.Key, h.Value);
            Requests.Add(snap);
            if (_responses.Count == 0)
                throw new InvalidOperationException("StubHandler ran out of canned responses");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
