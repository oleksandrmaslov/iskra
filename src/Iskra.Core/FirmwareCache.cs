namespace Iskra.Core;

public sealed class FirmwareCacheException : Exception
{
    public FirmwareCacheException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// On-disk cache for firmware downloaded from GitHub release assets.
/// Layout: <c>%LOCALAPPDATA%\Iskra\firmware-cache\&lt;owner&gt;_&lt;repo&gt;\&lt;tag&gt;\&lt;asset&gt;</c>.
/// <para>Policy: re-hash on every <see cref="GetOrDownloadAsync"/> call.
/// If the on-disk hash matches the catalog's <paramref name="expectedSha256"/>,
/// return the cached path; otherwise (mismatch or cache miss) download,
/// verify, atomically commit, and return the new path. If the downloaded
/// bytes don't match, the temporary download is removed and an exception is
/// thrown. An existing cache entry is left in place until a verified replacement
/// is ready.</para>
/// </summary>
public sealed class FirmwareCache
{
    public const string DefaultDirectoryName = "Iskra";
    public const string DefaultSubdirectoryName = "firmware-cache";

    private readonly string _root;
    private readonly string _tempRoot;
    private readonly GitHubReleaseAssetClient _api;
    private readonly Func<CancellationToken, Task<string>> _getAccessToken;

    // Keep cache paths portable even when tests or future builds run on a host
    // whose Path.GetInvalidFileNameChars() is less restrictive than Windows.
    private const string PortableInvalidFileNameChars = "<>:\"/\\|?*";

    /// <param name="api">Pure HTTP layer.</param>
    /// <param name="getAccessToken">Caller-supplied fresh-token source (see
    /// <see cref="AccessTokenProvider.GetFreshAccessTokenAsync"/>). Called per
    /// download attempt so we always use a non-stale token.</param>
    /// <param name="rootOverride">Alternate cache root, for tests.</param>
    public FirmwareCache(
        GitHubReleaseAssetClient api,
        Func<CancellationToken, Task<string>> getAccessToken,
        string? rootOverride = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _getAccessToken = getAccessToken ?? throw new ArgumentNullException(nameof(getAccessToken));
        var root = rootOverride ?? DefaultRoot();
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("cache root required", nameof(rootOverride));

        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _tempRoot = CanonicalizeUnderRoot(Path.Combine(_root, ".tmp"));
    }

    public static string DefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, DefaultDirectoryName, DefaultSubdirectoryName);
    }

    public string PathFor(GitHubReleaseRef src)
    {
        if (src is null) throw new ArgumentNullException(nameof(src));

        var (owner, repo) = ParseRepo(src.Repo);
        ValidatePathSegment(src.Tag, nameof(src.Tag));
        ValidatePathSegment(src.Asset, nameof(src.Asset));

        var ownerRepo = $"{owner}_{repo}";
        return CanonicalizeUnderRoot(Path.Combine(_root, ownerRepo, src.Tag, src.Asset));
    }

    /// <summary>
    /// Returns a local path containing the asset bytes, verified against
    /// <paramref name="expectedSha256"/>. Cache-hit + matching hash skips the
    /// network entirely; cache-miss or hash-mismatch triggers a download.
    /// </summary>
    public async Task<string> GetOrDownloadAsync(
        GitHubReleaseRef src,
        string expectedSha256,
        CancellationToken ct = default)
    {
        if (src is null) throw new ArgumentNullException(nameof(src));
        if (!FirmwareIntegrity.IsValidSha256Hex(expectedSha256))
            throw new ArgumentException("expectedSha256 must be 64 hex chars",
                nameof(expectedSha256));

        var dest = PathFor(src);

        if (File.Exists(dest))
        {
            var onDisk = FirmwareIntegrity.ComputeSha256Hex(dest);
            if (FirmwareIntegrity.HashesMatch(onDisk, expectedSha256))
                return dest;
            // Stale or tampered. Keep it until a verified replacement is ready;
            // failed downloads must not destroy the last on-disk copy.
        }

        await DownloadAndVerifyAsync(src, expectedSha256, dest, ct).ConfigureAwait(false);
        return dest;
    }

    private async Task DownloadAndVerifyAsync(
        GitHubReleaseRef src, string expectedSha256, string dest, CancellationToken ct)
    {
        var token = await _getAccessToken(ct).ConfigureAwait(false);
        string assetUrl;
        try
        {
            assetUrl = await _api.GetAssetDownloadUrlAsync(
                src.Repo, src.Tag, src.Asset, token, ct).ConfigureAwait(false);
        }
        catch (GitHubAssetNotFoundException) { throw; }
        catch (GitHubApiException ex)
        {
            throw new FirmwareCacheException(
                $"could not list assets for {src.Repo}@{src.Tag}: {ex.Message}", ex);
        }

        var dir = Path.GetDirectoryName(dest)
            ?? throw new FirmwareCacheException("cache destination has no parent directory");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(_tempRoot);

        // A predictable dest + ".tmp" file could be pre-created as a link and
        // also made concurrent downloads trample each other. Keep staging files
        // unique and inside the cache-owned temp directory.
        var tmp = CanonicalizeUnderRoot(Path.Combine(
            _tempRoot, $"{Guid.NewGuid():N}.download"));
        try
        {
            await using (var fs = new FileStream(
                tmp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
                await _api.DownloadAssetAsync(assetUrl, token, fs, ct).ConfigureAwait(false);

            var actual = FirmwareIntegrity.ComputeSha256Hex(tmp);
            if (!FirmwareIntegrity.HashesMatch(actual, expectedSha256))
            {
                throw new FirmwareCacheException(
                    $"{src.Repo}@{src.Tag} asset '{src.Asset}' downloaded but " +
                    $"sha256={actual} did not match catalog {expectedSha256.ToLowerInvariant()}");
            }

            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    private static (string Owner, string Repo) ParseRepo(string repo)
    {
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("repo must be owner/name", nameof(repo));
        if (Path.IsPathRooted(repo) || repo.Contains('\\'))
            throw new ArgumentException("repo must be owner/name without filesystem paths", nameof(repo));

        var parts = repo.Split('/');
        if (parts.Length != 2)
            throw new ArgumentException("repo must be owner/name", nameof(repo));

        ValidatePathSegment(parts[0], nameof(repo));
        ValidatePathSegment(parts[1], nameof(repo));
        return (parts[0], parts[1]);
    }

    private static void ValidatePathSegment(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("path segment required", paramName);
        if (value is "." or ".." || Path.IsPathRooted(value))
            throw new ArgumentException("path traversal is not allowed", paramName);
        if (value.EndsWith(' ') || value.EndsWith('.'))
            throw new ArgumentException("path segments may not end in a space or dot", paramName);
        if (value.Any(c => c < ' ' || PortableInvalidFileNameChars.Contains(c)))
            throw new ArgumentException("path separators and invalid filename characters are not allowed", paramName);
    }

    private string CanonicalizeUnderRoot(string path)
    {
        var candidate = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_root, candidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("cache path must stay under the cache root", nameof(path));
        }

        return candidate;
    }
}
