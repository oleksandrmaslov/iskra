using Iskra.Application;
using Iskra.Core;

namespace Iskra.Application.Tests;

public sealed class StationReadinessServiceTests
{
    private static readonly Catalog ReadyCatalog = new(
        SchemaVersion: 1,
        GeneratedAt: DateTime.UnixEpoch,
        Products: Array.Empty<Product>());

    [Fact]
    public void Exactly_one_probe_existing_gdb_and_ready_catalog_pass()
    {
        var service = NewService(probes: [Probe("COM7")]);

        var result = service.Evaluate(new AppSettings());

        Assert.True(result.IsReady);
        Assert.Equal(ProbeReadinessStatus.Ready, result.Probe.Status);
        Assert.Equal("COM7", result.Probe.Selected!.PortName);
        Assert.Equal(GdbReadinessStatus.Ready, result.Gdb.Status);
        Assert.True(result.Catalog.IsReady);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void No_probe_blocks_readiness()
    {
        var result = NewService(probes: []).Evaluate(new AppSettings());

        Assert.False(result.IsReady);
        Assert.Equal(ProbeReadinessStatus.NotFound, result.Probe.Status);
        Assert.Contains(StationReadinessIssue.ProbeNotFound, result.Issues);
    }

    [Fact]
    public void Multiple_probes_block_readiness_and_none_is_selected()
    {
        var result = NewService(probes: [Probe("COM7"), Probe("COM9")])
            .Evaluate(new AppSettings());

        Assert.False(result.IsReady);
        Assert.Equal(ProbeReadinessStatus.MultipleFound, result.Probe.Status);
        Assert.Null(result.Probe.Selected);
        Assert.Equal(2, result.Probe.Discovered.Count);
        Assert.Contains(StationReadinessIssue.MultipleProbes, result.Issues);
    }

    [Fact]
    public void Probe_discovery_exception_is_a_blocked_state()
    {
        var service = NewService(
            probeDiscovery: () => throw new IOException("registry unavailable"));

        var result = service.Evaluate(new AppSettings());

        Assert.False(result.IsReady);
        Assert.Equal(ProbeReadinessStatus.DiscoveryFailed, result.Probe.Status);
        Assert.Equal("registry unavailable", result.Probe.Diagnostic);
        Assert.Contains(StationReadinessIssue.ProbeDiscoveryFailed, result.Issues);
    }

    [Fact]
    public void Missing_gdb_blocks_readiness()
    {
        var result = NewService(probes: [Probe("COM7")], gdbPath: null)
            .Evaluate(new AppSettings());

        Assert.False(result.IsReady);
        Assert.Equal(GdbReadinessStatus.NotFound, result.Gdb.Status);
        Assert.Contains(StationReadinessIssue.GdbNotFound, result.Issues);
    }

    [Fact]
    public void Stale_gdb_path_blocks_readiness_even_if_discovery_returns_it()
    {
        var result = NewService(
                probes: [Probe("COM7")],
                gdbPath: "/toolchain/gdb",
                gdbFileExists: false)
            .Evaluate(new AppSettings());

        Assert.False(result.IsReady);
        Assert.Equal(GdbReadinessStatus.PathMissing, result.Gdb.Status);
        Assert.Contains(StationReadinessIssue.GdbPathMissing, result.Issues);
    }

    [Fact]
    public void Catalog_failure_blocks_readiness()
    {
        var catalog = new StubCatalogSession(new CatalogSessionResult(
            CatalogSessionStatus.TrustRejected,
            null,
            "catalog.json",
            null,
            CatalogTrustResult.BadSignature,
            false,
            "bad signature"));
        var service = NewService(probes: [Probe("COM7")], catalogSession: catalog);

        var result = service.Evaluate(new AppSettings());

        Assert.False(result.IsReady);
        Assert.Equal(CatalogSessionStatus.TrustRejected, result.Catalog.Status);
        Assert.Contains(StationReadinessIssue.CatalogNotReady, result.Issues);
    }

    [Fact]
    public void Catalog_session_exception_is_converted_to_blocked_result()
    {
        var service = NewService(
            probes: [Probe("COM7")],
            catalogSession: new ThrowingCatalogSession());

        var result = service.Evaluate(new AppSettings());

        Assert.False(result.IsReady);
        Assert.Equal(CatalogSessionStatus.UnexpectedError, result.Catalog.Status);
        Assert.Equal("catalog exploded", result.Catalog.Diagnostic);
        Assert.Contains(StationReadinessIssue.CatalogNotReady, result.Issues);
    }

    private static StationReadinessService NewService(
        IReadOnlyList<ProbeInfo>? probes = null,
        Func<IReadOnlyList<ProbeInfo>>? probeDiscovery = null,
        string? gdbPath = "/toolchain/arm-none-eabi-gdb",
        bool gdbFileExists = true,
        ICatalogSession? catalogSession = null)
    {
        catalogSession ??= new StubCatalogSession(ReadyCatalogResult());
        return new StationReadinessService(
            catalogSession,
            discoverProbes: probeDiscovery ?? (() => probes ?? Array.Empty<ProbeInfo>()),
            discoverGdb: _ => gdbPath,
            fileExists: _ => gdbFileExists);
    }

    private static ProbeInfo Probe(string port) => new(
        PortName: port,
        FriendlyName: "Black Magic Probe GDB",
        DeviceInstanceId: $"device-{port}",
        Interface: ProbeInterface.Gdb,
        SerialNumber: $"serial-{port}");

    private static CatalogSessionResult ReadyCatalogResult() => new(
        CatalogSessionStatus.Ready,
        ReadyCatalog,
        "catalog.json",
        ".",
        CatalogTrustResult.Verified,
        false,
        null);

    private sealed class StubCatalogSession : ICatalogSession
    {
        private readonly CatalogSessionResult _result;

        public StubCatalogSession(CatalogSessionResult result)
        {
            _result = result;
            Current = result;
        }

        public CatalogSessionResult Current { get; private set; }

        public CatalogSessionResult Load(AppSettings settings)
        {
            Current = _result;
            return _result;
        }
    }

    private sealed class ThrowingCatalogSession : ICatalogSession
    {
        public CatalogSessionResult Current => throw new InvalidOperationException("not available");

        public CatalogSessionResult Load(AppSettings settings) =>
            throw new InvalidOperationException("catalog exploded");
    }
}
