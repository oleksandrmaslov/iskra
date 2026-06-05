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
/// <para>Per-product fields: <c>display_name</c> = sidecar value if any, else
/// title-cased product_id. <c>default_release</c> = highest version per
/// product, ranked by <see cref="SemVerCompare"/>.</para>
/// </summary>
public static class CatalogGenerator
{
    /// <param name="sidecars">All known target.json sidecars across products and versions.</param>
    /// <param name="owner">GitHub owner / org for <c>elf_source.repo</c> derivation.</param>
    /// <param name="generatedAtUtc">Stamps <c>catalog.generated_at</c>.</param>
    public static Catalog Build(
        IEnumerable<TargetSidecar> sidecars,
        string owner,
        DateTime generatedAtUtc,
        IReadOnlyList<RevokedRelease>? revoked = null)
    {
        if (sidecars is null) throw new ArgumentNullException(nameof(sidecars));
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("owner required", nameof(owner));

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
            // All sidecars under one product must agree on target stack.
            EnsureTargetStackConsistent(productId, list);

            var canonical = list[0];
            var releases = list
                .OrderBy(s => s, SemVerComparer.Instance)
                .Select(s => ToRelease(s, owner))
                .ToList();
            var latest = list.OrderByDescending(s => s, SemVerComparer.Instance).First();

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
                                    canonical.TimeoutSeconds),
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

    private static FirmwareRelease ToRelease(TargetSidecar s, string owner)
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
                              Repo:  $"{owner}/{s.ProductId}-firmware",
                              Tag:   $"v{s.Version}",
                              Asset: asset),
            FirmwareKind: s.FirmwareKind);
    }

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
