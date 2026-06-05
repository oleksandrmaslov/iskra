namespace Iskra.Core;

public sealed record ResolveResult(
    bool Ok,
    string[]? ResolvedArgs,
    Product? Product,
    FirmwareRelease? Release,
    string? Error)
{
    public static ResolveResult Success(string[] args, Product p, FirmwareRelease r)
        => new(true, args, p, r, null);

    public static ResolveResult Passthrough(string[] args)
        => new(true, args, null, null, null);

    public static ResolveResult Failure(string error)
        => new(false, null, null, null, error);
}

/// <summary>
/// Resolves catalog-derived CLI flags. If <c>--catalog &lt;path&gt;</c> or
/// <c>--sideload-dir &lt;path&gt;</c> is present, reads a catalog, finds the product
/// (by <c>--product</c>) and a release
/// (by <c>--firmware-version</c> or the product's default), then fills in any
/// missing flags — <c>--target</c>, <c>--flash-kb</c>, <c>--firmware-version</c>,
/// <c>--firmware-sha256</c>, <c>--firmware-kind</c>, <c>--elf</c>, and optional
/// target overrides — from the catalog entry. Explicit CLI flags always win
/// (dev override).
/// </summary>
public static class CatalogResolver
{
    public static ResolveResult Resolve(string[] args)
    {
        var catalogPath = FindValue(args, "--catalog");
        var sideloadDir = FindValue(args, "--sideload-dir");
        if (catalogPath is null && sideloadDir is null) return ResolveResult.Passthrough(args);
        if (catalogPath is not null && sideloadDir is not null)
            return ResolveResult.Failure("use either --catalog or --sideload-dir, not both");

        Catalog catalog;
        string catalogDir;
        string[] resolveArgs;
        if (catalogPath is not null)
        {
            try { catalog = CatalogJson.ParseFile(catalogPath); }
            catch (CatalogParseException ex) { return ResolveResult.Failure(ex.Message); }
            catalogDir = Path.GetDirectoryName(Path.GetFullPath(catalogPath)) ?? "";
            resolveArgs = args;
        }
        else
        {
            try { catalog = SideloadCatalogBuilder.BuildFromDirectory(sideloadDir!); }
            catch (SideloadCatalogException ex) { return ResolveResult.Failure(ex.Message); }
            catalogDir = Path.GetFullPath(sideloadDir!);
            resolveArgs = StripFlag(args, "--sideload-dir").ToArray();
        }

        return ResolveWithCatalog(resolveArgs, catalog, catalogDir);
    }

    /// <summary>
    /// Pure resolution against an in-memory catalog. No IO.
    /// </summary>
    public static ResolveResult ResolveWithCatalog(string[] args, Catalog catalog, string catalogDir)
    {
        var productId = FindValue(args, "--product");
        if (productId is null)
            return ResolveResult.Failure("catalog resolution requires --product <id>");

        var product = catalog.FindProduct(productId);
        if (product is null)
            return ResolveResult.Failure($"product '{productId}' not in catalog");

        FirmwareRelease? release;
        var requestedVersion = FindValue(args, "--firmware-version");
        if (requestedVersion is not null)
        {
            release = product.FindRelease(requestedVersion);
            if (release is null)
                return ResolveResult.Failure(
                    $"{productId}: version '{requestedVersion}' not in catalog");
        }
        else
        {
            release = product.Default();
            if (release is null)
                return ResolveResult.Failure($"{productId}: no default release");
        }

        // Sprint 6: revocation gate. Refuse to resolve a revoked release so the
        // CLI never proceeds to flash. The catalog itself signs this list.
        var revocation = catalog.FindRevocation(product.ProductId, release.Version);
        if (revocation is not null)
        {
            var why = string.IsNullOrWhiteSpace(revocation.Reason)
                ? "revoked in catalog"
                : revocation.Reason;
            return ResolveResult.Failure(
                $"E_RELEASE_REVOKED: {product.ProductId} v{release.Version} {why}");
        }

        var resolved = StripFlag(args, "--catalog").ToList();
        AddIfMissing(resolved, "--target",           product.Target.BmpMatch);
        AddIfMissing(resolved, "--flash-kb",         product.Target.FlashKb.ToString());
        AddIfMissing(resolved, "--firmware-version", release.Version);
        AddIfMissing(resolved, "--firmware-sha256",  release.ElfSha256);
        AddIfMissing(resolved, "--firmware-kind",    release.FirmwareKind.ToString().ToLowerInvariant());
        if (product.Target.FrequencyHz is { } frequencyHz)
            AddIfMissing(resolved, "--freq", frequencyHz.ToString());
        if (product.Target.PowerMode is { } powerMode)
            AddIfMissing(resolved, "--power", powerMode.ToString().ToLowerInvariant());
        if (product.Target.ConnectReset == true && !resolved.Contains("--connect-reset"))
            resolved.Add("--connect-reset");
        if (product.Target.TimeoutSeconds is { } timeoutSeconds)
            AddIfMissing(resolved, "--timeout", timeoutSeconds.ToString());

        var explicitElf = FindValue(args, "--elf") is not null;
        if (!explicitElf && !release.IsRemote)
        {
            var elfPath = Path.IsPathRooted(release.ElfFilename)
                ? release.ElfFilename
                : Path.Combine(catalogDir, release.ElfFilename);
            resolved.Add("--elf");
            resolved.Add(elfPath);
        }
        // Remote releases without explicit --elf are left without --elf set;
        // the CLI is expected to call FirmwareCache.GetOrDownloadAsync and
        // inject --elf with the local cache path before FlashOptions.Parse.
        return ResolveResult.Success(resolved.ToArray(), product, release);
    }

    private static string? FindValue(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        if (i < 0 || i + 1 >= args.Length) return null;
        return args[i + 1];
    }

    private static IEnumerable<string> StripFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == flag)
            {
                i++; // skip the value too
                continue;
            }
            yield return args[i];
        }
    }

    private static void AddIfMissing(List<string> args, string flag, string value)
    {
        if (args.Contains(flag)) return;
        args.Add(flag);
        args.Add(value);
    }
}
