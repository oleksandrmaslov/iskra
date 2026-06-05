namespace Iskra.Core;

public sealed class SideloadCatalogException : Exception
{
    public SideloadCatalogException(string msg, Exception? inner = null) : base(msg, inner) { }
}

/// <summary>
/// Builds an in-memory catalog from a local folder of ad-hoc firmware artefacts.
/// Each release needs a <c>target.json</c> sidecar and a firmware file named
/// <c>&lt;product-id&gt;_v&lt;version&gt;_&lt;part-number&gt;.&lt;kind&gt;</c>. The produced
/// release uses an absolute local path and no GitHub source.
/// </summary>
public static class SideloadCatalogBuilder
{
    public static Catalog BuildFromDirectory(string sideloadDir, DateTime? generatedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sideloadDir))
            throw new SideloadCatalogException("sideload directory required");
        if (!Directory.Exists(sideloadDir))
            throw new SideloadCatalogException($"sideload directory not found: {sideloadDir}");

        var root = Path.GetFullPath(sideloadDir);
        var sidecarFiles = Directory.GetFiles(root, "target.json", SearchOption.AllDirectories);
        if (sidecarFiles.Length == 0)
            throw new SideloadCatalogException($"no target.json files under {root}");

        var entries = new List<SideloadEntry>(sidecarFiles.Length);
        foreach (var sidecarPath in sidecarFiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            TargetSidecar sidecar;
            try { sidecar = TargetSidecar.ParseFile(sidecarPath); }
            catch (TargetSidecarException ex) { throw new SideloadCatalogException(ex.Message, ex); }

            var firmwarePath = FindFirmware(root, sidecarPath, sidecar);
            entries.Add(new SideloadEntry(sidecar, firmwarePath));
        }

        return Build(entries, generatedAtUtc ?? DateTime.UtcNow);
    }

    private static Catalog Build(IReadOnlyList<SideloadEntry> entries, DateTime generatedAtUtc)
    {
        var byProduct = new Dictionary<string, List<SideloadEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!byProduct.TryGetValue(entry.Sidecar.ProductId, out var list))
                byProduct[entry.Sidecar.ProductId] = list = new List<SideloadEntry>();

            var existing = list.FirstOrDefault(x =>
                string.Equals(x.Sidecar.Version, entry.Sidecar.Version, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (!existing.Sidecar.Equals(entry.Sidecar) ||
                    !string.Equals(existing.FirmwarePath, entry.FirmwarePath, StringComparison.OrdinalIgnoreCase))
                    throw new SideloadCatalogException(
                        $"conflicting sideload entries for {entry.Sidecar.ProductId} v{entry.Sidecar.Version}");
                continue;
            }
            list.Add(entry);
        }

        var products = new List<Product>(byProduct.Count);
        foreach (var (productId, list) in byProduct.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            EnsureTargetStackConsistent(productId, list.Select(e => e.Sidecar).ToList());
            var canonical = list[0].Sidecar;
            var releases = list
                .OrderBy(e => e.Sidecar.Version, Comparer<string>.Create(CatalogGenerator.SemVerCompare))
                .Select(ToRelease)
                .ToList();
            var latest = releases
                .OrderByDescending(r => r.Version, Comparer<string>.Create(CatalogGenerator.SemVerCompare))
                .First();

            products.Add(new Product(
                ProductId: productId,
                DisplayName: string.IsNullOrWhiteSpace(canonical.DisplayName)
                    ? TitleCase(productId)
                    : canonical.DisplayName,
                Target: new TargetDescriptor(
                    canonical.BmpMatch,
                    canonical.PartNumber,
                    canonical.FlashKb,
                    canonical.FrequencyHz,
                    canonical.PowerMode,
                    canonical.ConnectReset,
                    canonical.TimeoutSeconds),
                Releases: releases,
                DefaultRelease: latest.Version));
        }

        var catalog = new Catalog(
            SchemaVersion: CatalogJson.CurrentSchemaVersion,
            GeneratedAt: generatedAtUtc.ToUniversalTime(),
            Products: products);
        CatalogJson.Validate(catalog);
        return catalog;
    }

    private static FirmwareRelease ToRelease(SideloadEntry entry)
    {
        var s = entry.Sidecar;
        return new FirmwareRelease(
            Version: s.Version,
            ElfFilename: entry.FirmwarePath,
            ElfSha256: s.ElfSha256.ToLowerInvariant(),
            ElfUrl: null,
            ReleasedAt: (s.ReleasedAt ?? DateTime.UtcNow).ToUniversalTime(),
            Notes: s.Notes,
            ElfSource: null,
            FirmwareKind: s.FirmwareKind);
    }

    private static string FindFirmware(string root, string sidecarPath, TargetSidecar sidecar)
    {
        var expected = $"{sidecar.ProductId}_v{sidecar.Version}_{sidecar.PartNumber}." +
            CatalogGenerator.ExtensionFor(sidecar.FirmwareKind);
        var sidecarDir = Path.GetDirectoryName(sidecarPath) ?? root;
        var sameDir = Path.Combine(sidecarDir, expected);
        if (File.Exists(sameDir)) return Path.GetFullPath(sameDir);

        var matches = Directory.GetFiles(root, expected, SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new SideloadCatalogException(
                $"{sidecarPath}: expected firmware file '{expected}' next to target.json or under {root}"),
            _ => throw new SideloadCatalogException(
                $"{sidecarPath}: multiple firmware files named '{expected}' under {root}"),
        };
    }

    private static void EnsureTargetStackConsistent(string productId, List<TargetSidecar> list)
    {
        var first = list[0];
        foreach (var s in list)
        {
            if (!string.Equals(s.BmpMatch, first.BmpMatch, StringComparison.OrdinalIgnoreCase))
                throw new SideloadCatalogException(
                    $"{productId}: sidecars disagree on bmp_match ('{first.BmpMatch}' vs '{s.BmpMatch}')");
            if (!string.Equals(s.PartNumber, first.PartNumber, StringComparison.OrdinalIgnoreCase))
                throw new SideloadCatalogException(
                    $"{productId}: sidecars disagree on part_number ('{first.PartNumber}' vs '{s.PartNumber}')");
            if (s.FlashKb != first.FlashKb)
                throw new SideloadCatalogException(
                    $"{productId}: sidecars disagree on flash_kb ({first.FlashKb} vs {s.FlashKb})");
            if (s.FrequencyHz != first.FrequencyHz)
                throw new SideloadCatalogException(
                    $"{productId}: sidecars disagree on frequency_hz ({first.FrequencyHz} vs {s.FrequencyHz})");
            if (s.PowerMode != first.PowerMode)
                throw new SideloadCatalogException(
                    $"{productId}: sidecars disagree on power_mode ({first.PowerMode} vs {s.PowerMode})");
            if (s.ConnectReset != first.ConnectReset)
                throw new SideloadCatalogException(
                    $"{productId}: sidecars disagree on connect_reset ({first.ConnectReset} vs {s.ConnectReset})");
            if (s.TimeoutSeconds != first.TimeoutSeconds)
                throw new SideloadCatalogException(
                    $"{productId}: sidecars disagree on timeout_s ({first.TimeoutSeconds} vs {s.TimeoutSeconds})");
        }
    }

    private static string TitleCase(string productId)
    {
        var parts = productId.Split('-');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant();
        }
        return string.Join("-", parts);
    }

    private sealed record SideloadEntry(TargetSidecar Sidecar, string FirmwarePath);
}
