using FlashlightApp.Core;

namespace FlashlightApp.Core.Tests;

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
    [InlineData("COM30", 30)]
    [InlineData("COM3",  3)]
    [InlineData("COM",   int.MaxValue)]
    [InlineData("",      int.MaxValue)]
    public void ParseComNumber_extracts_trailing_digits(string port, int expected)
    {
        Assert.Equal(expected, ProbeDiscovery.ParseComNumber(port));
    }

    [Fact]
    public void FindAll_returns_empty_or_real_list_without_throwing()
    {
        // On Windows we may or may not have BMP plugged in; on non-Windows we get empty.
        var result = ProbeDiscovery.FindAll();
        Assert.NotNull(result);
    }
}
