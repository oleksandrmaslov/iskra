using Iskra.Core;

namespace Iskra.Core.Tests;

/// <summary>
/// macOS probe discovery, driven through an injected /dev root so it runs on
/// any build host. This covers the naming convention only; it is not a
/// substitute for plugging a probe into a real Mac.
/// </summary>
public sealed class MacOsProbeDiscoveryTests : IDisposable
{
    private readonly string _dev;

    public MacOsProbeDiscoveryTests()
    {
        _dev = Path.Combine(Path.GetTempPath(), $"iskra-dev-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dev);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dev, recursive: true); } catch { /* best effort */ }
    }

    private void Node(string name) => File.WriteAllText(Path.Combine(_dev, name), string.Empty);

    [Fact]
    public void A_missing_dev_root_yields_no_probes_instead_of_throwing()
    {
        var probes = ProbeDiscovery.FindMacOs(Path.Combine(_dev, "does-not-exist"));

        Assert.Empty(probes);
    }

    [Fact]
    public void Interface_suffix_one_is_gdb_and_three_is_uart()
    {
        // Black Magic Probe, serial E4D2A1C3: interface 0 -> …1, interface 2 -> …3.
        Node("cu.usbmodemE4D2A1C31");
        Node("cu.usbmodemE4D2A1C33");

        var probes = ProbeDiscovery.FindMacOs(_dev);

        Assert.Equal(2, probes.Count);
        var gdb = Assert.Single(probes, p => p.Interface == ProbeInterface.Gdb);
        var uart = Assert.Single(probes, p => p.Interface == ProbeInterface.Uart);
        Assert.EndsWith("cu.usbmodemE4D2A1C31", gdb.PortName);
        Assert.EndsWith("cu.usbmodemE4D2A1C33", uart.PortName);
        Assert.Equal("E4D2A1C3", gdb.SerialNumber);
        Assert.Equal("E4D2A1C3", uart.SerialNumber);
    }

    [Fact]
    public void Only_the_gdb_interface_is_offered_for_flashing()
    {
        Node("cu.usbmodemE4D2A1C31");
        Node("cu.usbmodemE4D2A1C33");

        var probes = ProbeDiscovery.FindMacOs(_dev);
        var gdbOnly = probes.Where(p => p.Interface == ProbeInterface.Gdb).ToList();

        // The UART endpoint must never be handed to gdb as a target.
        Assert.Single(gdbOnly);
        Assert.EndsWith("1", gdbOnly[0].PortName);
    }

    [Fact]
    public void Two_probes_are_both_reported_so_readiness_can_block()
    {
        // Exactly-one-probe readiness depends on seeing both, not on picking one.
        Node("cu.usbmodemAAAA1");
        Node("cu.usbmodemAAAA3");
        Node("cu.usbmodemBBBB1");
        Node("cu.usbmodemBBBB3");

        var probes = ProbeDiscovery.FindMacOs(_dev);

        Assert.Equal(2, probes.Count(p => p.Interface == ProbeInterface.Gdb));
    }

    [Fact]
    public void Unrelated_serial_devices_are_ignored()
    {
        Node("cu.Bluetooth-Incoming-Port");
        Node("tty.usbmodemE4D2A1C31");
        Node("cu.usbserial-1420");

        var probes = ProbeDiscovery.FindMacOs(_dev);

        Assert.Empty(probes);
    }

    [Fact]
    public void An_unexpected_suffix_is_reported_as_unknown_not_as_gdb()
    {
        // Guessing GDB here would risk handing gdb a UART or unrelated endpoint.
        Node("cu.usbmodem14202");

        var probe = Assert.Single(ProbeDiscovery.FindMacOs(_dev));

        Assert.Equal(ProbeInterface.Unknown, probe.Interface);
    }
}
