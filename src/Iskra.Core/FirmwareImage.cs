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
    public const long MaxFirmwareFileBytes = 64L * 1024 * 1024;
    private const int MaxProgramHeaders = 512;
    private const long MaxHexLines = 1_000_000;
    private const int MaxHexLineChars = 521;

    public static FirmwareImageResult Read(string path, FirmwareKind kind)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Fail(FirmwareImageStatus.NotFound, "firmware file not found");

        try
        {
            if (new FileInfo(path).Length > MaxFirmwareFileBytes)
                return Fail(FirmwareImageStatus.Malformed,
                    $"firmware exceeds the {MaxFirmwareFileBytes}-byte safety limit");
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

        // ARM Cortex-M images are ELF32, little-endian, EM_ARM. Accepting a
        // different architecture and merely range-checking its addresses can
        // turn a correctly signed but mislabelled build into a destructive
        // factory input.
        if (ident[4] != 1)
            return Fail(FirmwareImageStatus.Malformed, $"expected ELF32 for ARM Cortex-M (class {ident[4]})");
        if (ident[5] != 1)
            return Fail(FirmwareImageStatus.Malformed, $"expected little-endian ELF (encoding {ident[5]})");
        if (ident[6] != 1)
            return Fail(FirmwareImageStatus.Malformed, $"unsupported ELF identification version {ident[6]}");

        const int headerSize = 52;
        var header = new byte[headerSize];
        fs.Position = 0;
        if (fs.Read(header, 0, headerSize) < headerSize)
            return Fail(FirmwareImageStatus.Malformed, "file is shorter than its ELF header");

        var elfType = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(16));
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18));
        if (elfType is not (2 or 3))
            return Fail(FirmwareImageStatus.Malformed, $"ELF type {elfType} is not executable or position-independent");
        if (machine != 40)
            return Fail(FirmwareImageStatus.Malformed, $"ELF machine {machine} is not ARM (EM_ARM=40)");

        var phoff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28));
        var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(42));
        var phnum = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(44));

        if (phnum == 0)
            return Fail(FirmwareImageStatus.Empty, "ELF has no program headers");
        if (phnum > MaxProgramHeaders)
            return Fail(FirmwareImageStatus.Malformed, $"implausible program header count {phnum}");

        const int minEntry = 32;
        if (phentsize < minEntry)
            return Fail(FirmwareImageStatus.Malformed, $"program header entry size {phentsize} is too small");

        var tableBytes = (long)phentsize * phnum;
        if (phoff > fs.Length || tableBytes > fs.Length - phoff)
            return Fail(FirmwareImageStatus.Malformed, "program header table extends past end of file");

        var table = new byte[checked((int)tableBytes)];
        fs.Position = phoff;
        if (fs.Read(table, 0, table.Length) < table.Length)
            return Fail(FirmwareImageStatus.Malformed, "program header table is truncated");

        var segments = new List<FirmwareSegment>();
        for (var i = 0; i < phnum; i++)
        {
            var entry = table.AsSpan(i * phentsize, phentsize);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (type != 1) continue; // PT_LOAD only

            // gdb's `load` writes file-backed bytes to the physical address, so
            // p_filesz and p_paddr are the pair that matter. p_memsz covers .bss,
            // which is never written by the programmer.
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            ulong paddr = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            ulong filesz = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            ulong memsz = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);

            if (filesz == 0) continue;
            if (filesz > memsz)
                return Fail(FirmwareImageStatus.Malformed, "PT_LOAD file size exceeds its memory size");
            if (offset > fs.Length || filesz > (ulong)(fs.Length - offset))
                return Fail(FirmwareImageStatus.Malformed, "PT_LOAD file bytes extend past end of file");
            if (paddr + filesz > (ulong)uint.MaxValue + 1UL)
                return Fail(FirmwareImageStatus.Malformed, "PT_LOAD address exceeds the 32-bit Cortex-M address space");
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
            if (line.Length > MaxHexLineChars)
                return Fail(FirmwareImageStatus.Malformed,
                    $"line {lineNumber} exceeds the Intel HEX record limit");

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
                    if (count > 0)
                    {
                        var address = upperBase + offset;
                        if (address > uint.MaxValue
                            || (ulong)count > (ulong)uint.MaxValue + 1UL - address)
                        {
                            return Fail(FirmwareImageStatus.Malformed,
                                $"line {lineNumber}: data record exceeds the 32-bit Cortex-M address space");
                        }
                        segments.Add(new FirmwareSegment(address, (ulong)count));
                    }
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

}
