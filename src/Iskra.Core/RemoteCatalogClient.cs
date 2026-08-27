using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Iskra.Core;

public sealed class RemoteCatalogException : Exception
{
    public RemoteCatalogException(string msg, Exception? inner = null) : base(msg, inner) { }
}

/// <summary>
/// Outcome of <see cref="RemoteCatalogClient.FetchAsync"/>. Either we got a
/// fresh verified catalog and saved it on disk, or we have a reason why not.
/// </summary>
public sealed record RemoteCatalogResult(
    Catalog? Catalog,
    string? LocalCatalogPath,
    string? LocalSignaturePath,
    string TagName,
    bool ChangedFromCached,
    RemoteCatalogStatus Status,
    string? Message);

public enum RemoteCatalogStatus
{
    Updated,              // downloaded + verified + replaced cache
    AlreadyUpToDate,      // same tag as we have cached; cache returned
    NoRelease,            // /releases/latest returned 404
    NetworkError,         // any HTTP failure short of 404
    BadSignature,         // download succeeded but signature didn't verify
    AssetsMissing,        // release doesn't have catalog.json + catalog.json.sig
    ParseError,           // catalog.json downloaded but failed to parse
    SourceNotAllowed,     // owner/repo isn't in CatalogTrust.AllowedCatalogSources
    RollbackRejected,     // signed catalog's generated_at <= last accepted (anti-rollback)
    CacheError,           // local transactional cache could not be read/committed safely
}

/// <summary>
/// Fetches signed catalog releases from the iskra-catalog repo on GitHub.
/// <para>The repo is public so the GET is anonymous. The signature is verified
/// against <see cref="CatalogTrust.EmbeddedPublicKey"/>; if it doesn't match,
/// the download is discarded and the cache is left untouched.</para>
/// <para>Cache layout is generation-based. A complete signed catalog,
/// signature, and tag are written beneath <c>generations/&lt;id&gt;</c>; one small
/// atomically replaced <c>current.json</c> pointer activates the generation.
/// Legacy <c>latest.*</c> files remain readable only when no pointer exists.</para>
/// </summary>
public sealed class RemoteCatalogClient
{
    public const string DefaultDirectoryName = "Iskra";
    public const string DefaultSubdirectoryName = "catalog";
    public const string CatalogFileName        = "latest.json";
    public const string SignatureFileName      = "latest.json.sig";
    public const string TagFileName            = "latest.tag";
    /// <summary>Anti-rollback floor: ISO-8601 UTC of the most recently committed catalog's
    /// <c>generated_at</c>. We refuse to overwrite the cache with an older catalog even
    /// when the signature is valid.</summary>
    public const string GeneratedAtFileName    = "latest.generated_at";
    public const string CurrentPointerFileName = "current.json";
    public const string GenerationsDirectoryName = "generations";
    public const string CacheLockFileName = ".cache.lock";

    public const string ApiBaseUrl  = "https://api.github.com";
    public const string ApiAccept   = "application/vnd.github+json";
    public const string ApiVersion  = "2022-11-28";
    public const int MaxReleaseMetadataBytes = 1 * 1024 * 1024;
    public const int MaxCatalogBytes = 2 * 1024 * 1024;
    public const int MaxSignatureBytes = 1024;

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;
    private readonly string _cacheDir;
    private readonly byte[] _verificationPublicKey;

    /// <summary>
    /// Creates the production client. Source allowlisting and the embedded
    /// verification key are not caller-configurable on this public surface.
    /// </summary>
    public RemoteCatalogClient(HttpClient http)
        : this(
            http,
            owner: CatalogTrust.OfficialCatalogSource.Owner,
            repo: CatalogTrust.OfficialCatalogSource.Repo,
            cacheDirOverride: null,
            enforceAllowlist: true,
            verificationPublicKey: null)
    {
    }

    internal RemoteCatalogClient(
        HttpClient http,
        string? owner = null,
        string? repo  = null,
        string? cacheDirOverride = null,
        bool enforceAllowlist = true,
        byte[]? verificationPublicKey = null)
    {
        if (http is null) throw new ArgumentNullException(nameof(http));
        // Default to the canonical official source. Callers can pass the
        // allowlisted values explicitly; non-allowlisted values are refused
        // here so a tampered AppSettings.json can never cause an HTTP request
        // to a non-official catalog.
        owner ??= CatalogTrust.OfficialCatalogSource.Owner;
        repo  ??= CatalogTrust.OfficialCatalogSource.Repo;
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))  throw new ArgumentException("repo required",  nameof(repo));
        if (enforceAllowlist && !CatalogTrust.IsAllowedCatalogSource(owner, repo))
            throw new ArgumentException(
                $"'{owner}/{repo}' is not in CatalogTrust.AllowedCatalogSources",
                nameof(owner));
        _http  = http;
        _owner = owner;
        _repo  = repo;
        _cacheDir = cacheDirOverride ?? DefaultCacheDir();
        _verificationPublicKey = verificationPublicKey
            ?? CatalogTrust.EmbeddedPublicKey
            ?? throw new ArgumentException("catalog verification public key is not configured");
    }

    public static string DefaultCacheDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, DefaultDirectoryName, DefaultSubdirectoryName);
    }

    public string CatalogPath        => ResolveActivePaths(_cacheDir).CatalogPath;
    public string SignaturePath      => ResolveActivePaths(_cacheDir).SignaturePath;
    public string TagPath            => ResolveActivePaths(_cacheDir).TagPath;
    public string GeneratedAtPath    => Path.Combine(_cacheDir, GeneratedAtFileName);
    public string CurrentPointerPath => Path.Combine(_cacheDir, CurrentPointerFileName);

    public static string ActiveCatalogPath(string? cacheDirOverride = null) =>
        ResolveActivePaths(cacheDirOverride ?? DefaultCacheDir()).CatalogPath;

    private sealed record CachePaths(
        string CatalogPath,
        string SignaturePath,
        string TagPath,
        string? ExpectedDigest,
        bool IsPointerGeneration);

    private static CachePaths ResolveActivePaths(string cacheDir)
    {
        var pointerPath = Path.Combine(cacheDir, CurrentPointerFileName);
        if (!File.Exists(pointerPath))
        {
            return new CachePaths(
                Path.Combine(cacheDir, CatalogFileName),
                Path.Combine(cacheDir, SignatureFileName),
                Path.Combine(cacheDir, TagFileName),
                null,
                false);
        }

        try
        {
            using var document = JsonDocument.Parse(ReadFileLimited(pointerPath, 4096));
            var root = document.RootElement;
            var generation = root.GetProperty("generation").GetString();
            var digest = root.GetProperty("catalog_sha256").GetString();
            if (root.GetProperty("schema_version").GetInt32() != 1
                || !IsSafeGenerationName(generation)
                || !FirmwareIntegrity.IsValidSha256Hex(digest))
            {
                throw new FormatException("catalog cache pointer is malformed");
            }

            var generationDirectory = Path.Combine(
                cacheDir,
                GenerationsDirectoryName,
                generation!);
            return new CachePaths(
                Path.Combine(generationDirectory, "catalog.json"),
                Path.Combine(generationDirectory, "catalog.json.sig"),
                Path.Combine(generationDirectory, "catalog.tag"),
                digest!.ToLowerInvariant(),
                true);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException
            or KeyNotFoundException
            or FormatException)
        {
            // A pointer means generation mode is authoritative. Never fall
            // through to legacy latest.* files when that pointer is corrupt,
            // because doing so could silently reactivate older signed data.
            var invalid = Path.Combine(cacheDir, ".invalid-current");
            return new CachePaths(
                Path.Combine(invalid, "catalog.json"),
                Path.Combine(invalid, "catalog.json.sig"),
                Path.Combine(invalid, "catalog.tag"),
                null,
                true);
        }
    }

    private static bool IsSafeGenerationName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 160
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>
    /// Reads the cached anti-rollback floor (catalog <c>generated_at</c> of the
    /// last successful commit). Returns <see cref="DateTime.MinValue"/> when
    /// nothing is cached. If the floor file exists but is unreadable or
    /// malformed, returns <see cref="DateTime.MaxValue"/> so rollback defense
    /// fails closed until a supervisor performs explicit cache recovery.
    /// </summary>
    public DateTime CachedGeneratedAt()
    {
        if (!File.Exists(GeneratedAtPath)) return DateTime.MinValue;
        try
        {
            var s = System.Text.Encoding.UTF8.GetString(
                ReadFileLimited(GeneratedAtPath, 512)).Trim();
            if (s.StartsWith('{'))
            {
                using var document = JsonDocument.Parse(s);
                s = document.RootElement.GetProperty("generated_at_utc").GetString() ?? "";
            }
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;
        }
        catch { /* fail closed below */ }
        return DateTime.MaxValue;
    }

    /// <summary>
    /// Returns the catalog cached on disk from a previous successful fetch,
    /// or <c>null</c> if there's nothing cached. Re-verifies the signature
    /// against the currently embedded public key (catches key rotation).
    /// </summary>
    public Catalog? LoadCached()
    {
        var paths = ResolveActivePaths(_cacheDir);
        if (!File.Exists(paths.CatalogPath) || !File.Exists(paths.SignaturePath)) return null;
        try
        {
            var bytes = ReadFileLimited(paths.CatalogPath, MaxCatalogBytes);
            var digest = CatalogActivationPolicy.ComputeCatalogSha256(bytes);
            if (paths.ExpectedDigest is not null
                && !FirmwareIntegrity.HashesMatch(digest, paths.ExpectedDigest))
            {
                return null;
            }
            var sigText = System.Text.Encoding.UTF8.GetString(
                ReadFileLimited(paths.SignaturePath, MaxSignatureBytes)).Trim();
            var sig = Convert.FromBase64String(sigText);
            if (!CatalogSignature.Verify(bytes, sig, _verificationPublicKey)) return null;
            var catalog = CatalogJson.Parse(bytes);
            CatalogJson.ValidateTrustedArtifactPaths(catalog);
            var activation = CatalogActivationPolicy.ValidateAndAdvance(
                catalog.GeneratedAt,
                GeneratedAtPath,
                catalogSha256: digest);
            if (!activation.IsAccepted) return null;
            return catalog;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or FormatException
            or ArgumentException
            or System.Security.Cryptography.CryptographicException
            or CatalogParseException)
        {
            return null;
        }
    }

    public string? CachedTag()
    {
        var paths = ResolveActivePaths(_cacheDir);
        if (!File.Exists(paths.TagPath)) return null;
        try
        {
            return System.Text.Encoding.UTF8.GetString(
                ReadFileLimited(paths.TagPath, 1024)).Trim();
        }
        catch { return null; }
    }

    /// <summary>
    /// Fetches the latest release of the catalog repo; if the tag has changed
    /// since the last successful fetch (or no cache exists), downloads the
    /// new assets, verifies the signature, and commits them to the cache
    /// atomically.
    /// </summary>
    public async Task<RemoteCatalogResult> FetchAsync(CancellationToken ct = default)
    {
        // 1) GET /repos/{owner}/{repo}/releases/latest
        var url = $"{ApiBaseUrl}/repos/{_owner}/{_repo}/releases/latest";
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(
                NewApiRequest(HttpMethod.Get, url),
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { return Failure(RemoteCatalogStatus.NetworkError, ex.Message); }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.NotFound)
                return Failure(RemoteCatalogStatus.NoRelease,
                    $"{_owner}/{_repo} has no releases yet");
            if (!resp.IsSuccessStatusCode)
                return Failure(RemoteCatalogStatus.NetworkError,
                    $"GET releases/latest → {(int)resp.StatusCode} {resp.ReasonPhrase}");

            string body;
            try
            {
                var bodyBytes = await ReadLimitedAsync(
                    resp.Content, MaxReleaseMetadataBytes, ct).ConfigureAwait(false);
                body = System.Text.Encoding.UTF8.GetString(bodyBytes);
            }
            catch (HttpRequestException ex)
            {
                return Failure(RemoteCatalogStatus.NetworkError, ex.Message);
            }

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException ex)
            {
                return Failure(RemoteCatalogStatus.ParseError,
                    $"release metadata is invalid JSON: {ex.Message}");
            }
            using (doc)
            {
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrEmpty(tagName))
                return Failure(RemoteCatalogStatus.AssetsMissing, "release has no tag_name");

            var cachedTag = CachedTag();
            var assetsSection = root.TryGetProperty("assets", out var aEl) && aEl.ValueKind == JsonValueKind.Array
                ? aEl : default;
            if (assetsSection.ValueKind != JsonValueKind.Array)
                return Failure(RemoteCatalogStatus.AssetsMissing, "release has no assets array");

            string? catalogUrl = null, sigUrl = null;
            foreach (var a in assetsSection.EnumerateArray())
            {
                if (!a.TryGetProperty("name", out var nEl) || !a.TryGetProperty("browser_download_url", out var uEl))
                    continue;
                var name = nEl.GetString();
                if (string.Equals(name, "catalog.json", StringComparison.Ordinal))         catalogUrl = uEl.GetString();
                else if (string.Equals(name, "catalog.json.sig", StringComparison.Ordinal)) sigUrl     = uEl.GetString();
            }
            if (catalogUrl is null || sigUrl is null)
                return Failure(RemoteCatalogStatus.AssetsMissing,
                    $"{tagName} is missing catalog.json or catalog.json.sig");
            if (!GitHubUrlPolicy.IsTrustedWebUrl(catalogUrl)
                || !GitHubUrlPolicy.IsTrustedWebUrl(sigUrl))
            {
                return Failure(
                    RemoteCatalogStatus.AssetsMissing,
                    $"{tagName} contains a catalog asset URL outside the trusted github.com HTTPS origin");
            }

            if (string.Equals(cachedTag, tagName, StringComparison.Ordinal) && File.Exists(CatalogPath))
            {
                var cached = LoadCached();
                if (cached is not null)
                {
                    var active = ResolveActivePaths(_cacheDir);
                    return new RemoteCatalogResult(
                        Catalog: cached,
                        LocalCatalogPath: active.CatalogPath,
                        LocalSignaturePath: active.SignaturePath,
                        TagName: tagName, ChangedFromCached: false,
                        Status: RemoteCatalogStatus.AlreadyUpToDate, Message: null);
                }
            }

            // 2) Download the two assets to disk (anonymous; public repo).
            byte[] catalogBytes, sigBytes;
            try
            {
                catalogBytes = await GetBytesAsync(
                    catalogUrl, MaxCatalogBytes, ct).ConfigureAwait(false);
                sigBytes = await GetBytesAsync(
                    sigUrl, MaxSignatureBytes, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                return Failure(RemoteCatalogStatus.NetworkError, ex.Message);
            }

            // 3) Verify signature with the embedded public key.
            byte[] sig;
            try { sig = Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(sigBytes).Trim()); }
            catch (FormatException) { return Failure(RemoteCatalogStatus.BadSignature, "signature is not base64"); }
            if (!CatalogSignature.Verify(catalogBytes, sig, _verificationPublicKey))
                return Failure(RemoteCatalogStatus.BadSignature,
                    "downloaded catalog signature did not match the embedded public key");

            // 4) Parse — refuse to commit an unparseable catalog.
            Catalog catalog;
            try
            {
                catalog = CatalogJson.Parse(catalogBytes);
                CatalogJson.ValidateTrustedArtifactPaths(catalog);
            }
            catch (CatalogParseException ex) { return Failure(RemoteCatalogStatus.ParseError, ex.Message); }

            // 4a) Anti-rollback: the catalog body itself is signed, so its
            // generated_at field cannot be forged. Reject anything older than
            // the most recently committed catalog — protects against an
            // attacker re-serving an older signed catalog (e.g. one whose
            // revocation list hasn't yet blocked a since-revoked release).
            var digest = CatalogActivationPolicy.ComputeCatalogSha256(catalogBytes);
            try
            {
                Directory.CreateDirectory(_cacheDir);
                using var cacheLock = new FileStream(
                    Path.Combine(_cacheDir, CacheLockFileName),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);

                // Another process may have committed while this process was
                // downloading. Re-check under the cache lock before creating a
                // new generation.
                if (string.Equals(CachedTag(), tagName, StringComparison.Ordinal))
                {
                    var cached = LoadCached();
                    if (cached is not null)
                    {
                        var cachedPaths = ResolveActivePaths(_cacheDir);
                        return new RemoteCatalogResult(
                            cached,
                            cachedPaths.CatalogPath,
                            cachedPaths.SignaturePath,
                            tagName,
                            false,
                            RemoteCatalogStatus.AlreadyUpToDate,
                            null);
                    }
                }

                var validation = CatalogActivationPolicy.ValidateAndAdvance(
                    catalog.GeneratedAt,
                    GeneratedAtPath,
                    requireNewer: true,
                    catalogSha256: digest,
                    advance: false);
                if (!validation.IsAccepted)
                {
                    return Failure(RemoteCatalogStatus.RollbackRejected,
                        validation.Diagnostic ?? validation.Status.ToString());
                }

                var committed = WriteGeneration(catalogBytes, sigBytes, tagName, digest);

                // Advance the signed timestamp+digest floor before activating
                // the pointer. A crash in between therefore fails closed on
                // the old generation; it can never activate the new catalog
                // without its rollback floor.
                var activation = CatalogActivationPolicy.ValidateAndAdvance(
                    catalog.GeneratedAt,
                    GeneratedAtPath,
                    requireNewer: true,
                    catalogSha256: digest);
                if (!activation.IsAccepted)
                {
                    return Failure(RemoteCatalogStatus.RollbackRejected,
                        activation.Diagnostic ?? activation.Status.ToString());
                }

                var pointer = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schema_version = 1,
                    generation = Path.GetFileName(Path.GetDirectoryName(committed.CatalogPath)),
                    catalog_sha256 = digest,
                    generated_at_utc = catalog.GeneratedAt.ToUniversalTime().ToString("O"),
                    tag = tagName,
                });
                WriteAtomic(CurrentPointerPath, pointer);
                var active = ResolveActivePaths(_cacheDir);

                return new RemoteCatalogResult(
                    Catalog: catalog,
                    LocalCatalogPath: active.CatalogPath,
                    LocalSignaturePath: active.SignaturePath,
                    TagName: tagName,
                    ChangedFromCached: cachedTag != tagName,
                    Status: RemoteCatalogStatus.Updated,
                    Message: null);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or JsonException)
            {
                return Failure(RemoteCatalogStatus.CacheError, ex.Message);
            }
            }
        }
    }

    private CachePaths WriteGeneration(
        byte[] catalogBytes,
        byte[] signatureBytes,
        string tagName,
        string digest)
    {
        var generations = Path.Combine(_cacheDir, GenerationsDirectoryName);
        Directory.CreateDirectory(generations);
        var generationName = $"g-{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{digest[..16]}-{Guid.NewGuid():N}";
        var temporaryDirectory = Path.Combine(generations, ".tmp-" + Guid.NewGuid().ToString("N"));
        var generationDirectory = Path.Combine(generations, generationName);
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            WriteNewDurable(Path.Combine(temporaryDirectory, "catalog.json"), catalogBytes);
            WriteNewDurable(Path.Combine(temporaryDirectory, "catalog.json.sig"), signatureBytes);
            WriteNewDurable(
                Path.Combine(temporaryDirectory, "catalog.tag"),
                System.Text.Encoding.UTF8.GetBytes(tagName));
            Directory.Move(temporaryDirectory, generationDirectory);
            return new CachePaths(
                Path.Combine(generationDirectory, "catalog.json"),
                Path.Combine(generationDirectory, "catalog.json.sig"),
                Path.Combine(generationDirectory, "catalog.tag"),
                digest,
                true);
        }
        catch
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch { }
            throw;
        }
    }

    private static void WriteNewDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        });
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private async Task<byte[]> GetBytesAsync(
        string url,
        int maxBytes,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("Iskra");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET {url} → {(int)resp.StatusCode} {resp.ReasonPhrase}");
        return await ReadLimitedAsync(resp.Content, maxBytes, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new HttpRequestException(
                $"response is {declared} bytes; limit is {maxBytes}");

        await using var source = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var destination = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                .ConfigureAwait(false);
            if (read == 0) break;
            if (destination.Length + read > maxBytes)
                throw new HttpRequestException($"response exceeded {maxBytes} byte limit");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static byte[] ReadFileLimited(string path, int maxBytes)
    {
        using var source = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 16 * 1024, FileOptions.SequentialScan);
        if (source.Length > maxBytes)
            throw new IOException($"cached file exceeded {maxBytes} byte limit");

        using var destination = new MemoryStream((int)Math.Min(source.Length, maxBytes));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (destination.Length + read > maxBytes)
                throw new IOException($"cached file exceeded {maxBytes} byte limit");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private HttpRequestMessage NewApiRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ApiAccept));
        req.Headers.UserAgent.ParseAdd("Iskra");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
        return req;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var tmp = path + $".{Guid.NewGuid():N}.tmp";
        using (var stream = new FileStream(tmp, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.WriteThrough,
        }))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        try { File.Move(tmp, path, overwrite: true); }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    private RemoteCatalogResult Failure(RemoteCatalogStatus status, string message)
        => new(Catalog: null, LocalCatalogPath: null, LocalSignaturePath: null,
               TagName: "", ChangedFromCached: false, Status: status, Message: message);
}
