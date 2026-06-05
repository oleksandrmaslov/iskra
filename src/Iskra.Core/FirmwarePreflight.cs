namespace Iskra.Core;

/// <summary>
/// Fast file sanity checks before invoking gdb. These checks are intentionally
/// shallow: enough to reject the wrong file kind with a clear app error while
/// leaving full image semantics to gdb/BFD.
/// </summary>
public static class FirmwarePreflight
{
    public enum CheckResult { Ok, NotFound, InvalidFormat, IoError }

    public static CheckResult Check(string path, FirmwareKind kind)
    {
        if (string.IsNullOrWhiteSpace(path)) return CheckResult.NotFound;
        if (!File.Exists(path)) return CheckResult.NotFound;

        return kind switch
        {
            FirmwareKind.Elf => CheckElf(path),
            FirmwareKind.Hex => CheckIntelHex(path),
            _                => CheckResult.InvalidFormat,
        };
    }

    public static string DisplayName(FirmwareKind kind) => kind switch
    {
        FirmwareKind.Hex => "HEX",
        _                => "ELF",
    };

    private static CheckResult CheckElf(string path) => ElfPreflight.Check(path) switch
    {
        ElfPreflight.CheckResult.Ok       => CheckResult.Ok,
        ElfPreflight.CheckResult.NotFound => CheckResult.NotFound,
        ElfPreflight.CheckResult.IoError  => CheckResult.IoError,
        _                                 => CheckResult.InvalidFormat,
    };

    private static CheckResult CheckIntelHex(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var sawData = false;
            var sawEof = false;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                if (!TryParseHexRecord(line, out var recordType, out var byteCount))
                    return CheckResult.InvalidFormat;
                if (recordType == 0x00 && byteCount > 0) sawData = true;
                if (recordType == 0x01)
                {
                    if (byteCount != 0) return CheckResult.InvalidFormat;
                    sawEof = true;
                    break;
                }
            }
            return sawData && sawEof ? CheckResult.Ok : CheckResult.InvalidFormat;
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

    private static bool TryParseHexRecord(string line, out int recordType, out int byteCount)
    {
        recordType = 0;
        byteCount = 0;
        if (line.Length < 11 || line[0] != ':' || ((line.Length - 1) % 2) != 0)
            return false;

        var bytes = new byte[(line.Length - 1) / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!TryHexByte(line.AsSpan(1 + (i * 2), 2), out bytes[i]))
                return false;
        }

        byteCount = bytes[0];
        recordType = bytes[3];
        if (bytes.Length != 5 + byteCount)
            return false;
        if (recordType is < 0 or > 5)
            return false;

        int sum = 0;
        foreach (var b in bytes) sum = (sum + b) & 0xFF;
        return sum == 0;
    }

    private static bool TryHexByte(ReadOnlySpan<char> s, out byte value)
    {
        value = 0;
        if (s.Length != 2) return false;
        if (!TryHexNibble(s[0], out var hi) || !TryHexNibble(s[1], out var lo))
            return false;
        value = (byte)((hi << 4) | lo);
        return true;
    }

    private static bool TryHexNibble(char c, out int value)
    {
        if (c is >= '0' and <= '9') { value = c - '0'; return true; }
        if (c is >= 'a' and <= 'f') { value = c - 'a' + 10; return true; }
        if (c is >= 'A' and <= 'F') { value = c - 'A' + 10; return true; }
        value = 0;
        return false;
    }
}
