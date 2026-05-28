namespace Iskra.Core;

public enum CatalogTrustResult
{
    /// <summary>Signature present and valid against the configured public key.</summary>
    Verified,
    /// <summary>No <c>.sig</c> file; <c>requireSigned</c> was false so we proceed.</summary>
    UnsignedAllowed,
    /// <summary>No <c>.sig</c> file and <c>requireSigned</c> was true.</summary>
    UnsignedRejected,
    /// <summary>Signature present but did not verify against the public key.</summary>
    BadSignature,
    /// <summary>Signature present but the app has no public key configured.</summary>
    NoPublicKeyConfigured,
    /// <summary>The catalog or signature file could not be read.</summary>
    IoError,
}

/// <summary>
/// File-level trust policy for <c>catalog.json</c>. The signature is a
/// base64-encoded Ed25519 signature over the raw catalog bytes, stored in a
/// sibling file <c>catalog.json.sig</c>.
/// </summary>
public static class CatalogTrust
{
    /// <summary>
    /// Base64-encoded Ed25519 public key embedded in the app.
    /// <para>This is the DEV key. The matching private key lives at
    /// <c>~/.claude/projects/c--Users-Alexandr-iskra/keys/catalog-key.priv</c>
    /// (outside the repo) and is used by the maintainer to sign
    /// <c>examples/catalog.json</c>. Rotate to a production key before factory deployment.</para>
    /// </summary>
    public const string EmbeddedPublicKeyBase64 =
        "r2f/iFzo9R60bpup5Hzs1QoO0pvLrwCnuZ1uPM/Wark=";

    public static byte[]? EmbeddedPublicKey =>
        string.IsNullOrEmpty(EmbeddedPublicKeyBase64)
            ? null
            : Convert.FromBase64String(EmbeddedPublicKeyBase64);

    /// <summary>
    /// Hard-coded allowlist of GitHub <c>owner/repo</c> sources the app will
    /// accept signed catalogs from. Settings.json values that disagree with this
    /// list are clamped back to the first entry on load; the WPF settings UI
    /// shows the locked source read-only. Changing the allowlist requires a
    /// build of the app — settings tampering on a station cannot widen it.
    /// <para>The signature check is the actual trust root; the allowlist exists
    /// so we never make an HTTP request to a non-official endpoint.</para>
    /// </summary>
    public static readonly IReadOnlyList<(string Owner, string Repo)> AllowedCatalogSources =
        new[]
        {
            ("oleksandrmaslov", "iskra-catalog"),
        };

    /// <summary>The canonical official catalog source — first entry of the allowlist.</summary>
    public static (string Owner, string Repo) OfficialCatalogSource => AllowedCatalogSources[0];

    public static bool IsAllowedCatalogSource(string owner, string repo)
    {
        foreach (var s in AllowedCatalogSources)
        {
            if (string.Equals(s.Owner, owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Repo, repo, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static string SignaturePathFor(string catalogPath) => catalogPath + ".sig";

    public static CatalogTrustResult VerifyCatalogFile(
        string catalogPath,
        bool requireSigned,
        byte[]? publicKey = null)
    {
        publicKey ??= EmbeddedPublicKey;
        var sigPath = SignaturePathFor(catalogPath);
        var hasSig = File.Exists(sigPath);

        if (!hasSig)
            return requireSigned
                ? CatalogTrustResult.UnsignedRejected
                : CatalogTrustResult.UnsignedAllowed;

        if (publicKey is null)
            return CatalogTrustResult.NoPublicKeyConfigured;

        byte[] catalogBytes;
        byte[] sigBytes;
        try
        {
            catalogBytes = File.ReadAllBytes(catalogPath);
            sigBytes = Convert.FromBase64String(File.ReadAllText(sigPath).Trim());
        }
        catch (IOException)        { return CatalogTrustResult.IoError; }
        catch (FormatException)    { return CatalogTrustResult.BadSignature; }
        catch (UnauthorizedAccessException) { return CatalogTrustResult.IoError; }

        return CatalogSignature.Verify(catalogBytes, sigBytes, publicKey)
            ? CatalogTrustResult.Verified
            : CatalogTrustResult.BadSignature;
    }
}
