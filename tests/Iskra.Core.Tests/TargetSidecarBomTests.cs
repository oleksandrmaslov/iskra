using System.Text;
using Iskra.Core;

namespace Iskra.Core.Tests;

/// <summary>
/// target.json sidecars are hand-authored in firmware repos on Windows, where
/// editors and PowerShell redirection both write a UTF-8 BOM. A BOM used to
/// fail the whole catalog build with "'0xEF' is an invalid start of a value",
/// which points at nothing an author can act on.
/// </summary>
public class TargetSidecarBomTests
{
    private const string Sha =
        "e6d14dde8002d9526685a27cd4d62e7b60d4c755c46cce473c74cb591129036b";

    /// <summary>
    /// Assembled line by line rather than as one raw literal so the
    /// without-flash-origin variant does not depend on this source file's line
    /// endings, which differ between a fresh checkout and a working tree.
    /// </summary>
    private static string SidecarJson(bool withFlashOrigin = true)
    {
        var lines = new List<string>
        {
            "{",
            """  "product_id":   "ci-clop",""",
            """  "version":      "1.0.4",""",
            """  "part_number":  "PY32F002Ax5",""",
            """  "bmp_match":    "PY32Fxxx",""",
            """  "flash_kb":     20,""",
        };

        if (withFlashOrigin) lines.Add("""  "flash_origin": "0x08000000",""");

        lines.Add($"""  "elf_sha256":   "{Sha}" """.TrimEnd());
        lines.Add("}");
        return string.Join(Environment.NewLine, lines);
    }

    [Fact]
    public void Parse_accepts_a_utf8_bom()
    {
        var withBom = '﻿' + SidecarJson();

        var sidecar = TargetSidecar.Parse(withBom);

        Assert.Equal("ci-clop", sidecar.ProductId);
        Assert.Equal(0x08000000UL, sidecar.FlashOrigin);
    }

    [Fact]
    public void ParseFile_accepts_a_bom_written_by_a_windows_editor()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iskra-sidecar-{Guid.NewGuid():N}.json");
        // UTF8Encoding(true) emits the same preamble Notepad and Out-File do.
        File.WriteAllText(path, SidecarJson(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var sidecar = TargetSidecar.ParseFile(path);

            Assert.Equal("1.0.4", sidecar.Version);
            Assert.Equal(20, sidecar.FlashKb);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_still_accepts_a_sidecar_without_a_bom()
    {
        var sidecar = TargetSidecar.Parse(SidecarJson());

        Assert.Equal("PY32F002Ax5", sidecar.PartNumber);
    }

    [Fact]
    public void A_generated_catalog_carries_flash_origin_and_passes_trusted_validation()
    {
        // The generator must never emit a catalog a station would refuse; the
        // published 2026-08-09 catalog did exactly that because every sidecar
        // omitted flash_origin.
        var sidecar = TargetSidecar.Parse(SidecarJson());

        var catalog = CatalogGenerator.Build(
            new[] { sidecar },
            "oleksandrmaslov",
            new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc),
            revoked: null,
            distributionRepo: "Energy-for-Ukraine/iskra-firmware");

        Assert.Equal(0x08000000UL, catalog.Products[0].Target.FlashOrigin);
        CatalogJson.ValidateTrustedArtifactPaths(catalog);
    }

    [Fact]
    public void A_sidecar_without_flash_origin_produces_a_catalog_no_station_accepts()
    {
        // Pins the failure the generator's own pre-publication check reports,
        // so the guard cannot be dropped without this turning red.
        var sidecar = TargetSidecar.Parse(SidecarJson(withFlashOrigin: false));
        Assert.Null(sidecar.FlashOrigin);

        var catalog = CatalogGenerator.Build(
            new[] { sidecar },
            "oleksandrmaslov",
            new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc),
            revoked: null,
            distributionRepo: "Energy-for-Ukraine/iskra-firmware");

        var ex = Assert.Throws<CatalogParseException>(
            () => CatalogJson.ValidateTrustedArtifactPaths(catalog));
        Assert.Contains("flash_origin", ex.Message);
    }
}
