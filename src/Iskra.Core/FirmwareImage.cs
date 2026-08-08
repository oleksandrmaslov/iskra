using System.Buffers.Binary;
using System.Globalization;

namespace Iskra.Core;

/// <summary>
/// One contiguous chunk the flasher will actually write, at its load address.
/// For ELF this is a PT_LOAD segment's physical address (LMA) and file size —
/// not the virtual address, because gdb's <c>load</c> writes to the LMA. For
/// Intel HEX it is a run of data records at consecutive addresses.
/// </summary>
public sealed record FirmwareSegment(ulong Address, ulong Length)
{
    public ulong EndExclusive => Address + Length;

    public override string ToString() =>
        $"0x{Address:X8}..0x{EndExclusive - 1:X8} ({Length} bytes)";
}

public enum FirmwareImageStatus
{
    Ok,
    NotFound,
    IoError,
    /// <summary>The file is not the declared kind, or its headers are unusable.</summary>
    Malformed,
    /// <summary>Well-formed but carries nothing to write.</summary>
    Empty,
}

public sealed record FirmwareImageResult(
    FirmwareImageStatus Status,
    IReadOnlyList<FirmwareSegment> Segments,
    string? Diagnostic)
{
    public bool IsOk => Status == FirmwareImageStatus.Ok;

    public ulong TotalBytes
    {
        get
        {
            ulong total = 0;
            foreach (var s in Segments) total += s.Length;
            return total;
        }
    }
}

/// <summary>
/// Extracts the load map from a firmware file so it can be checked against the
/// catalog's declared target memory before anything is written to a device.
///
/// <para>This is deliberately a reader, not a validator: it reports what the
/// image claims it will occupy. <see cref="FirmwareRangeCheck"/> decides whether
/// that is acceptable for a given target.</para>
/// </summary>
public static class FirmwareImage
{
    // Guard rails against a hostile or corrupt header table claiming absurd
    // counts. A Cortex-M image has a handful of loadable segments.
    private const int MaxProgramHeaders = 512;
    private const long MaxHexLines = 20_000_000;

    public static FirmwareImageResult Read(string path, FirmwareKind kind)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Fail(FirmwareImageStatus.NotFound, "firmware file not found");

        try
        {
            return kind switch
            {
                FirmwareKind.Hex => ReadIntelHex(path),
                _ => ReadElf(path),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(FirmwareImageStatus.IoError, ex.Message);
        }
    }

    // ============================================================
    // ELF
    // ============================================================

    private static FirmwareImageResult ReadElf(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> ident = stackalloc byte[16];
        if (fs.Read(ident) < 16)
            return Fail(FirmwareImageStatus.Malformed, "file is shorter than an ELF identification header");
        if (ident[0] != 0x7F || ident[1] != (byte)'E' || ident[2] != (byte)'L' || ident[3] != (byte)'F')
            return Fail(FirmwareImageStatus.Malformed, "missing ELF magic");

        var is64 = ident[4] == 2;      // EI_CLASS: 1 = ELF32, 2 = ELF64
        var isBig = ident[5] == 2;     // EI_DATA:  1 = LSB,   2 = MSB
        if (ident[4] is not (1 or 2))
            return Fail(FirmwareImageStatus.Malformed, $"unsupported ELF class {ident[4]}");
        if (ident[5] is not (1 or 2))
            return Fail(FirmwareImageStatus.Malformed, $"unsupported ELF data encoding {ident[5]}");

        // e_phoff / e_phentsize / e_phnum live at fixed offsets that differ
        // between ELF32 and ELF64.
        var headerSize = is64 ? 64 : 52;
        var header = new byte[headerSize];
        fs.Position = 0;
        if (fs.Read(header, 0, headerSize) < headerSize)
            return Fail(FirmwareImageStatus.Malformed, "file is shorter than its ELF header");

        ulong phoff;
        int phentsize, phnum;
        if (is64)
        {
            phoff = ReadU64(header.AsSpan(32), isBig);
            phentsize = ReadU16(header.AsSpan(54), isBig);
            phnum = ReadU16(header.AsSpan(56), isBig);
        }
        else
        {
            phoff = ReadU32(header.AsSpan(28), isBig);
            phentsize = ReadU16(header.AsSpan(42), isBig);
            phnum = ReadU16(header.AsSpan(44), isBig);
        }

        if (phnum == 0)
            return Fail(FirmwareImageStatus.Empty, "ELF has no program headers");
        if (phnum > MaxProgramHeaders)
            return Fail(FirmwareImageStatus.Malformed, $"implausible program header count {phnum}");

        var minEntry = is64 ? 56 : 32;
        if (phentsize < minEntry)
            return Fail(FirmwareImageStatus.Malformed, $"program header entry size {phentsize} is too small");

        var tableBytes = (long)phentsize * phnum;
        if (phoff > (ulong)long.MaxValue || (long)phoff + tableBytes > fs.Length)
            return Fail(FirmwareImageStatus.Malformed, "program header table extends past end of file");

        var table = new byte[tableBytes];
        fs.Position = (long)phoff;
        if (fs.Read(table, 0, (int)tableBytes) < tableBytes)
            return Fail(FirmwareImageStatus.Malformed, "program header table is truncated");

        var segments = new List<FirmwareSegment>();
        for (var i = 0; i < phnum; i++)
        {
            var entry = table.AsSpan(i * phentsize, phentsize);
            var type = ReadU32(entry, isBig);
            if (type != 1) continue; // PT_LOAD only

            // gdb's `load` writes file-backed bytes to the physical address, so
            // p_filesz and p_paddr are the pair that matter. p_memsz covers .bss,
            // which is never written by the programmer.
            ulong paddr, filesz;
            if (is64)
            {
                paddr = ReadU64(entry[16..], isBig);
                filesz = ReadU64(entry[32..], isBig);
            }
            else
            {
                paddr = ReadU32(entry[12..], isBig);
                filesz = ReadU32(entry[16..], isBig);
            }

            if (filesz == 0) continue;
            if (paddr > ulong.MaxValue - filesz)
                return Fail(FirmwareImageStatus.Malformed, "segment address overflows the address space");
            segments.Add(new FirmwareSegment(paddr, filesz));
        }

        return segments.Count == 0
            ? Fail(FirmwareImageStatus.Empty, "ELF has no loadable segments with file-backed content")
            : new FirmwareImageResult(FirmwareImageStatus.Ok, Merge(segments), null);
    }

    // ============================================================
    // Intel HEX
    // ============================================================

    private static FirmwareImageResult ReadIntelHex(string path)
    {
        var segments = new List<FirmwareSegment>();
        ulong upperBase = 0;
        var sawEof = false;
        long lineNumber = 0;

        using var reader = new StreamReader(path);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (++lineNumber > MaxHexLines)
                return Fail(FirmwareImageStatus.Malformed, "implausible number of HEX records");

            line = line.Trim();
            if (line.Length == 0) continue;
            if (sawEof)
                return Fail(FirmwareImageStatus.Malformed, $"record after EOF at line {lineNumber}");
            if (line[0] != ':')
                return Fail(FirmwareImageStatus.Malformed, $"line {lineNumber} does not start with ':'");

            var body = line[1..];
            if (body.Length < 10 || body.Length % 2 != 0)
                return Fail(FirmwareImageStatus.Malformed, $"line {lineNumber} has a bad length");
            if (!TryParseHexBytes(body, out var bytes))
                return Fail(FirmwareImageStatus.Malformed, $"line {lineNumber} has non-hex characters");

            int count = bytes[0];
            if (bytes.Length != count + 5)
                return Fail(FirmwareImageStatus.Malformed, $"line {lineNumber} byte count does not match payload");

            byte sum = 0;
            foreach (var b in bytes) sum += b;
            if (sum != 0)
                return Fail(FirmwareImageStatus.Malformed, $"line {lineNumber} checksum is invalid");

            var offset = (ulong)((bytes[1] << 8) | bytes[2]);
            var recordType = bytes[3];

            switch (recordType)
            {
                case 0x00: // data
                    if (count > 0) segments.Add(new FirmwareSegment(upperBase + offset, (ulong)count));
                    break;
                case 0x01: // end of file
                    sawEof = true;
                    break;
                case 0x02: // extended segment address (x16 paragraph)
                    if (count != 2)
                        return Fail(FirmwareImageStatus.Malformed, $"line {lineNumber}: bad extended segment record");
                    upperBase = (ulong)((bytes[4] << 8) | bytes[5]) * 16UL;
                    break;
                case 0x04: // extended linear address (upper 16 bits)
                    if (count != 2)
                        return Fail(FirmwareImageStatus.Malformed, $"line {lineNumber}: bad extended linear record");
                    upperBase = (ulong)((bytes[4] << 8) | bytes[5]) << 16;
                    break;
                case 0x03:
                case 0x05:
                    break; // start address records carry no payload to write
                default:
                    return Fail(FirmwareImageStatus.Malformed,
                        $"line {lineNumber}: unsupported record type 0x{recordType:X2}");
            }
        }

        if (!sawEof)
            return Fail(FirmwareImageStatus.Malformed, "HEX file has no EOF record");

        return segments.Count == 0
            ? Fail(FirmwareImageStatus.Empty, "HEX file contains no data records")
            : new FirmwareImageResult(FirmwareImageStatus.Ok, Merge(segments), null);
    }

    private static bool TryParseHexBytes(string body, out byte[] bytes)
    {
        bytes = new byte[body.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(
                    body.AsSpan(i * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out bytes[i]))
            {
                return false;
            }
        }

        return true;
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>
    /// Coalesces adjacent and overlapping chunks so a HEX file's thousands of
    /// 16-byte records become the handful of real regions an operator can read
    /// in an error message.
    /// </summary>
    private static IReadOnlyList<FirmwareSegment> Merge(List<FirmwareSegment> segments)
    {
        segments.Sort((a, b) => a.Address.CompareTo(b.Address));
        var merged = new List<FirmwareSegment>(segments.Count);
        var current = segments[0];

        for (var i = 1; i < segments.Count; i++)
        {
            var next = segments[i];
            if (next.Address <= current.EndExclusive)
            {
                var end = Math.Max(current.EndExclusive, next.EndExclusive);
                current = new FirmwareSegment(current.Address, end - current.Address);
            }
            else
            {
                merged.Add(current);
                current = next;
            }
        }

        merged.Add(current);
        return merged;
    }

    private static FirmwareImageResult Fail(FirmwareImageStatus status, string diagnostic) =>
        new(status, Array.Empty<FirmwareSegment>(), diagnostic);

    private static ushort ReadU16(ReadOnlySpan<byte> s, bool big) => big
        ? BinaryPrimitives.ReadUInt16BigEndian(s)
        : BinaryPrimitives.ReadUInt16LittleEndian(s);

    private static uint ReadU32(ReadOnlySpan<byte> s, bool big) => big
        ? BinaryPrimitives.ReadUInt32BigEndian(s)
        : BinaryPrimitives.ReadUInt32LittleEndian(s);

    private static ulong ReadU64(ReadOnlySpan<byte> s, bool big) => big
        ? BinaryPrimitives.ReadUInt64BigEndian(s)
        : BinaryPrimitives.ReadUInt64LittleEndian(s);
}
