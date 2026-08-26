using System.Text;

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
/// One bounded catalog-file snapshot and the trust decision made over those
/// exact bytes. <see cref="CatalogBytes"/> remains untrusted unless
/// <see cref="TrustResult"/> is <see cref="CatalogTrustResult.Verified"/>, or
/// the caller has explicitly enabled the unsigned lab policy.
/// </summary>
public sealed record CatalogFileVerificationResult(
    CatalogTrustResult TrustResult,
    ReadOnlyMemory<byte>? CatalogBytes);

/// <summary>
/// File-level trust policy for <c>catalog.json</c>. The signature is a
/// base64-encoded Ed25519 signature over the raw catalog bytes, stored in a
/// sibling file <c>catalog.json.sig</c>.
/// </summary>
public static class CatalogTrust
{
    /// <summary>
    /// A base64 Ed25519 signature is 88 ASCII characters. The larger bound
    /// permits a BOM and surrounding whitespace without allowing an unbounded
    /// sidecar read.
    /// </summary>
    public const int MaxSignatureFileBytes = 1024;

    /// <summary>
    /// Second explicit opt-in used only by a binary compiled with
    /// <c>IskraEnableLabCatalogs=true</c>. Release builds compile the feature
    /// out, so an operator-controlled environment variable cannot bypass the
    /// catalog trust root.
    /// </summary>
    public const string UnsignedLabModeEnvironmentVariable = "ISKRA_LAB_ALLOW_UNSIGNED_CATALOG";

#if ISKRA_LAB_CATALOGS
    public static bool UnsignedLabFeatureCompiled => true;
#else
    public static bool UnsignedLabFeatureCompiled => false;
#endif

    public static bool IsUnsignedLabModeEnabled()
    {
#if ISKRA_LAB_CATALOGS
        var value = Environment.GetEnvironmentVariable(UnsignedLabModeEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

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
        => ReadAndVerifyCatalogFile(catalogPath, requireSigned, publicKey).TrustResult;

    /// <summary>
    /// Captures a catalog once, within <see cref="CatalogJson.MaxCatalogBytes"/>,
    /// and verifies the sibling signature over that same byte buffer. Callers
    /// can subsequently deserialize <see cref="CatalogFileVerificationResult.CatalogBytes"/>
    /// without reopening the path.
    /// </summary>
    public static CatalogFileVerificationResult ReadAndVerifyCatalogFile(
        string catalogPath,
        bool requireSigned,
        byte[]? publicKey = null)
    {
        publicKey ??= EmbeddedPublicKey;
        var sigPath = SignaturePathFor(catalogPath);

        byte[] catalogBytes;
        try
        {
            catalogBytes = BoundedFileReader.ReadAllBytes(
                catalogPath,
                CatalogJson.MaxCatalogBytes);
        }
        catch (IOException)
        {
            return new CatalogFileVerificationResult(CatalogTrustResult.IoError, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new CatalogFileVerificationResult(CatalogTrustResult.IoError, null);
        }

        var snapshot = new ReadOnlyMemory<byte>(catalogBytes);
        if (!File.Exists(sigPath))
        {
            return new CatalogFileVerificationResult(
                requireSigned
                    ? CatalogTrustResult.UnsignedRejected
                    : CatalogTrustResult.UnsignedAllowed,
                snapshot);
        }

        if (publicKey is null)
        {
            return new CatalogFileVerificationResult(
                CatalogTrustResult.NoPublicKeyConfigured,
                snapshot);
        }

        byte[] encodedSignature;
        try
        {
            encodedSignature = BoundedFileReader.ReadAllBytes(
                sigPath,
                MaxSignatureFileBytes);
        }
        catch (IOException)
        {
            return new CatalogFileVerificationResult(CatalogTrustResult.IoError, snapshot);
        }
        catch (UnauthorizedAccessException)
        {
            return new CatalogFileVerificationResult(CatalogTrustResult.IoError, snapshot);
        }

        byte[] sigBytes;
        try
        {
            var signatureText = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(encodedSignature)
                .TrimStart('\uFEFF')
                .Trim();
            sigBytes = Convert.FromBase64String(signatureText);
        }
        catch (DecoderFallbackException)
        {
            return new CatalogFileVerificationResult(CatalogTrustResult.BadSignature, snapshot);
        }
        catch (FormatException)
        {
            return new CatalogFileVerificationResult(CatalogTrustResult.BadSignature, snapshot);
        }

        return CatalogSignature.Verify(catalogBytes, sigBytes, publicKey)
            ? new CatalogFileVerificationResult(CatalogTrustResult.Verified, snapshot)
            : new CatalogFileVerificationResult(CatalogTrustResult.BadSignature, snapshot);
    }
}
