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
        var parsed = new List<string>();
        var session = NewSession(
            fallbackCandidates: _ => ["fallback.json"],
            files: ["fallback.json"],
            parseCatalog: path =>
            {
                parsed.Add(path);
                return ValidCatalog;
            });

        var result = session.Load(new AppSettings { CatalogPath = "missing.json" });

        Assert.Equal(CatalogSessionStatus.ExplicitPathMissing, result.Status);
        Assert.False(result.IsReady);
        Assert.Empty(parsed);
    }

    [Fact]
    public void Untrusted_explicit_path_is_not_parsed_or_downgraded_to_fallback()
    {
        var parsed = new List<string>();
        var session = NewSession(
            fallbackCandidates: _ => ["fallback.json"],
            files: ["bad.json", "fallback.json"],
            trust: (_, _) => CatalogTrustResult.BadSignature,
            parseCatalog: path =>
            {
                parsed.Add(path);
                return ValidCatalog;
            });

        var result = session.Load(new AppSettings { CatalogPath = "bad.json" });

        Assert.Equal(CatalogSessionStatus.TrustRejected, result.Status);
        Assert.Equal(CatalogTrustResult.BadSignature, result.TrustResult);
        Assert.Empty(parsed);
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

    private static CatalogSession NewSession(
        Func<AppSettings, IEnumerable<string>> fallbackCandidates,
        IReadOnlyCollection<string>? files = null,
        IReadOnlyCollection<string>? directories = null,
        Func<string, bool, CatalogTrustResult>? trust = null,
        Func<string, Catalog>? parseCatalog = null,
        Func<string, Catalog>? buildSideload = null,
        bool labMode = false)
    {
        files ??= Array.Empty<string>();
        directories ??= Array.Empty<string>();
        return new CatalogSession(
            fallbackCandidates,
            fileExists: path => files.Contains(path),
            directoryExists: path => directories.Contains(path),
            verifyCatalog: trust ?? ((_, _) => CatalogTrustResult.Verified),
            parseCatalog: parseCatalog ?? (_ => ValidCatalog),
            buildSideload: buildSideload ?? (_ => ValidCatalog),
            unsignedLabModeEnabled: () => labMode,
            normalizePath: static path => path);
    }
}
