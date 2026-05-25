using FlashlightApp.Core;

namespace FlashlightApp.Core.Tests;

public class CatalogJsonTests
{
    private const string ValidJson = """
        {
          "schema_version": 1,
          "generated_at": "2026-05-25T17:00:00Z",
          "products": [
            {
              "product_id": "pocket-light",
              "display_name": "Pocket Light",
              "target": {
                "bmp_match": "PY32Fxxx",
                "part_number": "PY32F002Ax5",
                "flash_kb": 32
              },
              "releases": [
                {
                  "version": "1.0.0",
                  "elf_filename": "pocket-light_v1.0.0_PY32F002Ax5.elf",
                  "elf_sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                  "elf_url": "https://example/pocket-light_v1.0.0.elf",
                  "released_at": "2026-05-20T12:00:00Z",
                  "notes": "Initial release"
                }
              ],
              "default_release": "1.0.0"
            }
          ]
        }
        """;

    [Fact]
    public void Parse_valid_catalog_returns_populated_model()
    {
        var c = CatalogJson.Parse(ValidJson);
        Assert.Equal(1, c.SchemaVersion);
        Assert.Single(c.Products);
        var p = c.Products[0];
        Assert.Equal("pocket-light", p.ProductId);
        Assert.Equal("Pocket Light", p.DisplayName);
        Assert.Equal("PY32Fxxx", p.Target.BmpMatch);
        Assert.Equal("PY32F002Ax5", p.Target.PartNumber);
        Assert.Equal(32, p.Target.FlashKb);
        Assert.Single(p.Releases);
        Assert.Equal("1.0.0", p.Releases[0].Version);
        Assert.Equal("1.0.0", p.DefaultRelease);
        Assert.NotNull(p.Default());
        Assert.Equal("1.0.0", p.Default()!.Version);
    }

    [Fact]
    public void FindProduct_is_case_insensitive()
    {
        var c = CatalogJson.Parse(ValidJson);
        Assert.NotNull(c.FindProduct("POCKET-LIGHT"));
        Assert.NotNull(c.FindProduct("pocket-light"));
        Assert.Null(c.FindProduct("missing"));
    }

    [Fact]
    public void RoundTrip_serialises_and_reparses_to_equivalent_catalog()
    {
        var c1 = CatalogJson.Parse(ValidJson);
        var json = CatalogJson.Write(c1);
        var c2 = CatalogJson.Parse(json);
        Assert.Equal(c1.Products.Count, c2.Products.Count);
        Assert.Equal(c1.Products[0].Target.BmpMatch, c2.Products[0].Target.BmpMatch);
        Assert.Equal(c1.Products[0].Releases[0].ElfSha256, c2.Products[0].Releases[0].ElfSha256);
    }

    [Fact]
    public void Wrong_schema_version_rejected()
    {
        var bad = ValidJson.Replace("\"schema_version\": 1", "\"schema_version\": 99");
        var ex = Assert.Throws<CatalogParseException>(() => CatalogJson.Parse(bad));
        Assert.Contains("schema_version", ex.Message);
    }

    [Fact]
    public void Empty_products_rejected()
    {
        var bad = """
            { "schema_version": 1, "generated_at": "2026-01-01T00:00:00Z", "products": [] }
            """;
        Assert.Throws<CatalogParseException>(() => CatalogJson.Parse(bad));
    }

    [Fact]
    public void Duplicate_product_id_rejected()
    {
        var bad = """
            {
              "schema_version": 1,
              "generated_at": "2026-01-01T00:00:00Z",
              "products": [
                { "product_id": "x", "display_name": "X",
                  "target": { "bmp_match": "M", "part_number": "P", "flash_kb": 8 },
                  "releases": [ { "version": "1", "elf_filename": "x.elf",
                    "elf_sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                    "elf_url": null, "released_at": "2026-01-01T00:00:00Z", "notes": null } ],
                  "default_release": "1" },
                { "product_id": "X", "display_name": "X2",
                  "target": { "bmp_match": "M", "part_number": "P", "flash_kb": 8 },
                  "releases": [ { "version": "1", "elf_filename": "x.elf",
                    "elf_sha256": "0000000000000000000000000000000000000000000000000000000000000000",
                    "elf_url": null, "released_at": "2026-01-01T00:00:00Z", "notes": null } ],
                  "default_release": "1" }
              ]
            }
            """;
        var ex = Assert.Throws<CatalogParseException>(() => CatalogJson.Parse(bad));
        Assert.Contains("duplicate product_id", ex.Message);
    }

    [Theory]
    [InlineData("\"bmp_match\": \"PY32Fxxx\"", "\"bmp_match\": \"\"",         "bmp_match missing")]
    [InlineData("\"part_number\": \"PY32F002Ax5\"", "\"part_number\": \"\"", "part_number missing")]
    [InlineData("\"flash_kb\": 32", "\"flash_kb\": 0",                      "flash_kb must be > 0")]
    public void Missing_or_invalid_target_fields_rejected(string find, string replace, string expectedInMsg)
    {
        var bad = ValidJson.Replace(find, replace);
        var ex = Assert.Throws<CatalogParseException>(() => CatalogJson.Parse(bad));
        Assert.Contains(expectedInMsg, ex.Message);
    }

    [Fact]
    public void Non_hex_sha256_rejected()
    {
        var bad = ValidJson.Replace(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "ZZZZ56789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0");
        var ex = Assert.Throws<CatalogParseException>(() => CatalogJson.Parse(bad));
        Assert.Contains("elf_sha256", ex.Message);
    }

    [Fact]
    public void Sha256_wrong_length_rejected()
    {
        var bad = ValidJson.Replace(
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "deadbeef");
        Assert.Throws<CatalogParseException>(() => CatalogJson.Parse(bad));
    }

    [Fact]
    public void Default_release_must_point_at_an_existing_release()
    {
        var bad = ValidJson.Replace("\"default_release\": \"1.0.0\"", "\"default_release\": \"9.9.9\"");
        var ex = Assert.Throws<CatalogParseException>(() => CatalogJson.Parse(bad));
        Assert.Contains("default_release", ex.Message);
    }

    [Fact]
    public void Malformed_json_wrapped_in_CatalogParseException()
    {
        var ex = Assert.Throws<CatalogParseException>(() => CatalogJson.Parse("{not json"));
        Assert.IsType<System.Text.Json.JsonException>(ex.InnerException);
    }

    [Fact]
    public void Example_catalog_in_repo_parses_clean()
    {
        // Walk up from the test bin directory to the repo root, find examples/catalog.json.
        var dir = AppContext.BaseDirectory;
        string? path = null;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "examples", "catalog.json");
            if (File.Exists(candidate)) { path = candidate; break; }
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(path);
        var c = CatalogJson.ParseFile(path!);
        Assert.Contains(c.Products, p => p.ProductId == "pocket-light");
    }
}
