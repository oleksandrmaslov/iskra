using Iskra.Core;

namespace Iskra.Core.Tests;

public class ProbeDiscoveryTests
{
    [Theory]
    [InlineData("Black Magic GDB Server (COM30)", ProbeInterface.Gdb)]
    [InlineData("Black Magic UART Port (COM31)",  ProbeInterface.Uart)]
    [InlineData("BMP gdb interface",              ProbeInterface.Gdb)]
    [InlineData("Some other serial device",       ProbeInterface.Unknown)]
    [InlineData("",                               ProbeInterface.Unknown)]
    [InlineData(null,                             ProbeInterface.Unknown)]
    public void ClassifyInterface_routes_by_keyword(string? friendly, ProbeInterface expected)
    {
        Assert.Equal(expected, ProbeDiscovery.ClassifyInterface(friendly));
    }

    [Theory]
    [InlineData("Устройство с последовательным интерфейсом USB (COM30)", "VID_1D50&PID_6018&MI_00/6&abc&0&0000", ProbeInterface.Gdb)]
    [InlineData("USB Serial Device (COM31)", "VID_1D50&PID_6018&MI_02/6&abc&0&0002", ProbeInterface.Uart)]
    [InlineData("Black Magic GDB Server (COM30)", "VID_1D50&PID_6018&MI_02/6&abc&0&0002", ProbeInterface.Gdb)]
    [InlineData("USB Serial Device (COM32)", "VID_1D50&PID_6018&MI_04/6&abc&0&0004", ProbeInterface.Unknown)]
    public void ClassifyInterface_uses_usb_interface_number_when_name_is_generic(
        string? friendly,
        string? deviceInstanceId,
        ProbeInterface expected)
    {
        Assert.Equal(expected, ProbeDiscovery.ClassifyInterface(friendly, deviceInstanceId));
    }

    [Theory]
    [InlineData("COM30", 30)]
    [InlineData("COM3",  3)]
    [InlineData("COM",   int.MaxValue)]
    [InlineData("",      int.MaxValue)]
    public void ParseComNumber_extracts_trailing_digits(string port, int expected)
    {
        Assert.Equal(expected, ProbeDiscovery.ParseComNumber(port));
    }

    [Theory]
    [InlineData("BMP123456", null, "BMP123456")]
    [InlineData("6&2b8a123&0&0000", "6&2b8a123&0", "6&2b8a123&0")]
    [InlineData("", "parent-prefix", "parent-prefix")]
    [InlineData(null, null, null)]
    public void StableSerialFromInstanceName_prefers_real_usb_serial_then_parent_prefix(
        string? instanceName,
        string? parentPrefix,
        string? expected)
    {
        Assert.Equal(expected, ProbeDiscovery.StableSerialFromInstanceName(instanceName, parentPrefix));
    }

    [Fact]
    public void FindAll_returns_empty_or_real_list_without_throwing()
    {
        // On Windows we may or may not have BMP plugged in; on non-Windows we get empty.
        var result = ProbeDiscovery.FindAll();
        Assert.NotNull(result);
    }
}
