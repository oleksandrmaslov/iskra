using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Iskra.Core;

/// <summary>
/// One contiguous chunk the flasher will actually write, at its load address.
/// For ELF this is derived from allocatable, file-backed sections using their
/// containing PT_LOAD mapping. For Intel HEX it is a run of data records at
/// consecutive addresses.
/// </summary>
public sealed record FirmwareSegment(ulong Address, ulong Length)
{
    public ulong EndExclusive => Address + Length;

    public override string ToString() =>
        $"0x{Address:X8}..0x{EndExclusive - 1:X8} ({Length} bytes)";
}

/// <summary>
/// One exact ELF section expected in GDB/BFD's <c>load</c> output. Keeping the
/// name, LMA, and file-backed size lets the state machine prove that the load
/// plan observed at runtime is the plan Iskra range-checked before attaching.
/// </summary>
public sealed record FirmwareLoadSection(string Name, ulong Address, ulong Length);

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
    public IReadOnlyList<FirmwareLoadSection> LoadSections { get; init; } = [];

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
    private const int MaxSectionHeaders = 4096;
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
        var shoff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32));
        var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(42));
        var phnum = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(44));
        var shentsize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(46));
        var shnum = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(48));
        var shstrndx = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(50));

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

        var programLoads = new List<ElfProgramLoad>();
        for (var i = 0; i < phnum; i++)
        {
            var entry = table.AsSpan(i * phentsize, phentsize);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            if (type != 1) continue; // PT_LOAD only

            // gdb's `load` writes file-backed bytes to the physical address, so
            // p_filesz and p_paddr are the pair that matter. p_memsz covers .bss,
            // which is never written by the programmer.
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            ulong vaddr = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            ulong paddr = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            ulong filesz = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            ulong memsz = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]);

            if (filesz > memsz)
                return Fail(FirmwareImageStatus.Malformed, "PT_LOAD file size exceeds its memory size");
            if (offset > fs.Length || filesz > (ulong)(fs.Length - offset))
                return Fail(FirmwareImageStatus.Malformed, "PT_LOAD file bytes extend past end of file");
            if (paddr + filesz > (ulong)uint.MaxValue + 1UL)
                return Fail(FirmwareImageStatus.Malformed, "PT_LOAD address exceeds the 32-bit Cortex-M address space");
            if (vaddr + memsz > (ulong)uint.MaxValue + 1UL)
                return Fail(FirmwareImageStatus.Malformed, "PT_LOAD virtual address exceeds the 32-bit Cortex-M address space");
            programLoads.Add(new ElfProgramLoad(offset, vaddr, paddr, filesz));
        }

        if (programLoads.Count == 0)
            return Fail(FirmwareImageStatus.Empty, "ELF has no PT_LOAD program headers");

        // GDB's BFD loader iterates allocatable, file-backed sections rather
        // than blindly writing every byte covered by PT_LOAD. Derive each
        // section LMA from the exact segment mapping and reject any section for
        // which that mapping is absent or ambiguous. This closes the gap where
        // a safe-looking PT_LOAD table could hide an out-of-range SHF_ALLOC
        // section that GDB would still program.
        if (shnum == 0)
            return Fail(FirmwareImageStatus.Malformed,
                "ELF has no ordinary section table; extended/absent section counts are not accepted");
        if (shnum > MaxSectionHeaders)
            return Fail(FirmwareImageStatus.Malformed, $"implausible section header count {shnum}");
        const int minSectionEntry = 40;
        if (shentsize < minSectionEntry)
            return Fail(FirmwareImageStatus.Malformed, $"section header entry size {shentsize} is too small");
        if (shstrndx == 0 || shstrndx == ushort.MaxValue || shstrndx >= shnum)
            return Fail(FirmwareImageStatus.Malformed, "ELF section-name string table index is invalid");

        var sectionTableBytes = (long)shentsize * shnum;
        if (shoff > fs.Length || sectionTableBytes > fs.Length - shoff)
            return Fail(FirmwareImageStatus.Malformed, "section header table extends past end of file");
        var sectionTable = new byte[checked((int)sectionTableBytes)];
        fs.Position = shoff;
        if (fs.Read(sectionTable, 0, sectionTable.Length) < sectionTable.Length)
            return Fail(FirmwareImageStatus.Malformed, "section header table is truncated");

        var nameHeader = sectionTable.AsSpan(shstrndx * shentsize, shentsize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(nameHeader[4..]) != 3) // SHT_STRTAB
            return Fail(FirmwareImageStatus.Malformed, "section-name table is not SHT_STRTAB");
        var nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(nameHeader[16..]);
        var nameSize = BinaryPrimitives.ReadUInt32LittleEndian(nameHeader[20..]);
        if (nameSize == 0 || nameOffset > fs.Length || nameSize > fs.Length - nameOffset)
            return Fail(FirmwareImageStatus.Malformed, "section-name table extends past end of file");
        var sectionNames = new byte[checked((int)nameSize)];
        fs.Position = nameOffset;
        if (fs.Read(sectionNames, 0, sectionNames.Length) < sectionNames.Length)
            return Fail(FirmwareImageStatus.Malformed, "section-name table is truncated");

        const uint shtNoBits = 8;
        const uint shfAlloc = 0x2;
        var loadSections = new List<FirmwareLoadSection>();
        var segments = new List<FirmwareSegment>();
        for (var i = 1; i < shnum; i++)
        {
            var section = sectionTable.AsSpan(i * shentsize, shentsize);
            var sectionNameOffset = BinaryPrimitives.ReadUInt32LittleEndian(section);
            var sectionType = BinaryPrimitives.ReadUInt32LittleEndian(section[4..]);
            var sectionFlags = BinaryPrimitives.ReadUInt32LittleEndian(section[8..]);
            ulong sectionAddress = BinaryPrimitives.ReadUInt32LittleEndian(section[12..]);
            ulong sectionOffset = BinaryPrimitives.ReadUInt32LittleEndian(section[16..]);
            ulong sectionSize = BinaryPrimitives.ReadUInt32LittleEndian(section[20..]);

            if ((sectionFlags & shfAlloc) == 0 || sectionType == shtNoBits || sectionSize == 0)
                continue;
            if (sectionOffset > (ulong)fs.Length || sectionSize > (ulong)fs.Length - sectionOffset)
                return Fail(FirmwareImageStatus.Malformed,
                    $"allocatable section #{i} file bytes extend past end of file");
            if (!TryReadSectionName(sectionNames, sectionNameOffset, out var sectionName))
                return Fail(FirmwareImageStatus.Malformed,
                    $"allocatable section #{i} has an invalid or empty name");

            ulong? loadAddress = null;
            foreach (var load in programLoads)
            {
                if (!ContainsFileRange(load.Offset, load.FileSize, sectionOffset, sectionSize))
                    continue;

                var delta = sectionOffset - load.Offset;
                var expectedVma = load.VirtualAddress + delta;
                if (expectedVma != sectionAddress)
                    continue;
                var candidate = load.PhysicalAddress + delta;
                if (candidate + sectionSize > (ulong)uint.MaxValue + 1UL)
                    return Fail(FirmwareImageStatus.Malformed,
                        $"allocatable section '{sectionName}' exceeds the 32-bit Cortex-M address space");
                if (loadAddress is not null && loadAddress.Value != candidate)
                    return Fail(FirmwareImageStatus.Malformed,
                        $"allocatable section '{sectionName}' has ambiguous PT_LOAD mappings");
                loadAddress = candidate;
            }

            if (loadAddress is null)
                return Fail(FirmwareImageStatus.Malformed,
                    $"allocatable section '{sectionName}' is not covered by a matching PT_LOAD file/VMA mapping");

            loadSections.Add(new FirmwareLoadSection(sectionName, loadAddress.Value, sectionSize));
            segments.Add(new FirmwareSegment(loadAddress.Value, sectionSize));
        }

        return loadSections.Count == 0
            ? Fail(FirmwareImageStatus.Empty, "ELF has no allocatable file-backed sections")
            : new FirmwareImageResult(FirmwareImageStatus.Ok, Merge(segments), null)
            {
                LoadSections = loadSections.AsReadOnly(),
            };
    }

    private sealed record ElfProgramLoad(
        ulong Offset,
        ulong VirtualAddress,
        ulong PhysicalAddress,
        ulong FileSize);

    private static bool ContainsFileRange(
        ulong containerOffset,
        ulong containerLength,
        ulong itemOffset,
        ulong itemLength) =>
        itemOffset >= containerOffset
        && itemLength <= containerLength
        && itemOffset - containerOffset <= containerLength - itemLength;

    private static bool TryReadSectionName(byte[] names, uint offset, out string name)
    {
        name = string.Empty;
        if (offset >= names.Length) return false;
        var end = Array.IndexOf(names, (byte)0, checked((int)offset));
        if (end <= offset) return false;
        try
        {
            name = new System.Text.UTF8Encoding(false, true)
                .GetString(names, checked((int)offset), end - checked((int)offset));
            return !string.IsNullOrWhiteSpace(name);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
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
        while (TryReadBoundedHexLine(reader, out var line, out var lineWasTooLong))
        {
            if (++lineNumber > MaxHexLines)
                return Fail(FirmwareImageStatus.Malformed, "implausible number of HEX records");
            if (lineWasTooLong)
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

    /// <summary>
    /// Reads and drains one physical line while retaining at most the largest
    /// legal Intel HEX record. StreamReader.ReadLine() must not be used here:
    /// a hostile file can otherwise force a record-sized allocation before the
    /// length check gets a chance to reject it.
    /// </summary>
    private static bool TryReadBoundedHexLine(
        StreamReader reader,
        out string line,
        out bool wasTooLong)
    {
        var retained = new StringBuilder(MaxHexLineChars);
        var sawAnyCharacter = false;
        wasTooLong = false;

        while (true)
        {
            var raw = reader.Read();
            if (raw < 0)
            {
                line = retained.ToString();
                return sawAnyCharacter;
            }

            sawAnyCharacter = true;
            var value = (char)raw;
            if (value == '\r')
            {
                if (reader.Peek() == '\n') reader.Read();
                line = retained.ToString();
                return true;
            }

            if (value == '\n')
            {
                line = retained.ToString();
                return true;
            }

            if (retained.Length < MaxHexLineChars)
                retained.Append(value);
            else
                wasTooLong = true;
        }
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
