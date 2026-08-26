using System.Runtime.InteropServices;

namespace Iskra.Core.Tests;

public sealed class GdbDiscoveryTests
{
    [Fact]
    public void Linux_prefers_cross_gdb_then_accepts_distribution_multiarch_gdb()
    {
        Assert.Equal(
            ["arm-none-eabi-gdb", "gdb-multiarch"],
            GdbDiscovery.ExecutableNamesFor(OSPlatform.Linux));
    }

    [Fact]
    public void Windows_and_macos_keep_platform_specific_cross_gdb_names()
    {
        Assert.Equal(
            ["arm-none-eabi-gdb.exe"],
            GdbDiscovery.ExecutableNamesFor(OSPlatform.Windows));
        Assert.Equal(
            ["arm-none-eabi-gdb"],
            GdbDiscovery.ExecutableNamesFor(OSPlatform.OSX));
    }
}
