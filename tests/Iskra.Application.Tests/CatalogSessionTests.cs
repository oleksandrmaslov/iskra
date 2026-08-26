using Iskra.Application;
using Iskra.Core;

namespace Iskra.Application.Tests;

public sealed class CatalogSessionTests
{
    private static readonly Catalog ValidCatalog = new(
        SchemaVersion: 1,
        GeneratedAt: DateTime.UnixEpoch,
        Products: Array.Empty<Product>());

    [Fact]
    public void Explicit_valid_path_wins_without_enumerating_fallbacks()
    {
        var fallbackEnumerated = false;
        var session = NewSession(
            fallbackCandidates: _ => Fallbacks(),
            files: ["explicit.json", "fallback.json"]);

        var result = session.Load(new AppSettings { CatalogPath = " explicit.json " });

        Assert.True(result.IsReady);
        Assert.Equal("explicit.json", result.SourcePath);
        Assert.False(fallbackEnumerated);

        IEnumerable<string> Fallbacks()
        {
            fallbackEnumerated = true;
            yield return "fallback.json";
        }
    }

    [Fact]
    public void Missing_explicit_path_never_falls_back()
    {
        var parseCount = 0;
        var session = NewSession(
            fallbackCandidates: _ => ["fallback.json"],
            files: ["fallback.json"],
            parseCatalog: _ =>
            {
                parseCount++;
                return ValidCatalog;
            });

        var result = session.Load(new AppSettings { CatalogPath = "missing.json" });

        Assert.Equal(CatalogSessionStatus.ExplicitPathMissing, result.Status);
        Assert.False(result.IsReady);
        Assert.Equal(0, parseCount);
    }

    [Fact]
    public void Untrusted_explicit_path_is_not_parsed_or_downgraded_to_fallback()
    {
        var parseCount = 0;
        var session = NewSession(
            fallbackCandidates: _ => ["fallback.json"],
            files: ["bad.json", "fallback.json"],
            trust: (_, _) => CatalogTrustResult.BadSignature,
            parseCatalog: _ =>
            {
                parseCount++;
                return ValidCatalog;
            });

        var result = session.Load(new AppSettings { CatalogPath = "bad.json" });

        Assert.Equal(CatalogSessionStatus.TrustRejected, result.Status);
        Assert.Equal(CatalogTrustResult.BadSignature, result.TrustResult);
        Assert.Equal(0, parseCount);
    }

    [Fact]
    public void First_existing_fallback_is_authoritative_and_fails_closed()
    {
        var verified = new List<string>();
        var session = NewSession(
            fallbackCandidates: _ => ["missing.json", "bad.json", "good.json"],
            files: ["bad.json", "good.json"],
            trust: (path, _) =>
            {
                verified.Add(path);
                return path == "bad.json"
                    ? CatalogTrustResult.BadSignature
                    : CatalogTrustResult.Verified;
            });

        var result = session.Load(new AppSettings());

        Assert.Equal(CatalogSessionStatus.TrustRejected, result.Status);
        Assert.Equal("bad.json", result.SourcePath);
        Assert.Equal(["bad.json"], verified);
    }

    [Fact]
    public void First_existing_verified_fallback_loads()
    {
        var session = NewSession(
            fallbackCandidates: _ => ["missing.json", "good.json", "later.json"],
            files: ["good.json", "later.json"]);

        var result = session.Load(new AppSettings());

        Assert.True(result.IsReady);
        Assert.Same(ValidCatalog, result.Catalog);
        Assert.Equal("good.json", result.SourcePath);
        Assert.Same(result, session.Current);
    }

    [Fact]
    public void Persisted_unsigned_setting_without_lab_switch_still_requires_signature()
    {
        bool? requireSignedObserved = null;
        var session = NewSession(
            fallbackCandidates: _ => ["catalog.json"],
            files: ["catalog.json"],
            trust: (_, requireSigned) =>
            {
                requireSignedObserved = requireSigned;
                return CatalogTrustResult.UnsignedRejected;
            },
            labMode: false);

        var result = session.Load(new AppSettings { RequireSignedCatalog = false });

        Assert.True(requireSignedObserved);
        Assert.Equal(CatalogSessionStatus.TrustRejected, result.Status);
    }

    [Fact]
    public void Unsigned_file_is_accepted_only_with_both_setting_and_lab_switch()
    {
        var session = NewSession(
            fallbackCandidates: _ => ["catalog.json"],
            files: ["catalog.json"],
            trust: (_, requireSigned) => requireSigned
                ? CatalogTrustResult.UnsignedRejected
                : CatalogTrustResult.UnsignedAllowed,
            labMode: true);

        var result = session.Load(new AppSettings { RequireSignedCatalog = false });

        Assert.True(result.IsReady);
        Assert.Equal(CatalogTrustResult.UnsignedAllowed, result.TrustResult);
    }

    [Fact]
    public void Sideload_directory_requires_explicit_unsigned_lab_mode()
    {
        var built = false;
        var session = NewSession(
            fallbackCandidates: _ => Array.Empty<string>(),
            directories: ["sideload"],
            buildSideload: _ =>
            {
                built = true;
                return ValidCatalog;
            },
            labMode: false);

        var result = session.Load(new AppSettings
        {
            CatalogPath = "sideload",
            RequireSignedCatalog = false,
        });

        Assert.Equal(CatalogSessionStatus.SideloadRequiresLabMode, result.Status);
        Assert.False(built);
    }

    [Fact]
    public void Sideload_directory_loads_when_setting_and_lab_switch_allow_it()
    {
        var session = NewSession(
            fallbackCandidates: _ => Array.Empty<string>(),
            directories: ["sideload"],
            buildSideload: _ => ValidCatalog,
            labMode: true);

        var result = session.Load(new AppSettings
        {
            CatalogPath = "sideload",
            RequireSignedCatalog = false,
        });

        Assert.True(result.IsReady);
        Assert.True(result.IsSideload);
        Assert.Equal(CatalogTrustResult.UnsignedAllowed, result.TrustResult);
    }

    [Fact]
    public void Signed_file_is_parsed_from_the_verified_snapshot_even_if_path_changes_afterward()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"iskra-catalog-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var catalogPath = Path.Combine(directory, "catalog.json");

        try
        {
            var verifiedBytes = CatalogJson.WriteUtf8(CatalogWithProduct("verified-product"));
            var replacementBytes = CatalogJson.WriteUtf8(CatalogWithProduct("replacement-product"));
            var keypair = CatalogSignature.GenerateKeypair();
            File.WriteAllBytes(catalogPath, verifiedBytes);
            File.WriteAllText(
                CatalogTrust.SignaturePathFor(catalogPath),
                Convert.ToBase64String(CatalogSignature.Sign(verifiedBytes, keypair.PrivateKey)));

            var session = new CatalogSession(
                readAndVerifyCatalog: (path, requireSigned) =>
                    CatalogTrust.ReadAndVerifyCatalogFile(path, requireSigned, keypair.PublicKey),
                parseCatalog: snapshot =>
                {
                    // Deterministically simulate the old verify-then-reread
                    // race at the boundary between trust and deserialization.
                    File.WriteAllBytes(catalogPath, replacementBytes);
                    return CatalogJson.Parse(snapshot.Span);
                },
                activateCatalog: AcceptActivation);

            var result = session.Load(new AppSettings { CatalogPath = catalogPath });

            Assert.True(result.IsReady);
            Assert.NotNull(result.Catalog!.FindProduct("verified-product"));
            Assert.Null(result.Catalog.FindProduct("replacement-product"));
            Assert.NotNull(CatalogJson.ParseFile(catalogPath).FindProduct("replacement-product"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Signed_catalog_is_refused_when_activation_floor_detects_rollback()
    {
        var session = new CatalogSession(
            fallbackCandidates: _ => ["signed.json"],
            fileExists: _ => true,
            directoryExists: _ => false,
            readAndVerifyCatalog: (_, _) => new CatalogFileVerificationResult(
                CatalogTrustResult.Verified,
                ReadOnlyMemory<byte>.Empty),
            parseCatalog: _ => ValidCatalog,
            activateCatalog: catalog => new CatalogActivationResult(
                CatalogActivationStatus.RollbackRejected,
                catalog.GeneratedAt,
                catalog.GeneratedAt.AddDays(1),
                "signed catalog is older than the station floor"),
            normalizePath: static path => path);

        var result = session.Load(new AppSettings());

        Assert.Equal(CatalogSessionStatus.RollbackRejected, result.Status);
        Assert.False(result.IsReady);
        Assert.Equal(CatalogTrustResult.Verified, result.TrustResult);
    }

    private static CatalogSession NewSession(
        Func<AppSettings, IEnumerable<string>> fallbackCandidates,
        IReadOnlyCollection<string>? files = null,
        IReadOnlyCollection<string>? directories = null,
        Func<string, bool, CatalogTrustResult>? trust = null,
        Func<ReadOnlyMemory<byte>, Catalog>? parseCatalog = null,
        Func<string, Catalog>? buildSideload = null,
        bool labMode = false)
    {
        files ??= Array.Empty<string>();
        directories ??= Array.Empty<string>();
        return new CatalogSession(
            fallbackCandidates,
            fileExists: path => files.Contains(path),
            directoryExists: path => directories.Contains(path),
            readAndVerifyCatalog: (path, requireSigned) => new CatalogFileVerificationResult(
                (trust ?? ((_, _) => CatalogTrustResult.Verified))(path, requireSigned),
                ReadOnlyMemory<byte>.Empty),
            parseCatalog: parseCatalog ?? (_ => ValidCatalog),
            buildSideload: buildSideload ?? (_ => ValidCatalog),
            unsignedLabModeEnabled: () => labMode,
            normalizePath: static path => path,
            activateCatalog: AcceptActivation);
    }

    private static CatalogActivationResult AcceptActivation(Catalog catalog) => new(
        CatalogActivationStatus.Accepted,
        catalog.GeneratedAt,
        null,
        null);

    private static Catalog CatalogWithProduct(string productId)
    {
        var release = new FirmwareRelease(
            Version: "1.0.0",
            ElfFilename: $"{productId}.elf",
            ElfSha256: new string('0', 64),
            ElfUrl: null,
            ReleasedAt: DateTime.UnixEpoch,
            Notes: null);
        return new Catalog(
            SchemaVersion: CatalogJson.CurrentSchemaVersion,
            GeneratedAt: DateTime.UnixEpoch,
            Products:
            [
                new Product(
                    ProductId: productId,
                    DisplayName: productId,
                    Target: new TargetDescriptor(
                        "PY32Fxxx", "PY32F002Ax5", 32, FlashOrigin: 0x08000000),
                    Releases: [release],
                    DefaultRelease: release.Version),
            ]);
    }
}
