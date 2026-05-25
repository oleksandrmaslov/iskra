using FlashlightApp.Core;

namespace FlashlightApp.Core.Tests;

public class CatalogResolverTests
{
    private static Catalog SampleCatalog(string productId = "pocket-light",
        string bmp = "PY32Fxxx", int flashKb = 32, string defaultVersion = "1.0.0",
        params FirmwareRelease[] extraReleases)
    {
        var releases = new List<FirmwareRelease>
        {
            new("1.0.0", "pocket-light_v1.0.0_PY32F002Ax5.elf",
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
                    DisplayName: "Pocket Light",
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
        var args = new[] { "--product", "pocket-light", "--operator", "x", "--batch", "y" };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(), @"C:\fw");
        Assert.True(r.Ok);
        Assert.Equal("pocket-light", r.Product!.ProductId);
        Assert.Equal("1.0.0", r.Release!.Version);
        Assert.Equal("PY32Fxxx", GetFlag(r.ResolvedArgs!, "--target"));
        Assert.Equal("32",       GetFlag(r.ResolvedArgs!, "--flash-kb"));
        Assert.Equal("1.0.0",    GetFlag(r.ResolvedArgs!, "--firmware-version"));
        Assert.Equal(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            GetFlag(r.ResolvedArgs!, "--firmware-sha256"));
        Assert.Equal(
            Path.Combine(@"C:\fw", "pocket-light_v1.0.0_PY32F002Ax5.elf"),
            GetFlag(r.ResolvedArgs!, "--elf"));
    }

    [Fact]
    public void Explicit_target_overrides_catalog()
    {
        var args = new[]
        {
            "--product", "pocket-light",
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
            "--product", "pocket-light",
            "--elf", @"D:\override.elf",
            "--operator", "x", "--batch", "y",
        };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(), @"C:\fw");
        Assert.True(r.Ok);
        Assert.Equal(@"D:\override.elf", GetFlag(r.ResolvedArgs!, "--elf"));
    }

    [Fact]
    public void Requested_version_selects_that_release()
    {
        var extra = new FirmwareRelease("1.1.0", "pocket-light_v1.1.0_PY32F002Ax5.elf",
            "abcdef0000000000000000000000000000000000000000000000000000000001",
            null, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), null);
        var args = new[]
        {
            "--product", "pocket-light",
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
            "--product", "pocket-light", "--firmware-version", "9.9.9",
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
            "--product", "pocket-light",
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
            "--product", "pocket-light",
            "--operator", "x", "--batch", "y",
        };
        var r = CatalogResolver.Resolve(args);
        Assert.False(r.Ok);
        Assert.Contains("catalog not found", r.Error);
    }

    [Fact]
    public void Resolved_args_round_trip_through_FlashOptions_Parse()
    {
        // Simulate the CLI flow: catalog resolves catalog-driven fields, then
        // port auto-detect injects --port, then FlashOptions parses.
        var args = new[]
        {
            "--product", "pocket-light",
            "--operator", "Iryna", "--batch", "B-001",
        };
        var r = CatalogResolver.ResolveWithCatalog(args, SampleCatalog(), @"C:\fw");
        Assert.True(r.Ok);

        var withPort = r.ResolvedArgs!.Concat(new[] { "--port", "COM30" }).ToArray();
        var opts = FlashOptions.Parse(withPort);
        Assert.NotNull(opts);
        Assert.Equal("pocket-light", opts!.Product);
        Assert.Equal("PY32Fxxx", opts.TargetBmpMatch);
        Assert.Equal(32, opts.TargetFlashKb);
        Assert.Equal("1.0.0", opts.FirmwareVersion);
        Assert.Equal("COM30", opts.Port);
    }
}
