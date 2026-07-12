using Iskra.Core;

namespace Iskra.Application;

public enum ProbeReadinessStatus
{
    Ready,
    NotFound,
    MultipleFound,
    DiscoveryFailed,
}

public enum GdbReadinessStatus
{
    Ready,
    NotFound,
    PathMissing,
    DiscoveryFailed,
}

public enum StationReadinessIssue
{
    ProbeNotFound,
    MultipleProbes,
    ProbeDiscoveryFailed,
    GdbNotFound,
    GdbPathMissing,
    GdbDiscoveryFailed,
    CatalogNotReady,
}

public sealed record ProbeReadiness(
    ProbeReadinessStatus Status,
    IReadOnlyList<ProbeInfo> Discovered,
    ProbeInfo? Selected,
    string? Diagnostic)
{
    public bool IsReady => Status == ProbeReadinessStatus.Ready && Selected is not null;
}

public sealed record GdbReadiness(
    GdbReadinessStatus Status,
    string? Path,
    string? Diagnostic)
{
    public bool IsReady => Status == GdbReadinessStatus.Ready && !string.IsNullOrWhiteSpace(Path);
}

public sealed record StationReadinessSnapshot(
    ProbeReadiness Probe,
    GdbReadiness Gdb,
    CatalogSessionResult Catalog,
    IReadOnlyList<StationReadinessIssue> Issues)
{
    public bool IsReady => Probe.IsReady && Gdb.IsReady && Catalog.IsReady && Issues.Count == 0;
}

/// <summary>
/// Performs the read-only station gates shared by WPF and Avalonia. Exactly one
/// BMP GDB interface is required; zero and multiple probes both block flashing.
/// Exceptions from platform discovery are converted to explicit blocked states.
/// </summary>
public sealed class StationReadinessService
{
    private readonly ICatalogSession _catalogSession;
    private readonly Func<IReadOnlyList<ProbeInfo>> _discoverProbes;
    private readonly Func<string?, string?> _discoverGdb;
    private readonly Func<string, bool> _fileExists;

    public StationReadinessService(
        ICatalogSession catalogSession,
        Func<IReadOnlyList<ProbeInfo>>? discoverProbes = null,
        Func<string?, string?>? discoverGdb = null,
        Func<string, bool>? fileExists = null)
    {
        _catalogSession = catalogSession ?? throw new ArgumentNullException(nameof(catalogSession));
        _discoverProbes = discoverProbes ?? ProbeDiscovery.FindGdbPorts;
        _discoverGdb = discoverGdb ?? GdbDiscovery.Find;
        _fileExists = fileExists ?? File.Exists;
    }

    public StationReadinessSnapshot Evaluate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var issues = new List<StationReadinessIssue>();
        var probe = EvaluateProbe(issues);
        var gdb = EvaluateGdb(settings.GdbPath, issues);

        CatalogSessionResult catalog;
        try
        {
            catalog = _catalogSession.Load(settings);
        }
        catch (Exception ex)
        {
            // A custom/injected implementation must not be able to turn a
            // readiness refresh into a UI crash.
            catalog = new CatalogSessionResult(
                CatalogSessionStatus.UnexpectedError,
                null,
                null,
                null,
                null,
                false,
                ex.Message);
        }

        if (!catalog.IsReady)
        {
            issues.Add(StationReadinessIssue.CatalogNotReady);
        }

        return new StationReadinessSnapshot(probe, gdb, catalog, issues);
    }

    private ProbeReadiness EvaluateProbe(List<StationReadinessIssue> issues)
    {
        try
        {
            var probes = _discoverProbes() ?? Array.Empty<ProbeInfo>();
            if (probes.Count == 1)
            {
                return new ProbeReadiness(ProbeReadinessStatus.Ready, probes, probes[0], null);
            }

            if (probes.Count == 0)
            {
                issues.Add(StationReadinessIssue.ProbeNotFound);
                return new ProbeReadiness(ProbeReadinessStatus.NotFound, probes, null, null);
            }

            issues.Add(StationReadinessIssue.MultipleProbes);
            return new ProbeReadiness(ProbeReadinessStatus.MultipleFound, probes, null, null);
        }
        catch (Exception ex)
        {
            issues.Add(StationReadinessIssue.ProbeDiscoveryFailed);
            return new ProbeReadiness(
                ProbeReadinessStatus.DiscoveryFailed,
                Array.Empty<ProbeInfo>(),
                null,
                ex.Message);
        }
    }

    private GdbReadiness EvaluateGdb(string? configuredPath, List<StationReadinessIssue> issues)
    {
        try
        {
            var path = _discoverGdb(configuredPath);
            if (string.IsNullOrWhiteSpace(path))
            {
                issues.Add(StationReadinessIssue.GdbNotFound);
                return new GdbReadiness(GdbReadinessStatus.NotFound, null, null);
            }

            if (!_fileExists(path))
            {
                issues.Add(StationReadinessIssue.GdbPathMissing);
                return new GdbReadiness(GdbReadinessStatus.PathMissing, path, null);
            }

            return new GdbReadiness(GdbReadinessStatus.Ready, path, null);
        }
        catch (Exception ex)
        {
            issues.Add(StationReadinessIssue.GdbDiscoveryFailed);
            return new GdbReadiness(GdbReadinessStatus.DiscoveryFailed, null, ex.Message);
        }
    }
}
