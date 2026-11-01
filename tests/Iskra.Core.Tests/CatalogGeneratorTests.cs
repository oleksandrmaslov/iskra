using Iskra.Core;

namespace Iskra.Core.Tests;

public class CatalogGeneratorTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Sha2 = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string CiClopV100Sha = "4514acf573a17487db6ccf52b9e4ef2840bf59c4d743789ee6715eaf5655f2cd";
    private const string CiClopV102Sha = "60743a9c3ef6f40e6b707a2b4f8b201875c7954fe52fdf752f4864296ed3dfeb";
    private const string CiClopV103Sha = "403355ab8371c624af5c5cd5b01109c2734a6e31c81a89b18bbab8b9ea23423f";
    private const string CiClopV104Sha = "e6d14dde8002d9526685a27cd4d62e7b60d4c755c46cce473c74cb591129036b";

    private static TargetSidecar Sidecar(
        string productId = "ci-clop", string version = "1.0.0",
        string partNumber = "PY32F002Ax5", string bmpMatch = "PY32Fxxx",
        int flashKb = 32, string sha = Sha,
        string? displayName = null, string? notes = null,
        FirmwareKind firmwareKind = FirmwareKind.Elf,
        int? frequencyHz = null,
        PowerMode? powerMode = null,
        bool? connectReset = null,
        int? timeoutSeconds = null,
        ulong? flashOrigin = null,
        ulong? ramOrigin = null,
        int? ramKb = null) =>
        new(productId, version, partNumber, bmpMatch, flashKb, sha, displayName, null, notes,
            firmwareKind, frequencyHz, powerMode, connectReset, timeoutSeconds,
            flashOrigin, ramOrigin, ramKb);

    private static readonly DateTime FixedNow = new(2026, 5, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_single_sidecar_produces_one_product_with_one_release()
    {
        var c = CatalogGenerator.Build(new[] { Sidecar() }, "owner", FixedNow);

        Assert.Single(c.Products);
        var p = c.Products[0];
        Assert.Equal("ci-clop", p.ProductId);
        Assert.Equal("Ci-Clop", p.DisplayName); // title-cased fallback
        Assert.Equal("PY32Fxxx", p.Target.BmpMatch);
        Assert.Equal(32, p.Target.FlashKb);
        Assert.Equal("1.0.0", p.DefaultRelease);
        Assert.Single(p.Releases);

        var r = p.Releases[0];
        Assert.Equal("1.0.0", r.Version);
        Assert.Equal("ci-clop_v1.0.0_PY32F002Ax5.elf", r.ElfFilename);
        Assert.Equal(Sha, r.ElfSha256);
        Assert.NotNull(r.ElfSource);
        Assert.Equal("owner/ci-clop-firmware", r.ElfSource!.Repo);
        Assert.Equal("v1.0.0", r.ElfSource.Tag);
        Assert.Equal("ci-clop_v1.0.0_PY32F002Ax5.elf", r.ElfSource.Asset);
    }

    [Fact]
    public void Sidecar_display_name_wins_over_title_case_fallback()
    {
        var c = CatalogGenerator.Build(
            new[] { Sidecar(displayName: "CI-CLOP") }, "owner", FixedNow);
        Assert.Equal("CI-CLOP", c.Products[0].DisplayName);
    }

    [Fact]
    public void Build_carries_target_overrides_and_hex_kind()
    {
        var c = CatalogGenerator.Build(new[]
        {
            Sidecar(
                firmwareKind: FirmwareKind.Hex,
                frequencyHz: 4_000_000,
                powerMode: PowerMode.Probe,
                connectReset: true,
                timeoutSeconds: 28),
        }, "owner", FixedNow);

        var target = c.Products[0].Target;
        Assert.Equal(4_000_000, target.FrequencyHz);
        Assert.Equal(PowerMode.Probe, target.PowerMode);
        Assert.True(target.ConnectReset);
        Assert.Equal(28, target.TimeoutSeconds);

        var release = c.Products[0].Releases[0];
        Assert.Equal(FirmwareKind.Hex, release.FirmwareKind);
        Assert.Equal("ci-clop_v1.0.0_PY32F002Ax5.hex", release.ElfFilename);
        Assert.Equal("ci-clop_v1.0.0_PY32F002Ax5.hex", release.ElfSource!.Asset);
    }

    [Fact]
    public void Default_release_picks_highest_semver()
    {
        var sidecars = new[]
        {
            Sidecar(version: "1.0.0", sha: Sha),
            Sidecar(version: "0.9.0", sha: Sha2),
            Sidecar(version: "1.0.1", sha: new string('a', 64)),
        };
        var c = CatalogGenerator.Build(sidecars, "owner", FixedNow);
        Assert.Equal("1.0.1", c.Products[0].DefaultRelease);
        Assert.Equal(3, c.Products[0].Releases.Count);
        // Releases come out in ascending semver order.
        Assert.Equal(new[] { "0.9.0", "1.0.0", "1.0.1" },
            c.Products[0].Releases.Select(r => r.Version).ToArray());
    }

    [Fact]
    public void Prerelease_is_lower_than_release()
    {
        var sidecars = new[]
        {
            Sidecar(version: "1.0.0-rc1", sha: Sha),
            Sidecar(version: "1.0.0", sha: Sha2),
        };
        var c = CatalogGenerator.Build(sidecars, "owner", FixedNow);
        Assert.Equal("1.0.0", c.Products[0].DefaultRelease);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.0.0", "1.0.0",  0)]
    [InlineData("2.0.0", "1.9.9",  1)]
    [InlineData("1.0.0-rc1", "1.0.0", -1)]
    [InlineData("1.0.0-rc1", "1.0.0-rc2", -1)]
    [InlineData("1.0", "1.0.0", 0)] // missing components treated as 0
    public void SemVerCompare_examples(string a, string b, int sign)
    {
        var got = CatalogGenerator.SemVerCompare(a, b);
        Assert.Equal(Math.Sign(sign), Math.Sign(got));
    }

    [Fact]
    public void Build_groups_multiple_products()
    {
        var sidecars = new[]
        {
            Sidecar(productId: "ci-clop",  version: "1.0.0", partNumber: "PY32F002Ax5"),
            Sidecar(productId: "headlamp", version: "2.0.0", partNumber: "STM32F103C8",
                     bmpMatch: "STM32F1", flashKb: 64, sha: Sha2),
        };
        var c = CatalogGenerator.Build(sidecars, "owner", FixedNow);

        Assert.Equal(2, c.Products.Count);
        Assert.NotNull(c.FindProduct("ci-clop"));
        Assert.NotNull(c.FindProduct("headlamp"));
        var headlamp = c.FindProduct("headlamp")!;
        Assert.Equal("STM32F1", headlamp.Target.BmpMatch);
        Assert.Equal(64, headlamp.Target.FlashKb);
        Assert.Equal("owner/headlamp-firmware", headlamp.Releases[0].ElfSource!.Repo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Published_ci_clop_flash_metadata_is_corrected_regardless_of_input_order(
        bool reverseInput)
    {
        var sidecars = new[]
        {
            Sidecar(version: "1.0.0", flashKb: 32, sha: CiClopV100Sha),
            // v1.0.1 published the same stale v1.0.0 sidecar and is deduplicated.
            Sidecar(version: "1.0.0", flashKb: 32, sha: CiClopV100Sha),
            Sidecar(version: "1.0.2", flashKb: 32, sha: CiClopV102Sha),
            Sidecar(version: "1.0.3", flashKb: 32, sha: CiClopV103Sha),
            Sidecar(version: "1.0.4", flashKb: 20, sha: CiClopV104Sha),
        };
        if (reverseInput)
            Array.Reverse(sidecars);

        var c = CatalogGenerator.Build(sidecars, "owner", FixedNow);
        var p = c.Products[0];

        Assert.Equal("1.0.4", p.DefaultRelease);
        Assert.Equal(20, p.Target.FlashKb);
        Assert.Equal(new[] { "1.0.0", "1.0.2", "1.0.3", "1.0.4" },
            p.Releases.Select(r => r.Version).ToArray());
        Assert.Equal(new[] { CiClopV100Sha, CiClopV102Sha, CiClopV103Sha, CiClopV104Sha },
            p.Releases.Select(r => r.ElfSha256).ToArray());
    }

    [Fact]
    public void Unknown_release_cannot_use_legacy_flash_correction()
    {
        var sidecars = new[]
        {
            Sidecar(version: "1.0.3", flashKb: 32, sha: Sha),
            Sidecar(version: "1.0.4", flashKb: 20, sha: CiClopV104Sha),
        };
        var ex = Assert.Throws<CatalogGeneratorException>(
            () => CatalogGenerator.Build(sidecars, "owner", FixedNow));
        Assert.Contains("disagree on flash_kb", ex.Message);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    public void Known_legacy_release_rejects_unexpected_flash_metadata(int flashKb)
    {
        var ex = Assert.Throws<CatalogGeneratorException>(() =>
            CatalogGenerator.Build(
                new[] { Sidecar(version: "1.0.3", flashKb: flashKb, sha: CiClopV103Sha) },
                "owner",
                FixedNow));
        Assert.Contains("unexpected flash_kb", ex.Message);
    }

    [Fact]
    public void Same_product_with_inconsistent_target_override_throws()
    {
        var sidecars = new[]
        {
            Sidecar(version: "1.0.0", frequencyHz: 1_000_000),
            Sidecar(version: "1.0.1", frequencyHz: 4_000_000, sha: Sha2),
        };
        var ex = Assert.Throws<CatalogGeneratorException>(
            () => CatalogGenerator.Build(sidecars, "owner", FixedNow));
        Assert.Contains("frequency_hz", ex.Message);
    }

    [Theory]
    [InlineData("flash_origin")]
    [InlineData("ram_origin")]
    [InlineData("ram_kb")]
    public void Same_product_with_inconsistent_memory_map_throws(string field)
    {
        var first = Sidecar(version: "1.0.0", flashOrigin: 0x08000000, ramOrigin: 0x20000000, ramKb: 3);
        var second = Sidecar(version: "1.0.1", sha: Sha2,
            flashOrigin: field == "flash_origin" ? 0x08001000UL : 0x08000000UL,
            ramOrigin: field == "ram_origin" ? 0x20001000UL : 0x20000000UL,
            ramKb: field == "ram_kb" ? 4 : 3);

        var ex = Assert.Throws<CatalogGeneratorException>(
            () => CatalogGenerator.Build(new[] { first, second }, "owner", FixedNow));
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void Duplicate_identical_sidecar_is_deduped_silently()
    {
        var s = Sidecar();
        var c = CatalogGenerator.Build(new[] { s, s }, "owner", FixedNow);
        Assert.Single(c.Products[0].Releases);
    }

    [Fact]
    public void Duplicate_conflicting_sidecar_throws()
    {
        var a = Sidecar(version: "1.0.0", sha: Sha);
        var b = Sidecar(version: "1.0.0", sha: Sha2);
        var ex = Assert.Throws<CatalogGeneratorException>(
            () => CatalogGenerator.Build(new[] { a, b }, "owner", FixedNow));
        Assert.Contains("conflicting sidecars", ex.Message);
    }

    [Fact]
    public void Duplicate_version_with_conflicting_target_metadata_throws()
    {
        var a = Sidecar(version: "1.0.0", flashKb: 32, sha: Sha);
        var b = Sidecar(version: "1.0.0", flashKb: 20, sha: Sha);
        var ex = Assert.Throws<CatalogGeneratorException>(
            () => CatalogGenerator.Build(new[] { a, b }, "owner", FixedNow));
        Assert.Contains("conflicting sidecars", ex.Message);
    }

    [Fact]
    public void Empty_sidecar_list_throws_rather_than_producing_empty_catalog()
    {
        var ex = Assert.Throws<CatalogGeneratorException>(
            () => CatalogGenerator.Build(Array.Empty<TargetSidecar>(), "owner", FixedNow));
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void Build_output_round_trips_through_CatalogJson()
    {
        var c1 = CatalogGenerator.Build(new[] { Sidecar() }, "owner", FixedNow);
        var json = CatalogJson.Write(c1);
        var c2 = CatalogJson.Parse(json);
        Assert.Equal(c1.Products.Count, c2.Products.Count);
        Assert.Equal(c1.Products[0].Releases[0].ElfSource!.Repo,
                     c2.Products[0].Releases[0].ElfSource!.Repo);
    }

    // --- ReadTargetsTree integration with disk ---

    [Fact]
    public void ReadTargetsTree_walks_subdirs_and_parses_each_target_json()
    {
        var root = Path.Combine(Path.GetTempPath(), $"iskra-targets-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "ci-clop", "v1.0.0"));
            Directory.CreateDirectory(Path.Combine(root, "ci-clop", "v1.0.1"));
            Directory.CreateDirectory(Path.Combine(root, "headlamp", "v2.0.0"));

            File.WriteAllText(Path.Combine(root, "ci-clop", "v1.0.0", "target.json"),
                ToJson("ci-clop", "1.0.0", "PY32F002Ax5", "PY32Fxxx", 32, Sha));
            File.WriteAllText(Path.Combine(root, "ci-clop", "v1.0.1", "target.json"),
                ToJson("ci-clop", "1.0.1", "PY32F002Ax5", "PY32Fxxx", 32, Sha2));
            File.WriteAllText(Path.Combine(root, "headlamp", "v2.0.0", "target.json"),
                ToJson("headlamp", "2.0.0", "STM32F103C8", "STM32F1", 64, new string('b', 64)));

            var sidecars = CatalogGenerator.ReadTargetsTree(root);
            Assert.Equal(3, sidecars.Count);
            Assert.Contains(sidecars, s => s.ProductId == "ci-clop"  && s.Version == "1.0.0");
            Assert.Contains(sidecars, s => s.ProductId == "ci-clop"  && s.Version == "1.0.1");
            Assert.Contains(sidecars, s => s.ProductId == "headlamp" && s.Version == "2.0.0");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadTargetsTree_throws_when_dir_missing()
    {
        Assert.Throws<CatalogGeneratorException>(() =>
            CatalogGenerator.ReadTargetsTree(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}")));
    }

    [Fact]
    public void ReadTargetsTree_throws_when_no_target_json_files()
    {
        var root = Path.Combine(Path.GetTempPath(), $"iskra-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<CatalogGeneratorException>(() => CatalogGenerator.ReadTargetsTree(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadTargetsTree_strict_passes_when_tag_dir_matches_version()
    {
        var root = Path.Combine(Path.GetTempPath(), $"iskra-strict-ok-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "ci-clop", "v1.0.0"));
            Directory.CreateDirectory(Path.Combine(root, "ci-clop", "v1.0.1"));
            File.WriteAllText(Path.Combine(root, "ci-clop", "v1.0.0", "target.json"),
                ToJson("ci-clop", "1.0.0", "PY32F002Ax5", "PY32Fxxx", 32, Sha));
            File.WriteAllText(Path.Combine(root, "ci-clop", "v1.0.1", "target.json"),
                ToJson("ci-clop", "1.0.1", "PY32F002Ax5", "PY32Fxxx", 32, Sha2));

            var sidecars = CatalogGenerator.ReadTargetsTree(root, strictTagMatch: true);
            Assert.Equal(2, sidecars.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ReadTargetsTree_strict_accepts_tag_dir_without_v_prefix()
    {
        var root = Path.Combine(Path.GetTempPath(), $"iskra-strict-bare-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "ci-clop", "1.0.0"));
            File.WriteAllText(Path.Combine(root, "ci-clop", "1.0.0", "target.json"),
                ToJson("ci-clop", "1.0.0", "PY32F002Ax5", "PY32Fxxx", 32, Sha));

            var sidecars = CatalogGenerator.ReadTargetsTree(root, strictTagMatch: true);
            Assert.Single(sidecars);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ReadTargetsTree_strict_throws_when_tag_dir_disagrees_with_sidecar_version()
    {
        // This is the exact v1.0.1-with-stale-v1.0.0-assets footgun we hit.
        var root = Path.Combine(Path.GetTempPath(), $"iskra-strict-bad-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "ci-clop", "v1.0.1"));
            File.WriteAllText(Path.Combine(root, "ci-clop", "v1.0.1", "target.json"),
                ToJson("ci-clop", "1.0.0", "PY32F002Ax5", "PY32Fxxx", 32, Sha));

            var ex = Assert.Throws<CatalogGeneratorException>(() =>
                CatalogGenerator.ReadTargetsTree(root, strictTagMatch: true));
            Assert.Contains("v1.0.1", ex.Message);
            Assert.Contains("1.0.0", ex.Message);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ReadTargetsTree_lenient_mode_ignores_tag_dir_mismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"iskra-lenient-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "ci-clop", "v1.0.1"));
            File.WriteAllText(Path.Combine(root, "ci-clop", "v1.0.1", "target.json"),
                ToJson("ci-clop", "1.0.0", "PY32F002Ax5", "PY32Fxxx", 32, Sha));

            // Default (lenient) mode: no exception, sidecar accepted at face value.
            var sidecars = CatalogGenerator.ReadTargetsTree(root);
            Assert.Single(sidecars);
            Assert.Equal("1.0.0", sidecars[0].Version);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TargetSidecar_rejects_missing_required_fields()
    {
        var bad = """{ "version": "1.0.0", "part_number": "X", "bmp_match": "Y", "flash_kb": 32, "elf_sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }""";
        var ex = Assert.Throws<TargetSidecarException>(() => TargetSidecar.Parse(bad));
        Assert.Contains("product_id", ex.Message);
    }

    [Fact]
    public void TargetSidecar_rejects_bad_sha()
    {
        var bad = ToJson("ci-clop", "1.0.0", "PY32F002Ax5", "PY32Fxxx", 32, "not-hex");
        var ex = Assert.Throws<TargetSidecarException>(() => TargetSidecar.Parse(bad));
        Assert.Contains("elf_sha256", ex.Message);
    }

    [Fact]
    public void TargetSidecar_parses_optional_kind_and_overrides()
    {
        var json = $$"""
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
        """;
        var s = TargetSidecar.Parse(json);
        Assert.Equal(FirmwareKind.Hex, s.FirmwareKind);
        Assert.Equal(4_000_000, s.FrequencyHz);
        Assert.Equal(PowerMode.Probe, s.PowerMode);
        Assert.True(s.ConnectReset);
        Assert.Equal(28, s.TimeoutSeconds);
    }

    private static string ToJson(string productId, string version, string partNumber, string bmpMatch, int flashKb, string sha) =>
        $$"""
        {
          "product_id":  "{{productId}}",
          "version":     "{{version}}",
          "part_number": "{{partNumber}}",
          "bmp_match":   "{{bmpMatch}}",
          "flash_kb":    {{flashKb}},
          "elf_sha256":  "{{sha}}"
        }
        """;
}
