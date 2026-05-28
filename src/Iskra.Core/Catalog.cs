using System.Text.Json.Serialization;

namespace Iskra.Core;

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

/// <summary>
/// Points at a firmware artefact published as a GitHub Release asset. When set
/// on a <see cref="FirmwareRelease"/>, the flasher downloads the asset (auth
/// via Sprint 3 GitHub App + Device Flow), caches it locally, and verifies
/// against <see cref="FirmwareRelease.ElfSha256"/> before flashing.
/// <para><c>Repo</c> is <c>owner/name</c>. <c>Tag</c> is the GitHub release
/// tag (e.g. <c>v1.0.0</c>). <c>Asset</c> is the release asset filename;
/// by convention it matches <see cref="FirmwareRelease.ElfFilename"/>.</para>
/// </summary>
public sealed record GitHubReleaseRef(
    string Repo,
    string Tag,
    string Asset);

public sealed record FirmwareRelease(
    string Version,
    string ElfFilename,
    string ElfSha256,
    string? ElfUrl,
    DateTime ReleasedAt,
    string? Notes,
    GitHubReleaseRef? ElfSource = null)
{
    /// <summary>True when this release must be downloaded from a remote source.</summary>
    [JsonIgnore]
    public bool IsRemote => ElfSource is not null;
}

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

/// <summary>
/// A signed entry that disables a previously-shipped firmware release. The
/// flash gate refuses to flash any <c>(product_id, version)</c> in this list,
/// even if the matching release entry is still present (operators may have
/// stale cached releases too). <c>Reason</c> is shown to the operator.
/// </summary>
public sealed record RevokedRelease(
    string ProductId,
    string Version,
    string? Reason = null);

public sealed record Catalog(
    int SchemaVersion,
    DateTime GeneratedAt,
    IReadOnlyList<Product> Products,
    IReadOnlyList<RevokedRelease>? Revoked = null)
{
    public Product? FindProduct(string productId) =>
        Products.FirstOrDefault(p =>
            string.Equals(p.ProductId, productId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the catalog explicitly revokes <c>(productId, version)</c>.
    /// Compared case-insensitively. Treat the catalog as authoritative — a
    /// cached release on disk that's now revoked must not be flashed.
    /// </summary>
    public bool IsRevoked(string productId, string version) =>
        Revoked is not null && Revoked.Any(r =>
            string.Equals(r.ProductId, productId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Version,   version,   StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the revocation entry for a release, or <c>null</c> if not revoked.</summary>
    public RevokedRelease? FindRevocation(string productId, string version) =>
        Revoked?.FirstOrDefault(r =>
            string.Equals(r.ProductId, productId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Version,   version,   StringComparison.OrdinalIgnoreCase));
}
