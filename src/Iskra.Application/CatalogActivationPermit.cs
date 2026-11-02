using Iskra.Core;

namespace Iskra.Application;

/// <summary>
/// Opaque authorization to use one catalog in the physical flash workflow.
/// Its constructor is assembly-internal so UI and external callers cannot turn
/// an arbitrary deserialized Catalog into trusted flash metadata.
/// </summary>
public sealed class CatalogActivationPermit
{
    internal CatalogActivationPermit(
        Catalog catalog,
        CatalogTrustResult trustResult,
        string? catalogSha256,
        string? sourcePath,
        bool isSideload)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = CatalogJson.Freeze(catalog);
        if (trustResult is not (CatalogTrustResult.Verified or CatalogTrustResult.UnsignedAllowed))
            throw new ArgumentOutOfRangeException(nameof(trustResult));
        if (trustResult == CatalogTrustResult.Verified
            && !FirmwareIntegrity.IsValidSha256Hex(catalogSha256))
        {
            throw new ArgumentException(
                "A verified catalog permit requires its exact SHA-256.",
                nameof(catalogSha256));
        }

        TrustResult = trustResult;
        CatalogSha256 = catalogSha256?.ToLowerInvariant();
        SourcePath = sourcePath;
        IsSideload = isSideload;
    }

    public Catalog Catalog { get; }
    public CatalogTrustResult TrustResult { get; }
    public string? CatalogSha256 { get; }
    public string? SourcePath { get; }
    public bool IsSideload { get; }

    /// <summary>
    /// Builds a production permit from the exact byte snapshot whose signature
    /// Core verified. The snapshot result itself cannot be constructed outside
    /// trusted Iskra assemblies.
    /// </summary>
    public static CatalogActivationPermit FromVerifiedSnapshot(
        CatalogFileVerificationResult verification,
        string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(verification);
        if (verification.TrustResult != CatalogTrustResult.Verified
            || verification.CatalogBytes is not { } bytes)
        {
            throw new InvalidOperationException("catalog snapshot is not signature-verified");
        }

        var catalog = CatalogJson.Parse(bytes.Span);
        CatalogJson.ValidateTrustedArtifactPaths(catalog);
        var digest = CatalogActivationPolicy.ComputeCatalogSha256(bytes.Span);
        var activation = CatalogActivationPolicy.ValidateAndAdvance(
            catalog.GeneratedAt,
            catalogSha256: digest);
        if (!activation.IsAccepted)
        {
            throw new InvalidOperationException(
                activation.Diagnostic ?? activation.Status.ToString());
        }

        return new CatalogActivationPermit(
            catalog,
            CatalogTrustResult.Verified,
            digest,
            sourcePath,
            isSideload: false);
    }

    /// <summary>
    /// Lab-only escape hatch. Release binaries compile unsigned catalog support
    /// out, so this factory cannot authorize manual/sideload metadata there.
    /// </summary>
    public static CatalogActivationPermit FromUnsignedLab(
        Catalog catalog,
        string? sourcePath = null,
        bool isSideload = false)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!CatalogTrust.IsUnsignedLabModeEnabled())
            throw new InvalidOperationException("unsigned catalog lab mode is not enabled");
        return new CatalogActivationPermit(
            catalog,
            CatalogTrustResult.UnsignedAllowed,
            null,
            sourcePath,
            isSideload);
    }

    internal static CatalogActivationPermit ForTests(Catalog catalog) => new(
        catalog,
        CatalogTrustResult.UnsignedAllowed,
        null,
        "test-fixture",
        false);
}
