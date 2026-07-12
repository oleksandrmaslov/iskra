using Iskra.Core;

namespace Iskra.Application;

public enum CatalogSessionStatus
{
    Ready,
    NotFound,
    ExplicitPathMissing,
    SideloadRequiresLabMode,
    TrustRejected,
    ParseError,
    IoError,
    UnexpectedError,
}

/// <summary>A fail-closed catalog selection result suitable for any UI.</summary>
public sealed record CatalogSessionResult(
    CatalogSessionStatus Status,
    Catalog? Catalog,
    string? SourcePath,
    string? SourceDirectory,
    CatalogTrustResult? TrustResult,
    bool IsSideload,
    string? Diagnostic)
{
    public bool IsReady => Status == CatalogSessionStatus.Ready && Catalog is not null;
}

public interface ICatalogSession
{
    CatalogSessionResult Current { get; }
    CatalogSessionResult Load(AppSettings settings);
}

/// <summary>
/// Chooses, verifies, and parses the active catalog with deterministic source
/// precedence. An explicit configured path is authoritative: if it is missing,
/// untrusted, or malformed, the session refuses it and never silently falls
/// through to a bundled catalog. Without an explicit path, the first existing
/// fallback is likewise authoritative and fails closed if invalid.
/// </summary>
public sealed class CatalogSession : ICatalogSession
{
    private readonly Func<AppSettings, IEnumerable<string>> _fallbackCandidates;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool, CatalogTrustResult> _verifyCatalog;
    private readonly Func<string, Catalog> _parseCatalog;
    private readonly Func<string, Catalog> _buildSideload;
    private readonly Func<bool> _unsignedLabModeEnabled;
    private readonly Func<string, string> _normalizePath;

    public CatalogSessionResult Current { get; private set; } = NotFound(
        "Catalog has not been loaded yet.");

    public CatalogSession(
        Func<AppSettings, IEnumerable<string>>? fallbackCandidates = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null,
        Func<string, bool, CatalogTrustResult>? verifyCatalog = null,
        Func<string, Catalog>? parseCatalog = null,
        Func<string, Catalog>? buildSideload = null,
        Func<bool>? unsignedLabModeEnabled = null,
        Func<string, string>? normalizePath = null)
    {
        _fallbackCandidates = fallbackCandidates ?? DefaultFallbackCandidates;
        _fileExists = fileExists ?? File.Exists;
        _directoryExists = directoryExists ?? Directory.Exists;
        _verifyCatalog = verifyCatalog ?? ((path, requireSigned) =>
            CatalogTrust.VerifyCatalogFile(path, requireSigned));
        _parseCatalog = parseCatalog ?? CatalogJson.ParseFile;
        _buildSideload = buildSideload ?? (path => SideloadCatalogBuilder.BuildFromDirectory(path));
        _unsignedLabModeEnabled = unsignedLabModeEnabled ?? CatalogTrust.IsUnsignedLabModeEnabled;
        _normalizePath = normalizePath ?? Path.GetFullPath;
    }

    public CatalogSessionResult Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            if (!string.IsNullOrWhiteSpace(settings.CatalogPath))
            {
                var explicitPath = _normalizePath(settings.CatalogPath.Trim());
                if (!_fileExists(explicitPath) && !_directoryExists(explicitPath))
                {
                    return SetCurrent(new CatalogSessionResult(
                        CatalogSessionStatus.ExplicitPathMissing,
                        null,
                        explicitPath,
                        null,
                        null,
                        false,
                        "The explicitly configured catalog path does not exist."));
                }

                return SetCurrent(EvaluateExistingPath(explicitPath, settings));
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in _fallbackCandidates(settings))
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var path = _normalizePath(candidate.Trim());
                if (!seen.Add(path))
                {
                    continue;
                }
                if (!_fileExists(path) && !_directoryExists(path))
                {
                    continue;
                }

                // First existing fallback wins. If it is untrusted or broken,
                // returning its failure prevents a silent downgrade to an older
                // bundled catalog.
                return SetCurrent(EvaluateExistingPath(path, settings));
            }

            return SetCurrent(NotFound("No catalog exists at any configured fallback path."));
        }
        catch (IOException ex)
        {
            return SetCurrent(Failure(CatalogSessionStatus.IoError, null, ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            return SetCurrent(Failure(CatalogSessionStatus.IoError, null, ex.Message));
        }
        catch (Exception ex)
        {
            return SetCurrent(Failure(CatalogSessionStatus.UnexpectedError, null, ex.Message));
        }
    }

    private CatalogSessionResult EvaluateExistingPath(string path, AppSettings settings)
    {
        var labAllowsUnsigned = !settings.RequireSignedCatalog && _unsignedLabModeEnabled();

        if (_directoryExists(path))
        {
            if (!labAllowsUnsigned)
            {
                return new CatalogSessionResult(
                    CatalogSessionStatus.SideloadRequiresLabMode,
                    null,
                    path,
                    path,
                    CatalogTrustResult.UnsignedRejected,
                    true,
                    "Sideload directories require the explicit unsigned lab mode.");
            }

            try
            {
                var catalog = _buildSideload(path);
                return Ready(catalog, path, path, CatalogTrustResult.UnsignedAllowed, isSideload: true);
            }
            catch (SideloadCatalogException ex)
            {
                return Failure(CatalogSessionStatus.ParseError, path, ex.Message, isSideload: true);
            }
            catch (IOException ex)
            {
                return Failure(CatalogSessionStatus.IoError, path, ex.Message, isSideload: true);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Failure(CatalogSessionStatus.IoError, path, ex.Message, isSideload: true);
            }
        }

        // A persisted false setting alone cannot enable unsigned input. Without
        // the process-level lab switch we still ask Core to require a signature.
        var requireSigned = !labAllowsUnsigned;
        var trust = _verifyCatalog(path, requireSigned);
        var accepted = trust == CatalogTrustResult.Verified
            || (labAllowsUnsigned && trust == CatalogTrustResult.UnsignedAllowed);
        if (!accepted)
        {
            return new CatalogSessionResult(
                CatalogSessionStatus.TrustRejected,
                null,
                path,
                Path.GetDirectoryName(path),
                trust,
                false,
                $"Catalog trust check failed: {trust}.");
        }

        try
        {
            var catalog = _parseCatalog(path);
            return Ready(catalog, path, Path.GetDirectoryName(path), trust, isSideload: false);
        }
        catch (CatalogParseException ex)
        {
            return Failure(CatalogSessionStatus.ParseError, path, ex.Message);
        }
        catch (IOException ex)
        {
            return Failure(CatalogSessionStatus.IoError, path, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failure(CatalogSessionStatus.IoError, path, ex.Message);
        }
    }

    private CatalogSessionResult SetCurrent(CatalogSessionResult result)
    {
        Current = result;
        return result;
    }

    private static CatalogSessionResult Ready(
        Catalog catalog,
        string sourcePath,
        string? sourceDirectory,
        CatalogTrustResult trust,
        bool isSideload) => new(
            CatalogSessionStatus.Ready,
            catalog,
            sourcePath,
            sourceDirectory,
            trust,
            isSideload,
            null);

    private static CatalogSessionResult NotFound(string diagnostic) => new(
        CatalogSessionStatus.NotFound,
        null,
        null,
        null,
        null,
        false,
        diagnostic);

    private static CatalogSessionResult Failure(
        CatalogSessionStatus status,
        string? sourcePath,
        string diagnostic,
        bool isSideload = false) => new(
            status,
            null,
            sourcePath,
            isSideload ? sourcePath : sourcePath is null ? null : Path.GetDirectoryName(sourcePath),
            null,
            isSideload,
            diagnostic);

    private static IEnumerable<string> DefaultFallbackCandidates(AppSettings settings)
    {
        if (settings.CatalogAutoUpdate)
        {
            yield return Path.Combine(
                RemoteCatalogClient.DefaultCacheDir(),
                RemoteCatalogClient.CatalogFileName);
        }

        yield return Path.Combine(AppContext.BaseDirectory, "examples", "catalog.json");
        yield return Path.Combine(AppContext.BaseDirectory, "catalog.json");
        yield return Path.Combine(Environment.CurrentDirectory, "examples", "catalog.json");
        yield return Path.Combine(Environment.CurrentDirectory, "catalog.json");
    }
}
