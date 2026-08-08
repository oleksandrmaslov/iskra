namespace Iskra.Core;

public enum FirmwareRangeStatus
{
    /// <summary>Every loadable byte fits the target's declared memory.</summary>
    Ok,
    /// <summary>The image could not be read; nothing was validated.</summary>
    ImageUnreadable,
    /// <summary>Total loadable bytes exceed the target's flash size.</summary>
    TooLargeForFlash,
    /// <summary>A segment falls outside every declared memory region.</summary>
    OutsideDeclaredMemory,
}

public sealed record FirmwareRangeResult(
    FirmwareRangeStatus Status,
    ulong TotalBytes,
    IReadOnlyList<FirmwareSegment> Segments,
    string? Diagnostic)
{
    public bool IsAcceptable => Status == FirmwareRangeStatus.Ok;
}

/// <summary>
/// Refuses firmware whose load map cannot belong to the catalog-declared target,
/// before any byte reaches the device.
///
/// <para>Two levels of strictness, so existing signed catalogs keep working:</para>
/// <list type="bullet">
/// <item>Always: total loadable bytes must fit <c>flash_kb</c>. An image built
/// for a larger part in the same family is caught here, and BMP's family-level
/// <c>bmp_match</c> cannot catch it.</item>
/// <item>When the catalog declares <c>flash_origin</c> (and optionally a RAM
/// window): every segment's load address must fall inside a declared region. An
/// image linked for a different memory map is caught here even when it is small
/// enough to fit.</item>
/// </list>
///
/// <para>This complements, and does not replace, the SHA-256 integrity check.
/// Integrity proves the file is the one the catalog names; this proves the named
/// file can physically belong to the target in front of the operator.</para>
/// </summary>
public static class FirmwareRangeCheck
{
    public static FirmwareRangeResult Validate(
        string firmwarePath,
        FirmwareKind kind,
        TargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var image = FirmwareImage.Read(firmwarePath, kind);
        if (!image.IsOk)
        {
            return new FirmwareRangeResult(
                FirmwareRangeStatus.ImageUnreadable,
                0,
                image.Segments,
                image.Diagnostic);
        }

        return Validate(image, target);
    }

    public static FirmwareRangeResult Validate(FirmwareImageResult image, TargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(target);

        if (!image.IsOk)
        {
            return new FirmwareRangeResult(
                FirmwareRangeStatus.ImageUnreadable, 0, image.Segments, image.Diagnostic);
        }

        var flashBytes = (ulong)Math.Max(0, target.FlashKb) * 1024UL;
        var total = image.TotalBytes;
        if (flashBytes > 0 && total > flashBytes)
        {
            return new FirmwareRangeResult(
                FirmwareRangeStatus.TooLargeForFlash,
                total,
                image.Segments,
                $"image needs {total} bytes but {target.PartNumber} has {target.FlashKb} KB "
                + $"({flashBytes} bytes) of flash");
        }

        var regions = DeclaredRegions(target);
        if (regions.Count == 0)
        {
            // Size-only mode: the catalog has not declared where memory lives, so
            // absolute addresses cannot be judged. Populating flash_origin turns
            // this into the strict check.
            return new FirmwareRangeResult(FirmwareRangeStatus.Ok, total, image.Segments, null);
        }

        foreach (var segment in image.Segments)
        {
            if (FitsAnyRegion(segment, regions)) continue;

            return new FirmwareRangeResult(
                FirmwareRangeStatus.OutsideDeclaredMemory,
                total,
                image.Segments,
                $"segment {segment} is outside {target.PartNumber} memory "
                + $"[{string.Join(", ", regions)}]");
        }

        return new FirmwareRangeResult(FirmwareRangeStatus.Ok, total, image.Segments, null);
    }

    /// <summary>
    /// Memory windows the catalog declared for this target. Empty means the
    /// catalog only stated a flash size, which limits validation to total bytes.
    /// </summary>
    public static IReadOnlyList<MemoryRegion> DeclaredRegions(TargetDescriptor target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var regions = new List<MemoryRegion>(2);

        if (target.FlashOrigin is { } flashOrigin && target.FlashKb > 0)
            regions.Add(new MemoryRegion("flash", flashOrigin, (ulong)target.FlashKb * 1024UL));

        if (target.RamOrigin is { } ramOrigin && target.RamKb is > 0)
            regions.Add(new MemoryRegion("ram", ramOrigin, (ulong)target.RamKb.Value * 1024UL));

        return regions;
    }

    private static bool FitsAnyRegion(FirmwareSegment segment, IReadOnlyList<MemoryRegion> regions)
    {
        foreach (var region in regions)
        {
            if (segment.Address >= region.Origin && segment.EndExclusive <= region.EndExclusive)
                return true;
        }

        return false;
    }
}

public sealed record MemoryRegion(string Name, ulong Origin, ulong Length)
{
    public ulong EndExclusive => Origin + Length;

    public override string ToString() =>
        $"{Name} 0x{Origin:X8}..0x{EndExclusive - 1:X8}";
}
