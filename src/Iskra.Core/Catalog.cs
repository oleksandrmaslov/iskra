namespace FlashlightApp.Core;

/// <summary>
/// What the catalog promises a given product is, target-stack-wise.
/// <para><c>BmpMatch</c> is the substring expected in BMP's <c>monitor swdp_scan</c>
/// output. BMP reports at family granularity (e.g. all PY32F0xx as
/// "PY32Fxxx M0+"), not part number, so <c>BmpMatch</c> must be a family
/// string like "PY32Fxxx" or "STM32F103". <c>PartNumber</c> is display-only.</para>
/// </summary>
public sealed record TargetDescriptor(
    string BmpMatch,
    string PartNumber,
    int FlashKb);

public sealed record FirmwareRelease(
    string Version,
    string ElfFilename,
    string ElfSha256,
    string? ElfUrl,
    DateTime ReleasedAt,
    string? Notes);

public sealed record Product(
    string ProductId,
    string DisplayName,
    TargetDescriptor Target,
    IReadOnlyList<FirmwareRelease> Releases,
    string DefaultRelease)
{
    public FirmwareRelease? FindRelease(string version) =>
        Releases.FirstOrDefault(r =>
            string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));

    public FirmwareRelease? Default() => FindRelease(DefaultRelease);
}

public sealed record Catalog(
    int SchemaVersion,
    DateTime GeneratedAt,
    IReadOnlyList<Product> Products)
{
    public Product? FindProduct(string productId) =>
        Products.FirstOrDefault(p =>
            string.Equals(p.ProductId, productId, StringComparison.OrdinalIgnoreCase));
}
