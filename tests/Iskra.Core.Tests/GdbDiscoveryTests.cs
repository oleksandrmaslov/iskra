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

    [Theory]
    [InlineData("/usr/bin/arm-none-eabi-gdb", true)]
    [InlineData("/bin/gdb-multiarch", true)]
    [InlineData("/usr/binary/arm-none-eabi-gdb", false)]
    [InlineData("/usr/local/bin/arm-none-eabi-gdb", false)]
    [InlineData("/home/operator/bin/arm-none-eabi-gdb", false)]
    [InlineData("/usr/bin/../tmp/arm-none-eabi-gdb", false)]
    public void Linux_trusts_only_package_managed_system_locations(string path, bool expected)
    {
        Assert.Equal(expected, GdbDiscovery.IsTrustedLocation(path, OSPlatform.Linux));
    }

    [Theory]
    [InlineData("/Applications/ArmGNUToolchain/15.2.rel1/arm-none-eabi/bin/arm-none-eabi-gdb", true)]
    [InlineData("/Library/ArmGNUToolchain/bin/arm-none-eabi-gdb", true)]
    [InlineData("/opt/homebrew/bin/arm-none-eabi-gdb", false)]
    [InlineData("/Users/operator/bin/arm-none-eabi-gdb", false)]
    public void Macos_trusts_admin_installed_cask_location_not_user_prefixes(string path, bool expected)
    {
        Assert.Equal(expected, GdbDiscovery.IsTrustedLocation(path, OSPlatform.OSX));
    }

    [Fact]
    public void Root_matching_requires_a_path_component_boundary()
    {
        Assert.True(GdbDiscovery.IsWithinRoot(
            "/usr/bin/arm-none-eabi-gdb", "/usr/bin", StringComparison.Ordinal));
        Assert.False(GdbDiscovery.IsWithinRoot(
            "/usr/binary/arm-none-eabi-gdb", "/usr/bin", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_records_sha256_even_when_version_probe_is_not_executable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iskra-gdb-evidence-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllText(path, "not an executable");
            var evidence = GdbDiscovery.Inspect(path, TimeSpan.FromMilliseconds(100));

            Assert.Equal(FirmwareIntegrity.ComputeSha256Hex(path), evidence.Sha256);
            Assert.Equal(Path.GetFullPath(path), evidence.LauncherPath);
            Assert.False(evidence.IsTrustedLocation);
            Assert.Null(evidence.VersionBanner);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
