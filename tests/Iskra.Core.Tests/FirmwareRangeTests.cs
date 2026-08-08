using System.Buffers.Binary;
using System.Text;
using Iskra.Core;

namespace Iskra.Core.Tests;

/// <summary>
/// Covers the load-map reader and the range policy that refuses firmware which
/// cannot belong to the catalog-declared target. BMP reports only an MCU family,
/// so these checks are the app's only defence against flashing a build meant for
/// a different part in the same family.
/// </summary>
public sealed class FirmwareRangeTests : IDisposable
{
    private readonly List<string> _temp = [];

    public void Dispose()
    {
        foreach (var path in _temp)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }
    }

    // ============================================================
    // ELF reading
    // ============================================================

    [Fact]
    public void Reads_pt_load_segments_at_their_physical_address()
    {
        var path = WriteElf32([(0x08000000u, 0x1000u), (0x08001000u, 0x0200u)]);

        var image = FirmwareImage.Read(path, FirmwareKind.Elf);

        Assert.Equal(FirmwareImageStatus.Ok, image.Status);
        // Adjacent segments coalesce into one readable region.
        var segment = Assert.Single(image.Segments);
        Assert.Equal(0x08000000u, segment.Address);
        Assert.Equal(0x1200u, segment.Length);
        Assert.Equal(0x1200u, image.TotalBytes);
    }

    [Fact]
    public void Skips_non_loadable_and_zero_length_segments()
    {
        // A PT_LOAD with filesz 0 is .bss: allocated on the device, never written
        // by the programmer, so it must not count against flash.
        var path = WriteElf32([(0x08000000u, 0x100u), (0x20000000u, 0x0u)]);

        var image = FirmwareImage.Read(path, FirmwareKind.Elf);

        Assert.Equal(FirmwareImageStatus.Ok, image.Status);
        Assert.Equal(0x100u, image.TotalBytes);
        Assert.Equal(0x08000000u, Assert.Single(image.Segments).Address);
    }

    [Fact]
    public void Rejects_a_file_that_is_not_an_elf()
    {
        var path = NewTempFile(".elf");
        File.WriteAllText(path, "definitely not an ELF");

        var image = FirmwareImage.Read(path, FirmwareKind.Elf);

        Assert.Equal(FirmwareImageStatus.Malformed, image.Status);
    }

    [Fact]
    public void Rejects_an_elf_whose_program_header_table_runs_past_the_file()
    {
        var path = WriteElf32([(0x08000000u, 0x100u)]);
        var bytes = File.ReadAllBytes(path);
        // Claim 500 program headers in a file that holds one.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(44), 500);
        File.WriteAllBytes(path, bytes);

        var image = FirmwareImage.Read(path, FirmwareKind.Elf);

        Assert.Equal(FirmwareImageStatus.Malformed, image.Status);
    }

    [Fact]
    public void Reports_missing_file_without_throwing()
    {
        var image = FirmwareImage.Read(Path.Combine(Path.GetTempPath(), "no-such-iskra.elf"), FirmwareKind.Elf);

        Assert.Equal(FirmwareImageStatus.NotFound, image.Status);
    }

    // ============================================================
    // Intel HEX reading
    // ============================================================

    [Fact]
    public void Reads_hex_data_records_and_honors_extended_linear_addresses()
    {
        var hex = new StringBuilder();
        hex.AppendLine(HexRecord(0x0000, 0x04, [0x08, 0x00]));      // upper base 0x0800
        hex.AppendLine(HexRecord(0x0000, 0x00, new byte[16]));      // 0x08000000
        hex.AppendLine(HexRecord(0x0010, 0x00, new byte[16]));      // 0x08000010
        hex.AppendLine(HexRecord(0x0000, 0x01, []));               // EOF
        var path = NewTempFile(".hex");
        File.WriteAllText(path, hex.ToString());

        var image = FirmwareImage.Read(path, FirmwareKind.Hex);

        Assert.Equal(FirmwareImageStatus.Ok, image.Status);
        var segment = Assert.Single(image.Segments);
        Assert.Equal(0x08000000u, segment.Address);
        Assert.Equal(32u, segment.Length);
    }

    [Fact]
    public void Rejects_hex_with_a_bad_checksum()
    {
        var good = HexRecord(0x0000, 0x00, new byte[4]);
        // Corrupt the final checksum nibble.
        var bad = good[..^1] + (good[^1] == 'F' ? '0' : 'F');
        var path = NewTempFile(".hex");
        File.WriteAllText(path, bad + Environment.NewLine + HexRecord(0x0000, 0x01, []));

        var image = FirmwareImage.Read(path, FirmwareKind.Hex);

        Assert.Equal(FirmwareImageStatus.Malformed, image.Status);
        Assert.Contains("checksum", image.Diagnostic);
    }

    [Fact]
    public void Rejects_hex_without_an_eof_record()
    {
        var path = NewTempFile(".hex");
        File.WriteAllText(path, HexRecord(0x0000, 0x00, new byte[4]));

        var image = FirmwareImage.Read(path, FirmwareKind.Hex);

        Assert.Equal(FirmwareImageStatus.Malformed, image.Status);
        Assert.Contains("EOF", image.Diagnostic);
    }

    // ============================================================
    // Range policy
    // ============================================================

    [Fact]
    public void Accepts_an_image_that_fits_the_declared_flash_window()
    {
        var path = WriteElf32([(0x08000000u, 0x2000u)]);
        var target = Py32(flashKb: 32, flashOrigin: 0x08000000);

        var result = FirmwareRangeCheck.Validate(path, FirmwareKind.Elf, target);

        Assert.Equal(FirmwareRangeStatus.Ok, result.Status);
        Assert.True(result.IsAcceptable);
    }

    [Fact]
    public void Refuses_an_image_larger_than_the_targets_flash()
    {
        // 64 KB of content aimed at a 32 KB part: the classic wrong-sibling-part
        // pairing that bmp_match cannot catch.
        var path = WriteElf32([(0x08000000u, 64u * 1024u)]);
        var target = Py32(flashKb: 32, flashOrigin: 0x08000000);

        var result = FirmwareRangeCheck.Validate(path, FirmwareKind.Elf, target);

        Assert.Equal(FirmwareRangeStatus.TooLargeForFlash, result.Status);
        Assert.Contains("32 KB", result.Diagnostic);
    }

    [Fact]
    public void Refuses_an_image_linked_for_a_different_memory_map()
    {
        // Small enough to fit, but linked at the STM32 0x08000000 alias of a
        // part whose flash actually starts at 0x00000000.
        var path = WriteElf32([(0x08000000u, 0x400u)]);
        var target = Py32(flashKb: 32, flashOrigin: 0x00000000);

        var result = FirmwareRangeCheck.Validate(path, FirmwareKind.Elf, target);

        Assert.Equal(FirmwareRangeStatus.OutsideDeclaredMemory, result.Status);
        Assert.Contains("outside", result.Diagnostic);
    }

    [Fact]
    public void Refuses_an_image_that_straddles_the_end_of_flash()
    {
        // Starts inside the window, ends past it. A start-address-only check
        // would wrongly accept this.
        var path = WriteElf32([(0x08007F00u, 0x400u)]);
        var target = Py32(flashKb: 32, flashOrigin: 0x08000000);

        var result = FirmwareRangeCheck.Validate(path, FirmwareKind.Elf, target);

        Assert.Equal(FirmwareRangeStatus.OutsideDeclaredMemory, result.Status);
    }

    [Fact]
    public void Accepts_a_ram_segment_only_when_ram_is_declared()
    {
        var path = WriteElf32([(0x20000000u, 0x100u)]);
        var withoutRam = Py32(flashKb: 32, flashOrigin: 0x08000000);
        var withRam = withoutRam with { RamOrigin = 0x20000000, RamKb = 4 };

        Assert.Equal(
            FirmwareRangeStatus.OutsideDeclaredMemory,
            FirmwareRangeCheck.Validate(path, FirmwareKind.Elf, withoutRam).Status);
        Assert.Equal(
            FirmwareRangeStatus.Ok,
            FirmwareRangeCheck.Validate(path, FirmwareKind.Elf, withRam).Status);
    }

    [Fact]
    public void Falls_back_to_size_only_checking_when_no_origin_is_declared()
    {
        // Every catalog signed before the schema addition lands here: absolute
        // addresses cannot be judged, but an oversized image is still refused.
        var fits = WriteElf32([(0xDEADBE00u, 0x100u)]);
        var oversized = WriteElf32([(0xDEADBE00u, 64u * 1024u)]);
        var legacy = Py32(flashKb: 32, flashOrigin: null);

        Assert.Empty(FirmwareRangeCheck.DeclaredRegions(legacy));
        Assert.Equal(
            FirmwareRangeStatus.Ok,
            FirmwareRangeCheck.Validate(fits, FirmwareKind.Elf, legacy).Status);
        Assert.Equal(
            FirmwareRangeStatus.TooLargeForFlash,
            FirmwareRangeCheck.Validate(oversized, FirmwareKind.Elf, legacy).Status);
    }

    [Fact]
    public void Reports_an_unreadable_image_without_claiming_a_range_verdict()
    {
        var path = NewTempFile(".elf");
        File.WriteAllText(path, "garbage");

        var result = FirmwareRangeCheck.Validate(path, FirmwareKind.Elf, Py32(32, 0x08000000));

        Assert.Equal(FirmwareRangeStatus.ImageUnreadable, result.Status);
        Assert.False(result.IsAcceptable);
    }

    // ============================================================
    // Catalog schema
    // ============================================================

    [Fact]
    public void Parses_hex_string_and_numeric_memory_origins()
    {
        var catalog = CatalogJson.Parse(CatalogWithTarget(
            "\"flash_origin\": \"0x08000000\", \"ram_origin\": \"0x20000000\", \"ram_kb\": 4"));

        var target = catalog.Products[0].Target;
        Assert.Equal(0x08000000u, target.FlashOrigin);
        Assert.Equal(0x20000000u, target.RamOrigin);
        Assert.Equal(4, target.RamKb);
    }

    [Fact]
    public void Treats_an_unprefixed_origin_string_as_hexadecimal()
    {
        // Reading 08000000 as decimal eight million would put the accepted
        // window in completely the wrong place.
        var catalog = CatalogJson.Parse(CatalogWithTarget("\"flash_origin\": \"08000000\""));

        Assert.Equal(0x08000000u, catalog.Products[0].Target.FlashOrigin);
    }

    [Fact]
    public void Keeps_catalogs_without_a_memory_map_valid()
    {
        var catalog = CatalogJson.Parse(CatalogWithTarget(null));

        var target = catalog.Products[0].Target;
        Assert.Null(target.FlashOrigin);
        Assert.Null(target.RamOrigin);
        Assert.Null(target.RamKb);
    }

    [Fact]
    public void Refuses_a_half_declared_ram_window()
    {
        Assert.Throws<CatalogParseException>(() =>
            CatalogJson.Parse(CatalogWithTarget("\"flash_origin\": \"0x08000000\", \"ram_origin\": \"0x20000000\"")));
        Assert.Throws<CatalogParseException>(() =>
            CatalogJson.Parse(CatalogWithTarget("\"flash_origin\": \"0x08000000\", \"ram_kb\": 4")));
    }

    [Fact]
    public void Refuses_ram_declared_without_flash_origin()
    {
        Assert.Throws<CatalogParseException>(() =>
            CatalogJson.Parse(CatalogWithTarget("\"ram_origin\": \"0x20000000\", \"ram_kb\": 4")));
    }

    [Fact]
    public void Cli_options_carry_the_memory_map_into_the_target_descriptor()
    {
        var opts = FlashOptions.Parse(
        [
            "--elf", "fw.elf", "--port", "COM3", "--product", "ci-clop",
            "--operator", "Alex", "--batch", "B1", "--target", "PY32Fxxx", "--flash-kb", "32",
            "--flash-origin", "0x08000000", "--ram-origin", "20000000", "--ram-kb", "4",
        ]);

        Assert.NotNull(opts);
        Assert.Equal(0x08000000u, opts!.TargetFlashOrigin);
        Assert.Equal(0x20000000u, opts.TargetRamOrigin);
        Assert.Equal(4, opts.TargetRamKb);

        var regions = FirmwareRangeCheck.DeclaredRegions(opts.ToTargetDescriptor());
        Assert.Equal(2, regions.Count);
    }

    [Fact]
    public void Cli_refuses_a_half_declared_ram_window()
    {
        var opts = FlashOptions.Parse(
        [
            "--elf", "fw.elf", "--port", "COM3", "--product", "ci-clop",
            "--operator", "Alex", "--batch", "B1", "--target", "PY32Fxxx", "--flash-kb", "32",
            "--ram-origin", "0x20000000",
        ]);

        Assert.Null(opts);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static TargetDescriptor Py32(int flashKb, ulong? flashOrigin) =>
        new("PY32Fxxx", "PY32F002Ax5", flashKb, FlashOrigin: flashOrigin);

    private static string CatalogWithTarget(string? extraTargetJson)
    {
        var extra = extraTargetJson is null ? "" : ", " + extraTargetJson;
        return $$"""
        {
          "schema_version": 1,
          "generated_at": "2026-08-07T00:00:00Z",
          "products": [
            {
              "product_id": "ci-clop",
              "display_name": "Ci-Clop",
              "target": {
                "bmp_match": "PY32Fxxx",
                "part_number": "PY32F002Ax5",
                "flash_kb": 32{{extra}}
              },
              "default_release": "1.0.0",
              "releases": [
                {
                  "version": "1.0.0",
                  "elf_filename": "ci-clop_v1.0.0_PY32F002Ax5.elf",
                  "elf_sha256": "0000000000000000000000000000000000000000000000000000000000000001",
                  "released_at": "2026-08-07T00:00:00Z"
                }
              ]
            }
          ]
        }
        """;
    }

    /// <summary>
    /// Minimal little-endian ELF32 with a program header table. Enough structure
    /// for the reader under test; not a linkable object.
    /// </summary>
    private string WriteElf32((uint Paddr, uint Filesz)[] segments)
    {
        const int headerSize = 52;
        const int entrySize = 32;
        var table = new byte[entrySize * segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            var e = table.AsSpan(i * entrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(e, 1);                      // p_type = PT_LOAD
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], 0);                 // p_offset
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], segments[i].Paddr); // p_vaddr
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], segments[i].Paddr);// p_paddr
            BinaryPrimitives.WriteUInt32LittleEndian(e[16..], segments[i].Filesz);
            BinaryPrimitives.WriteUInt32LittleEndian(e[20..], segments[i].Filesz);
        }

        var bytes = new byte[headerSize + table.Length];
        bytes[0] = 0x7F; bytes[1] = (byte)'E'; bytes[2] = (byte)'L'; bytes[3] = (byte)'F';
        bytes[4] = 1; // ELF32
        bytes[5] = 1; // little endian
        bytes[6] = 1; // version
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 2);   // e_type = ET_EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 40);  // e_machine = ARM
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), headerSize); // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(42), entrySize);  // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(44), (ushort)segments.Length);
        table.CopyTo(bytes, headerSize);

        var path = NewTempFile(".elf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string HexRecord(ushort address, byte type, byte[] data)
    {
        var sb = new StringBuilder(":");
        var all = new List<byte>
        {
            (byte)data.Length,
            (byte)(address >> 8),
            (byte)(address & 0xFF),
            type,
        };
        all.AddRange(data);

        byte sum = 0;
        foreach (var b in all) sum += b;
        all.Add((byte)(0x100 - sum));

        foreach (var b in all) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    private string NewTempFile(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"iskra-range-{Guid.NewGuid():N}{extension}");
        _temp.Add(path);
        return path;
    }
}
