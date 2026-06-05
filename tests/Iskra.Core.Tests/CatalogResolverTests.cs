using Iskra.Core;

namespace Iskra.Core.Tests;

public class CatalogResolverTests
{
    private static Catalog SampleCatalog(string productId = "ci-clop",
        string bmp = "PY32Fxxx", int flashKb = 32, string defaultVersion = "1.0.0",
        params FirmwareRelease[] extraReleases)
    {
        var releases = new List<FirmwareRelease>
        {
            new("1.0.0", "ci-clop_v1.0.0_PY32F002Ax5.elf",
                "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null),
        };
        releases.AddRange(extraReleases);
        return new Catalog(
            SchemaVersion: 1,
            GeneratedAt:   new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Products: new[]
            {
                new Product(
                    ProductId: productId,
                    DisplayName: "CI-CLOP",
                    Target: new TargetDescriptor(bmp, "PY32F002Ax5", flashKb),
                    Releases: releases,
                    DefaultRelease: defaultVersion),
            });
    }

    private static string GetFlag(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : "<missing>";
    }

    [Fact]
    public void Passthrough_when_no_catalog_flag()
    {
        var args = new[] { "--operator", "x", "--batch", "y" };
        var r = CatalogResolver.Resolve(args);
        Assert.True(r.Ok);
        Assert.Null(r.Product);
        Assert.Equal(args, r.ResolvedArgs);
    }

    [Fact]
    public void Resolves_default_release_for_known_product()
    {
        var args = new[] { "--product", "ci-clop", "--operator", "x", "--batch", "y" };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(), @"C:\fw");
        Assert.True(r.Ok);
        Assert.Equal("ci-clop", r.Product!.ProductId);
        Assert.Equal("1.0.0", r.Release!.Version);
        Assert.Equal("PY32Fxxx", GetFlag(r.ResolvedArgs!, "--target"));
        Assert.Equal("32",       GetFlag(r.ResolvedArgs!, "--flash-kb"));
        Assert.Equal("1.0.0",    GetFlag(r.ResolvedArgs!, "--firmware-version"));
        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            GetFlag(r.ResolvedArgs!, "--firmware-sha256"));
        Assert.Equal("elf", GetFlag(r.ResolvedArgs!, "--firmware-kind"));
        Assert.Equal(
            Path.Combine(@"C:\fw", "ci-clop_v1.0.0_PY32F002Ax5.elf"),
            GetFlag(r.ResolvedArgs!, "--elf"));
    }

    [Fact]
    public void Explicit_target_overrides_catalog()
    {
        var args = new[]
        {
            "--product", "ci-clop",
            "--target", "STM32F1",
            "--operator", "x", "--batch", "y",
        };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(), @"C:\fw");
        Assert.True(r.Ok);
        Assert.Equal("STM32F1", GetFlag(r.ResolvedArgs!, "--target"));
        // catalog still fills the rest
        Assert.Equal("32", GetFlag(r.ResolvedArgs!, "--flash-kb"));
    }

    [Fact]
    public void Explicit_elf_overrides_catalog_elf_path()
    {
        var args = new[]
        {
            "--product", "ci-clop",
            "--elf", @"D:\override.elf",
            "--operator", "x", "--batch", "y",
        };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(), @"C:\fw");
        Assert.True(r.Ok);
        Assert.Equal(@"D:\override.elf", GetFlag(r.ResolvedArgs!, "--elf"));
    }

    [Fact]
    public void Target_overrides_fill_flash_knobs_when_missing()
    {
        var catalog = new Catalog(
            SchemaVersion: 1,
            GeneratedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Products: new[]
            {
                new Product(
                    ProductId: "ci-clop",
                    DisplayName: "CI-CLOP",
                    Target: new TargetDescriptor(
                        "PY32Fxxx", "PY32F002Ax5", 32,
                        FrequencyHz: 4_000_000,
                        PowerMode: PowerMode.Probe,
                        ConnectReset: true,
                        TimeoutSeconds: 28),
                    Releases: new[]
                    {
                        new FirmwareRelease("1.0.0", "ci-clop_v1.0.0_PY32F002Ax5.hex",
                            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                            null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null,
                            FirmwareKind: FirmwareKind.Hex),
                    },
                    DefaultRelease: "1.0.0"),
            });
        var args = new[] { "--product", "ci-clop", "--operator", "x", "--batch", "y" };
        var r = CatalogResolver.ResolveWithCatalog(args, catalog, @"C:\fw");

        Assert.True(r.Ok);
        Assert.Equal("hex", GetFlag(r.ResolvedArgs!, "--firmware-kind"));
        Assert.Equal("4000000", GetFlag(r.ResolvedArgs!, "--freq"));
        Assert.Equal("probe", GetFlag(r.ResolvedArgs!, "--power"));
        Assert.Contains("--connect-reset", r.ResolvedArgs!);
        Assert.Equal("28", GetFlag(r.ResolvedArgs!, "--timeout"));
        Assert.EndsWith(".hex", GetFlag(r.ResolvedArgs!, "--elf"));
    }

    [Fact]
    public void Explicit_flash_knobs_override_target_overrides()
    {
        var catalog = new Catalog(
            SchemaVersion: 1,
            GeneratedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Products: new[]
            {
                new Product(
                    "ci-clop", "CI-CLOP",
                    new TargetDescriptor("PY32Fxxx", "PY32F002Ax5", 32,
                        FrequencyHz: 4_000_000, PowerMode: PowerMode.Probe, TimeoutSeconds: 28),
                    new[]
                    {
                        new FirmwareRelease("1.0.0", "ci-clop_v1.0.0_PY32F002Ax5.elf",
                            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                            null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null),
                    },
                    "1.0.0"),
            });
        var args = new[]
        {
            "--product", "ci-clop", "--operator", "x", "--batch", "y",
            "--freq", "1000000", "--power", "external", "--timeout", "15",
        };

        var r = CatalogResolver.ResolveWithCatalog(args, catalog, @"C:\fw");

        Assert.True(r.Ok);
        Assert.Equal("1000000", GetFlag(r.ResolvedArgs!, "--freq"));
        Assert.Equal("external", GetFlag(r.ResolvedArgs!, "--power"));
        Assert.Equal("15", GetFlag(r.ResolvedArgs!, "--timeout"));
    }

    [Fact]
    public void Requested_version_selects_that_release()
    {
        var extra = new FirmwareRelease("1.1.0", "ci-clop_v1.1.0_PY32F002Ax5.elf",
            "abcdef0000000000000000000000000000000000000000000000000000000001",
            null, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), null);
        var args = new[]
        {
            "--product", "ci-clop",
            "--firmware-version", "1.1.0",
            "--operator", "x", "--batch", "y",
        };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(extraReleases: extra), @"C:\fw");
        Assert.True(r.Ok);
        Assert.Equal("1.1.0", r.Release!.Version);
        Assert.Equal("abcdef0000000000000000000000000000000000000000000000000000000001",
            GetFlag(r.ResolvedArgs!, "--firmware-sha256"));
    }

    [Fact]
    public void Unknown_product_fails()
    {
        var args = new[] { "--product", "nope", "--operator", "x", "--batch", "y" };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(), @"C:\fw");
        Assert.False(r.Ok);
        Assert.Contains("not in catalog", r.Error);
    }

    [Fact]
    public void Unknown_version_fails()
    {
        var args = new[]
        {
            "--product", "ci-clop", "--firmware-version", "9.9.9",
            "--operator", "x", "--batch", "y",
        };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(), @"C:\fw");
        Assert.False(r.Ok);
        Assert.Contains("9.9.9", r.Error);
    }

    [Fact]
    public void Missing_product_arg_fails()
    {
        var args = new[] { "--operator", "x", "--batch", "y" };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(), @"C:\fw");
        Assert.False(r.Ok);
        Assert.Contains("--product", r.Error);
    }

    [Fact]
    public void Resolved_args_do_not_contain_catalog_flag()
    {
        var args = new[]
        {
            "--catalog", @"C:\fw\catalog.json",
            "--product", "ci-clop",
            "--operator", "x", "--batch", "y",
        };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(), @"C:\fw");
        Assert.True(r.Ok);
        Assert.DoesNotContain("--catalog", r.ResolvedArgs!);
        Assert.DoesNotContain(@"C:\fw\catalog.json", r.ResolvedArgs!);
    }

    [Fact]
    public void Resolve_with_bad_catalog_path_fails_cleanly()
    {
        var args = new[]
        {
            "--catalog", Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json"),
            "--product", "ci-clop",
            "--operator", "x", "--batch", "y",
        };
        var r = CatalogResolver.Resolve(args);
        Assert.False(r.Ok);
        Assert.Contains("catalog not found", r.Error);
    }

    [Fact]
    public void Remote_release_without_explicit_elf_resolves_but_omits_elf_for_CLI_to_fetch()
    {
        var remote = new FirmwareRelease("2.0.0", "ci-clop_v2.0.0_PY32F002Ax5.elf",
            "abcdef0000000000000000000000000000000000000000000000000000000002",
            null, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), null,
            new GitHubReleaseRef("oleksandrmaslov/ci-clop-firmware", "v2.0.0",
                "ci-clop_v2.0.0_PY32F002Ax5.elf"));
        var args = new[]
        {
            "--product", "ci-clop", "--firmware-version", "2.0.0",
            "--operator", "x", "--batch", "y",
        };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(extraReleases: remote), @"C:\fw");

        Assert.True(r.Ok);
        Assert.True(r.Release!.IsRemote);
        Assert.NotNull(r.Release.ElfSource);
        // Other fields are filled normally so the CLI can do its preflight.
        Assert.Equal("PY32Fxxx", GetFlag(r.ResolvedArgs!, "--target"));
        Assert.Equal("2.0.0",    GetFlag(r.ResolvedArgs!, "--firmware-version"));
        Assert.Equal("abcdef0000000000000000000000000000000000000000000000000000000002",
            GetFlag(r.ResolvedArgs!, "--firmware-sha256"));
        // --elf intentionally absent — CLI's FirmwareCache step injects it.
        Assert.DoesNotContain("--elf", r.ResolvedArgs!);
    }

    [Fact]
    public void Remote_release_with_explicit_elf_passes_through()
    {
        var remote = new FirmwareRelease("2.0.0", "ci-clop_v2.0.0_PY32F002Ax5.elf",
            "abcdef0000000000000000000000000000000000000000000000000000000002",
            null, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), null,
            new GitHubReleaseRef("oleksandrmaslov/ci-clop-firmware", "v2.0.0",
                "ci-clop_v2.0.0_PY32F002Ax5.elf"));
        var args = new[]
        {
            "--product", "ci-clop", "--firmware-version", "2.0.0",
            "--elf", @"D:\dev-build.elf",
            "--operator", "x", "--batch", "y",
        };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(extraReleases: remote), @"C:\fw");
        Assert.True(r.Ok);
        Assert.Equal(@"D:\dev-build.elf", GetFlag(r.ResolvedArgs!, "--elf"));
        Assert.Equal("abcdef0000000000000000000000000000000000000000000000000000000002",
            GetFlag(r.ResolvedArgs!, "--firmware-sha256"));
    }

    [Fact]
    public void Resolved_args_round_trip_through_FlashOptions_Parse()
    {
        // Simulate the CLI flow: catalog resolves catalog-driven fields, then
        // port auto-detect injects --port, then FlashOptions parses.
        var args = new[]
        {
            "--product", "ci-clop",
            "--operator", "Iryna", "--batch", "B-001",
        };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(), @"C:\fw");
        Assert.True(r.Ok);

        var withPort = r.ResolvedArgs!.Concat(new[] { "--port", "COM30" }).ToArray();
        var opts = FlashOptions.Parse(withPort);
        Assert.NotNull(opts);
        Assert.Equal("ci-clop", opts!.Product);
        Assert.Equal("PY32Fxxx", opts.TargetBmpMatch);
        Assert.Equal(32, opts.TargetFlashKb);
        Assert.Equal("1.0.0", opts.FirmwareVersion);
        Assert.Equal("COM30", opts.Port);
    }
}
