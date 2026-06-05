using Iskra.Core;

namespace Iskra.Core.Tests;

public class SideloadCatalogBuilderTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void BuildFromDirectory_creates_local_catalog_with_absolute_firmware_path()
    {
        var root = Path.Combine(Path.GetTempPath(), $"iskra-sideload-{Guid.NewGuid():N}");
        try
        {
            var releaseDir = Path.Combine(root, "ci-clop", "v1.0.0");
            Directory.CreateDirectory(releaseDir);
            var firmware = Path.Combine(releaseDir, "ci-clop_v1.0.0_PY32F002Ax5.hex");
            File.WriteAllText(firmware, ":10010000214601360121470136007EFE09D2190140\n:00000001FF\n");
            File.WriteAllText(Path.Combine(releaseDir, "target.json"), $$"""
                {
                  "product_id": "ci-clop",
                  "version": "1.0.0",
                  "part_number": "PY32F002Ax5",
                  "bmp_match": "PY32Fxxx",
                  "flash_kb": 32,
                  "elf_sha256": "{{Sha}}",
                  "firmware_kind": "hex",
                  "frequency_hz": 4000000,
                  "power_mode": "probe",
                  "connect_reset": true,
                  "timeout_s": 28
                }
                """);

            var catalog = SideloadCatalogBuilder.BuildFromDirectory(root, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

            var product = Assert.Single(catalog.Products);
            Assert.Equal("ci-clop", product.ProductId);
            Assert.Equal(4_000_000, product.Target.FrequencyHz);
            Assert.Equal(PowerMode.Probe, product.Target.PowerMode);
            Assert.True(product.Target.ConnectReset);
            Assert.Equal(28, product.Target.TimeoutSeconds);

            var release = Assert.Single(product.Releases);
            Assert.Equal(FirmwareKind.Hex, release.FirmwareKind);
            Assert.Equal(Path.GetFullPath(firmware), release.ElfFilename);
            Assert.False(release.IsRemote);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogResolver_accepts_sideload_dir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"iskra-sideload-resolve-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "ci-clop_v1.0.0_PY32F002Ax5.elf"),
                new byte[] { 0x7F, 0x45, 0x4C, 0x46 });
            File.WriteAllText(Path.Combine(root, "target.json"), $$"""
                {
                  "product_id": "ci-clop",
                  "version": "1.0.0",
                  "part_number": "PY32F002Ax5",
                  "bmp_match": "PY32Fxxx",
                  "flash_kb": 32,
                  "elf_sha256": "{{Sha}}"
                }
                """);

            var r = CatalogResolver.Resolve(new[]
            {
                "--sideload-dir", root,
                "--product", "ci-clop",
                "--operator", "x",
                "--batch", "b",
            });

            Assert.True(r.Ok, r.Error);
            Assert.Equal("ci-clop", r.Product!.ProductId);
            Assert.Equal("1.0.0", r.Release!.Version);
            Assert.DoesNotContain("--sideload-dir", r.ResolvedArgs!);
            Assert.Contains("--elf", r.ResolvedArgs!);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
