using FlashlightApp.Core;

namespace FlashlightApp.Core.Tests;

public class ElfPreflightTests
{
    [Fact]
    public void NotFound_when_path_does_not_exist()
    {
        var r = ElfPreflight.Check(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.elf"));
        Assert.Equal(ElfPreflight.CheckResult.NotFound, r);
    }

    [Fact]
    public void NotFound_when_path_is_empty()
    {
        Assert.Equal(ElfPreflight.CheckResult.NotFound, ElfPreflight.Check(""));
    }

    [Fact]
    public void NotAnElf_when_file_lacks_magic()
    {
        var path = Path.Combine(Path.GetTempPath(), $"not-elf-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }); // PE/MZ magic
            Assert.Equal(ElfPreflight.CheckResult.NotAnElf, ElfPreflight.Check(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Ok_when_file_starts_with_elf_magic()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fake-elf-{Guid.NewGuid():N}.elf");
        try
        {
            File.WriteAllBytes(path, new byte[]
            {
                0x7F, 0x45, 0x4C, 0x46, // ELF magic
                0x01, 0x01, 0x01, 0x00, // ident remainder (irrelevant for magic check)
            });
            Assert.Equal(ElfPreflight.CheckResult.Ok, ElfPreflight.Check(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void NotAnElf_when_file_is_truncated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"trunc-{Guid.NewGuid():N}.elf");
        try
        {
            File.WriteAllBytes(path, new byte[] { 0x7F, 0x45 });
            Assert.Equal(ElfPreflight.CheckResult.NotAnElf, ElfPreflight.Check(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
