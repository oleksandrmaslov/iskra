namespace Iskra.Core;

public sealed class CatalogGeneratorException : Exception
{
    public CatalogGeneratorException(string msg, Exception? inner = null) : base(msg, inner) { }
}

/// <summary>
/// Builds a <see cref="Catalog"/> from a set of <see cref="TargetSidecar"/>
/// records (one per (product, version)). The output is signing-ready —
/// pass it to <see cref="CatalogJson.WriteUtf8"/> then sign with
/// <see cref="CatalogSignature.Sign"/>.
/// <para>Per-release fields are derived from the sidecar + owner convention:
/// <c>elf_filename</c> = <c>&lt;product_id&gt;_v&lt;version&gt;_&lt;part_number&gt;.&lt;kind&gt;</c>,
/// <c>elf_source.repo</c> = <c>&lt;owner&gt;/&lt;product_id&gt;-firmware</c>,
/// <c>elf_source.tag</c> = <c>v&lt;version&gt;</c>,
/// <c>elf_source.asset</c> = same as <c>elf_filename</c>.</para>
/// <para>Target fields must agree across sidecars. A finite allowlist corrects
/// known ci-clop release identities whose immutable historical sidecars said
/// 32 KiB even though the PY32F002Ax5 linker target has always been 20 KiB.
/// Every other target difference remains an error. <c>display_name</c> falls
/// back to the title-cased product_id and <c>default_release</c> is the highest
/// version by <see cref="SemVerCompare"/>.</para>
/// </summary>
public static class CatalogGenerator
{
    /// <param name="sidecars">All known target.json sidecars across products and versions.</param>
    /// <param name="owner">GitHub owner / org for <c>elf_source.repo</c> derivation.</param>
    /// <param name="generatedAtUtc">Stamps <c>catalog.generated_at</c>.</param>
    /// <param name="revoked">Releases the catalog must refuse to flash.</param>
    /// <param name="distributionRepo">
    /// Optional <c>owner/repo</c> that publishes the built firmware artefacts.
    ///
    /// <para>When set, every <c>elf_source.repo</c> points here instead of at
    /// the per-product source repository. That is what lets an operator be
    /// granted the flashable binaries without being granted the firmware
    /// source: GitHub read access is repository-wide and cannot be narrowed to
    /// "releases only", so the separation has to be a separate repository.</para>
    ///
    /// <para>Omit it to keep the historical
    /// <c>&lt;owner&gt;/&lt;product_id&gt;-firmware</c> convention.</para>
    /// </param>
    public static Catalog Build(
        IEnumerable<TargetSidecar> sidecars,
        string owner,
        DateTime generatedAtUtc,
        IReadOnlyList<RevokedRelease>? revoked = null,
        string? distributionRepo = null)
    {
        if (sidecars is null) throw new ArgumentNullException(nameof(sidecars));
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("owner required", nameof(owner));
        if (distributionRepo is not null)
        {
            distributionRepo = distributionRepo.Trim();
            // A malformed value here would silently redirect every station's
            // firmware download, so it is rejected rather than normalised.
            var parts = distributionRepo.Split('/');
            if (parts.Length != 2 ||
                string.IsNullOrWhiteSpace(parts[0]) ||
                string.IsNullOrWhiteSpace(parts[1]))
                throw new ArgumentException(
                    $"distributionRepo must be 'owner/repo', got '{distributionRepo}'",
                    nameof(distributionRepo));
        }

        var byProduct = new Dictionary<string, List<TargetSidecar>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sidecars)
        {
            if (!byProduct.TryGetValue(s.ProductId, out var list))
                byProduct[s.ProductId] = list = new List<TargetSidecar>();
            // Reject same (product, version) appearing twice with conflicting fields.
            var existing = list.FirstOrDefault(x =>
                string.Equals(x.Version, s.Version, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (!existing.Equals(s))
                    throw new CatalogGeneratorException(
                        $"conflicting sidecars for {s.ProductId} v{s.Version}");
                continue;
            }
            list.Add(s);
        }

        if (byProduct.Count == 0)
            throw new CatalogGeneratorException("no sidecars provided — catalog would be empty");

        var products = new List<Product>(byProduct.Count);
        foreach (var (productId, list) in byProduct.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            // Apply only reviewed, identity-bound corrections before checking
            // that hardware and flashing policy do not drift between releases.
            var corrected = list.Select(ApplyKnownTargetMetadataCorrection).ToList();
            EnsureTargetStackConsistent(productId, corrected);

            var canonical = corrected[0];
            var releases = corrected
                .OrderBy(s => s, SemVerComparer.Instance)
                .Select(s => ToRelease(s, owner, distributionRepo))
                .ToList();
            var latest = corrected.OrderByDescending(s => s, SemVerComparer.Instance).First();

            var displayName = !string.IsNullOrWhiteSpace(canonical.DisplayName)
                ? canonical.DisplayName
                : TitleCase(productId);

            products.Add(new Product(
                ProductId:      productId,
                DisplayName:    displayName,
                Target:         new TargetDescriptor(
                                    canonical.BmpMatch,
                                    canonical.PartNumber,
                                    canonical.FlashKb,
                                    canonical.FrequencyHz,
                                    canonical.PowerMode,
                                    canonical.ConnectReset,
                                    canonical.TimeoutSeconds,
                                    canonical.FlashOrigin,
                                    canonical.RamOrigin,
                                    canonical.RamKb),
                Releases:       releases,
                DefaultRelease: latest.Version));
        }

        var catalog = new Catalog(
            SchemaVersion: CatalogJson.CurrentSchemaVersion,
            GeneratedAt:   generatedAtUtc,
            Products:      products,
            Revoked:       revoked is null || revoked.Count == 0 ? null : revoked);

        // Run the catalog validator on the way out — if we produced something
        // unparseable, fail loudly inside CI rather than at app startup.
        CatalogJson.Validate(catalog);
        return catalog;
    }

    /// <summary>
    /// Loads a <c>revoked.json</c> sidecar (JSON array of
    /// <see cref="RevokedRelease"/> records) into a strongly-typed list, or
    /// returns an empty list if the file is missing. Throws
    /// <see cref="CatalogGeneratorException"/> for malformed input. Called from
    /// CI before <see cref="Build"/> so the production catalog carries the
    /// signed revocation list.
    /// </summary>
    public static IReadOnlyList<RevokedRelease> ReadRevokedFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return Array.Empty<RevokedRelease>();
        try
        {
            var json = File.ReadAllText(path);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<RevokedRelease>>(
                json, CatalogJson.DefaultOptions);
            return list ?? new List<RevokedRelease>();
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new CatalogGeneratorException($"{path}: invalid revoked.json — {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Reads every <c>target.json</c> under <paramref name="rootDir"/> (recursive)
    /// and returns the parsed sidecars. Filename must literally be <c>target.json</c>.
    /// <para>When <paramref name="strictTagMatch"/> is set, the path must look
    /// like <c>&lt;root&gt;/&lt;product&gt;/&lt;tag&gt;/target.json</c>, and each
    /// sidecar's <c>version</c> field must equal <c>tag</c> (optional leading
    /// <c>v</c> stripped). This catches the failure mode where a release tag
    /// gets bumped but its <c>target.json</c> asset is left over from the
    /// previous version — silent "vX.Y.Z disappeared from catalog" turns into
    /// a loud build error.</para>
    /// </summary>
    public static List<TargetSidecar> ReadTargetsTree(string rootDir, bool strictTagMatch = false)
    {
        if (!Directory.Exists(rootDir))
            throw new CatalogGeneratorException($"targets directory not found: {rootDir}");
        var files = Directory.GetFiles(rootDir, "target.json", SearchOption.AllDirectories);
        if (files.Length == 0)
            throw new CatalogGeneratorException($"no target.json files under {rootDir}");
        var result = new List<TargetSidecar>(files.Length);
        foreach (var f in files)
        {
            var s = TargetSidecar.ParseFile(f);
            if (strictTagMatch)
            {
                var tagDir = Path.GetFileName(Path.GetDirectoryName(f)) ?? "";
                var expected = tagDir.StartsWith('v') || tagDir.StartsWith('V')
                    ? tagDir[1..] : tagDir;
                if (!string.Equals(expected, s.Version, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CatalogGeneratorException(
                        $"{f}: tag directory '{tagDir}' implies version '{expected}', " +
                        $"but target.json declares version '{s.Version}'. " +
                        "Re-cut the release with matching assets, or omit --strict-tag-match.");
                }
            }
            result.Add(s);
        }
        return result;
    }

    private static FirmwareRelease ToRelease(TargetSidecar s, string owner, string? distributionRepo)
    {
        var asset = $"{s.ProductId}_v{s.Version}_{s.PartNumber}.{ExtensionFor(s.FirmwareKind)}";
        return new FirmwareRelease(
            Version:      s.Version,
            ElfFilename:  asset,
            ElfSha256:    s.ElfSha256.ToLowerInvariant(),
            ElfUrl:       null,
            ReleasedAt:   (s.ReleasedAt ?? DateTime.UtcNow).ToUniversalTime(),
            Notes:        s.Notes,
            ElfSource:    new GitHubReleaseRef(
                              Repo:  distributionRepo ?? $"{owner}/{s.ProductId}-firmware",
                              // A per-product source repo owns its tag namespace, so
                              // plain "v1.0.3" is unambiguous there. A shared
                              // distribution repo is not: two products releasing the
                              // same version would land on one tag, and re-cutting
                              // either release would destroy the other's assets.
                              Tag:   distributionRepo is null
                                         ? $"v{s.Version}"
                                         : $"{s.ProductId}-v{s.Version}",
                              Asset: asset),
            FirmwareKind: s.FirmwareKind);
    }

    private static TargetSidecar ApplyKnownTargetMetadataCorrection(TargetSidecar s)
    {
        if (!string.Equals(s.ProductId, "ci-clop", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(s.PartNumber, "PY32F002Ax5", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(s.BmpMatch, "PY32Fxxx", StringComparison.OrdinalIgnoreCase) ||
            !IsKnownCiClopLegacyRelease(s.Version, s.ElfSha256))
            return s;

        return s.FlashKb switch
        {
            20 => s,
            32 => s with { FlashKb = 20 },
            _ => throw new CatalogGeneratorException(
                $"{s.ProductId} v{s.Version}: known legacy release has unexpected flash_kb {s.FlashKb}")
        };
    }

    private static bool IsKnownCiClopLegacyRelease(string version, string sha256) =>
        (string.Equals(version, "1.0.0", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(sha256, "4514acf573a17487db6ccf52b9e4ef2840bf59c4d743789ee6715eaf5655f2cd",
             StringComparison.OrdinalIgnoreCase)) ||
        (string.Equals(version, "1.0.2", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(sha256, "60743a9c3ef6f40e6b707a2b4f8b201875c7954fe52fdf752f4864296ed3dfeb",
             StringComparison.OrdinalIgnoreCase)) ||
        (string.Equals(version, "1.0.3", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(sha256, "403355ab8371c624af5c5cd5b01109c2734a6e31c81a89b18bbab8b9ea23423f",
             StringComparison.OrdinalIgnoreCase));

    private static void EnsureTargetStackConsistent(string productId, List<TargetSidecar> list)
    {
        var first = list[0];
        foreach (var s in list)
        {
            if (!string.Equals(s.BmpMatch, first.BmpMatch, StringComparison.OrdinalIgnoreCase))
                throw new CatalogGeneratorException(
                    $"{productId}: sidecars disagree on bmp_match ('{first.BmpMatch}' vs '{s.BmpMatch}')");
            if (!string.Equals(s.PartNumber, first.PartNumber, StringComparison.OrdinalIgnoreCase))
                throw new CatalogGeneratorException(
                    $"{productId}: sidecars disagree on part_number ('{first.PartNumber}' vs '{s.PartNumber}')");
            if (s.FlashKb != first.FlashKb)
                throw new CatalogGeneratorException(
                    $"{productId}: sidecars disagree on flash_kb ({first.FlashKb} vs {s.FlashKb})");
            if (s.FrequencyHz != first.FrequencyHz)
                throw new CatalogGeneratorException(
                    $"{productId}: sidecars disagree on frequency_hz ({first.FrequencyHz} vs {s.FrequencyHz})");
            if (s.PowerMode != first.PowerMode)
                throw new CatalogGeneratorException(
                    $"{productId}: sidecars disagree on power_mode ({first.PowerMode} vs {s.PowerMode})");
            if (s.ConnectReset != first.ConnectReset)
                throw new CatalogGeneratorException(
                    $"{productId}: sidecars disagree on connect_reset ({first.ConnectReset} vs {s.ConnectReset})");
            if (s.TimeoutSeconds != first.TimeoutSeconds)
                throw new CatalogGeneratorException(
                    $"{productId}: sidecars disagree on timeout_s ({first.TimeoutSeconds} vs {s.TimeoutSeconds})");
            if (s.FlashOrigin != first.FlashOrigin)
                throw new CatalogGeneratorException(
                    $"{productId}: sidecars disagree on flash_origin ({first.FlashOrigin} vs {s.FlashOrigin})");
            if (s.RamOrigin != first.RamOrigin)
                throw new CatalogGeneratorException(
                    $"{productId}: sidecars disagree on ram_origin ({first.RamOrigin} vs {s.RamOrigin})");
            if (s.RamKb != first.RamKb)
                throw new CatalogGeneratorException(
                    $"{productId}: sidecars disagree on ram_kb ({first.RamKb} vs {s.RamKb})");
        }
    }

    internal static string ExtensionFor(FirmwareKind kind) => kind switch
    {
        FirmwareKind.Elf => "elf",
        FirmwareKind.Hex => "hex",
        _                => "elf",
    };

    private static string TitleCase(string productId)
    {
        // ci-clop → Ci-Clop. Operators see DisplayName in the WPF dropdown;
        // a sidecar can override via the optional display_name field.
        var parts = productId.Split('-');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant();
        }
        return string.Join("-", parts);
    }

    /// <summary>
    /// Loose semver compare — splits on dots, parses ints, ignores pre-release
    /// suffixes after a dash. Sidecars are expected to use semver-ish tags
    /// (the firmware-side convention is <c>v1.0.0</c>, <c>v1.0.0-rc1</c>, etc.).
    /// </summary>
    public static int SemVerCompare(string a, string b)
    {
        var (aCore, aPre) = SplitCoreAndPrerelease(a);
        var (bCore, bPre) = SplitCoreAndPrerelease(b);
        var aParts = aCore.Split('.');
        var bParts = bCore.Split('.');
        int max = Math.Max(aParts.Length, bParts.Length);
        for (int i = 0; i < max; i++)
        {
            int av = i < aParts.Length && int.TryParse(aParts[i], out var ax) ? ax : 0;
            int bv = i < bParts.Length && int.TryParse(bParts[i], out var bx) ? bx : 0;
            if (av != bv) return av.CompareTo(bv);
        }
        // SemVer: a prerelease is LOWER than its non-prerelease counterpart.
        // i.e. 1.0.0-rc1 < 1.0.0.
        if (aPre is null && bPre is not null) return  1;
        if (aPre is not null && bPre is null) return -1;
        return string.CompareOrdinal(aPre ?? "", bPre ?? "");
    }

    private static (string Core, string? Pre) SplitCoreAndPrerelease(string v)
    {
        int dash = v.IndexOf('-');
        return dash < 0 ? (v, null) : (v[..dash], v[(dash + 1)..]);
    }

    private sealed class SemVerComparer : IComparer<TargetSidecar>
    {
        public static readonly SemVerComparer Instance = new();
        public int Compare(TargetSidecar? x, TargetSidecar? y) =>
            SemVerCompare(x?.Version ?? "", y?.Version ?? "");
    }
}
