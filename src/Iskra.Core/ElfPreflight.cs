namespace FlashlightApp.Core;

/// <summary>
/// Quick sanity checks on the ELF file before handing it to gdb.
/// Fails fast with a clear error code instead of letting gdb crash on bad input.
/// </summary>
public static class ElfPreflight
{
    public enum CheckResult { Ok, NotFound, NotAnElf, IoError }

    private static readonly byte[] ElfMagic = { 0x7F, 0x45, 0x4C, 0x46 }; // 0x7F 'E' 'L' 'F'

    public static CheckResult Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return CheckResult.NotFound;
        if (!File.Exists(path)) return CheckResult.NotFound;

        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> buf = stackalloc byte[4];
            int read = fs.Read(buf);
            if (read < 4) return CheckResult.NotAnElf;
            for (int i = 0; i < 4; i++)
                if (buf[i] != ElfMagic[i]) return CheckResult.NotAnElf;
            return CheckResult.Ok;
        }
        catch (IOException)
        {
            return CheckResult.IoError;
        }
        catch (UnauthorizedAccessException)
        {
            return CheckResult.IoError;
        }
    }
}
