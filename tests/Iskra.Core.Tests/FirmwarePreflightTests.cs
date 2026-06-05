using Iskra.Core;

namespace Iskra.Core.Tests;

public class FirmwarePreflightTests
{
    [Fact]
    public void Hex_ok_when_records_and_checksum_are_valid()
    {
        var path = Path.Combine(Path.GetTempPath(), $"valid-{Guid.NewGuid():N}.hex");
        try
        {
            File.WriteAllText(path, ":10010000214601360121470136007EFE09D2190140\n:00000001FF\n");
            Assert.Equal(FirmwarePreflight.CheckResult.Ok,
                FirmwarePreflight.Check(path, FirmwareKind.Hex));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Hex_invalid_when_checksum_is_wrong()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.hex");
        try
        {
            File.WriteAllText(path, ":10010000214601360121470136007EFE09D2190141\n:00000001FF\n");
            Assert.Equal(FirmwarePreflight.CheckResult.InvalidFormat,
                FirmwarePreflight.Check(path, FirmwareKind.Hex));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Elf_path_delegates_to_existing_magic_check()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-{Guid.NewGuid():N}.elf");
        try
        {
            File.WriteAllBytes(path, new byte[] { 0x7F, 0x45, 0x4C, 0x46 });
            Assert.Equal(FirmwarePreflight.CheckResult.Ok,
                FirmwarePreflight.Check(path, FirmwareKind.Elf));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
