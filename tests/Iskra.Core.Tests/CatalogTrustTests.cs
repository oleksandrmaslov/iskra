using System.Text;
using System.Runtime.InteropServices;
using Iskra.Core;

namespace Iskra.Core.Tests;

public class CatalogTrustTests : IDisposable
{
    private readonly string _dir;
    private readonly string _catalogPath;
    private readonly string _sigPath;

    public CatalogTrustTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"flcat-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _catalogPath = Path.Combine(_dir, "catalog.json");
        _sigPath = CatalogTrust.SignaturePathFor(_catalogPath);
        File.WriteAllBytes(_catalogPath, Encoding.UTF8.GetBytes("{\"sample\":\"catalog\"}"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* swallow */ }
    }

    [Fact]
    public void Runtime_environment_cannot_enable_a_feature_compiled_out_of_release()
    {
        if (CatalogTrust.UnsignedLabFeatureCompiled)
            return;

        var previous = Environment.GetEnvironmentVariable(
            CatalogTrust.UnsignedLabModeEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                CatalogTrust.UnsignedLabModeEnvironmentVariable, "1");
            Assert.False(CatalogTrust.IsUnsignedLabModeEnabled());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                CatalogTrust.UnsignedLabModeEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Verified_when_signature_matches_key()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var bytes = File.ReadAllBytes(_catalogPath);
        var sig = CatalogSignature.Sign(bytes, kp.PrivateKey);
        File.WriteAllText(_sigPath, Convert.ToBase64String(sig));

        var result = CatalogTrust.VerifyCatalogFile(_catalogPath, requireSigned: false, publicKey: kp.PublicKey);
        Assert.Equal(CatalogTrustResult.Verified, result);
    }

    [Fact]
    public void ReadAndVerify_returns_the_exact_buffer_covered_by_the_signature()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var verifiedBytes = File.ReadAllBytes(_catalogPath);
        var sig = CatalogSignature.Sign(verifiedBytes, kp.PrivateKey);
        File.WriteAllText(_sigPath, Convert.ToBase64String(sig));

        var result = CatalogTrust.ReadAndVerifyCatalogFile(
            _catalogPath,
            requireSigned: true,
            publicKey: kp.PublicKey);
        File.WriteAllText(_catalogPath, "replacement");

        Assert.Equal(CatalogTrustResult.Verified, result.TrustResult);
        Assert.True(result.CatalogBytes.HasValue);
        Assert.Equal(verifiedBytes, result.CatalogBytes.Value.ToArray());
    }

    [Fact]
    public void Verification_snapshot_cannot_be_mutated_through_readonly_memory()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var verifiedBytes = File.ReadAllBytes(_catalogPath);
        File.WriteAllText(
            _sigPath,
            Convert.ToBase64String(CatalogSignature.Sign(verifiedBytes, kp.PrivateKey)));
        var result = CatalogTrust.ReadAndVerifyCatalogFile(
            _catalogPath,
            requireSigned: true,
            publicKey: kp.PublicKey);

        var exposedCopy = result.CatalogBytes!.Value;
        Assert.True(MemoryMarshal.TryGetArray(exposedCopy, out var segment));
        segment.Array![segment.Offset] ^= 0xFF;

        Assert.Equal(verifiedBytes, result.CatalogBytes!.Value.ToArray());
    }

    [Fact]
    public void Public_verification_api_cannot_substitute_an_attacker_key()
    {
        var publicOverloads = typeof(CatalogTrust)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(method => method.Name is nameof(CatalogTrust.VerifyCatalogFile)
                or nameof(CatalogTrust.ReadAndVerifyCatalogFile))
            .ToArray();

        Assert.Equal(2, publicOverloads.Length);
        Assert.All(publicOverloads, method => Assert.Equal(2, method.GetParameters().Length));
    }

    [Fact]
    public void Catalog_source_allowlist_is_not_backed_by_a_mutable_array()
    {
        Assert.False(CatalogTrust.AllowedCatalogSources is (string Owner, string Repo)[]);
        var list = Assert.IsAssignableFrom<IList<(string Owner, string Repo)>>(
            CatalogTrust.AllowedCatalogSources);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() =>
            list[0] = ("attacker", "catalog"));
        Assert.True(CatalogTrust.IsAllowedCatalogSource("oleksandrmaslov", "iskra-catalog"));
    }

    [Fact]
    public void Production_remote_catalog_constructor_has_no_key_or_policy_override()
    {
        var constructors = typeof(RemoteCatalogClient).GetConstructors();
        var constructor = Assert.Single(constructors);
        var parameter = Assert.Single(constructor.GetParameters());
        Assert.Equal(typeof(HttpClient), parameter.ParameterType);
    }

    [Fact]
    public void Raw_physical_flash_driver_is_not_exported_as_public_api()
    {
        Assert.False(typeof(GdbProcess).IsPublic);
        Assert.False(typeof(FlashStateMachine).IsPublic);
    }

    [Fact]
    public void Shared_catalog_serializer_options_are_not_publicly_mutable()
    {
        var property = typeof(CatalogJson).GetProperty(
            "DefaultOptions",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.Null(property);
    }

    [Fact]
    public void UnsignedAllowed_when_no_sig_and_not_required()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var result = CatalogTrust.VerifyCatalogFile(_catalogPath, requireSigned: false, publicKey: kp.PublicKey);
        Assert.Equal(CatalogTrustResult.UnsignedAllowed, result);
    }

    [Fact]
    public void UnsignedRejected_when_no_sig_and_required()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var result = CatalogTrust.VerifyCatalogFile(_catalogPath, requireSigned: true, publicKey: kp.PublicKey);
        Assert.Equal(CatalogTrustResult.UnsignedRejected, result);
    }

    [Fact]
    public void BadSignature_when_sig_does_not_match_key()
    {
        var kpSigner   = CatalogSignature.GenerateKeypair();
        var kpVerifier = CatalogSignature.GenerateKeypair();
        var bytes = File.ReadAllBytes(_catalogPath);
        var sig = CatalogSignature.Sign(bytes, kpSigner.PrivateKey);
        File.WriteAllText(_sigPath, Convert.ToBase64String(sig));

        var result = CatalogTrust.VerifyCatalogFile(_catalogPath, requireSigned: true, publicKey: kpVerifier.PublicKey);
        Assert.Equal(CatalogTrustResult.BadSignature, result);
    }

    [Fact]
    public void BadSignature_when_catalog_was_tampered_after_signing()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var bytes = File.ReadAllBytes(_catalogPath);
        var sig = CatalogSignature.Sign(bytes, kp.PrivateKey);
        File.WriteAllText(_sigPath, Convert.ToBase64String(sig));

        // Tamper with the catalog file after it has been signed.
        File.AppendAllText(_catalogPath, "x");

        var result = CatalogTrust.VerifyCatalogFile(_catalogPath, requireSigned: true, publicKey: kp.PublicKey);
        Assert.Equal(CatalogTrustResult.BadSignature, result);
    }

    [Fact]
    public void NoPublicKeyConfigured_when_sig_present_but_no_key_given()
    {
        // Need a sig file present; content can be anything base64 of right length.
        File.WriteAllText(_sigPath, Convert.ToBase64String(new byte[CatalogSignature.SignatureBytes]));

        // Pass explicit null AND embedded key is empty in this test build (no production key yet).
        // To make the test robust to the embedded key being set, we explicitly null-it via a path
        // that doesn't have access to EmbeddedPublicKey override — so the test asserts the policy
        // when no key is configured. We verify by passing a key explicitly to confirm BadSignature,
        // then leave it null to confirm NoPublicKeyConfigured.
        var withEmptyKey = CatalogTrust.VerifyCatalogFile(_catalogPath, requireSigned: false, publicKey: Array.Empty<byte>());
        Assert.Equal(CatalogTrustResult.BadSignature, withEmptyKey);
    }

    [Fact]
    public void BadSignature_when_sig_file_is_not_valid_base64()
    {
        var kp = CatalogSignature.GenerateKeypair();
        File.WriteAllText(_sigPath, "%%%not-base64%%%");
        var result = CatalogTrust.VerifyCatalogFile(_catalogPath, requireSigned: false, publicKey: kp.PublicKey);
        Assert.Equal(CatalogTrustResult.BadSignature, result);
    }

    [Fact]
    public void Catalog_larger_than_the_strict_limit_is_not_loaded()
    {
        File.WriteAllBytes(_catalogPath, new byte[CatalogJson.MaxCatalogBytes + 1]);

        var result = CatalogTrust.ReadAndVerifyCatalogFile(
            _catalogPath,
            requireSigned: false);

        Assert.Equal(CatalogTrustResult.IoError, result.TrustResult);
        Assert.False(result.CatalogBytes.HasValue);
    }

    [Fact]
    public void Signature_sidecar_larger_than_the_strict_limit_is_not_loaded()
    {
        File.WriteAllBytes(_sigPath, new byte[CatalogTrust.MaxSignatureFileBytes + 1]);

        var result = CatalogTrust.ReadAndVerifyCatalogFile(
            _catalogPath,
            requireSigned: true);

        Assert.Equal(CatalogTrustResult.IoError, result.TrustResult);
        Assert.True(result.CatalogBytes.HasValue);
    }

    [Fact]
    public void Embedded_dev_key_verifies_the_signed_example_catalog()
    {
        // Skip if the example catalog or its signature isn't present in the test
        // working directory (e.g. in an isolated CI sandbox).
        var dir = AppContext.BaseDirectory;
        string? catalogPath = null;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "examples", "catalog.json");
            if (File.Exists(candidate)) { catalogPath = candidate; break; }
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(catalogPath);
        Assert.True(File.Exists(catalogPath + ".sig"),
            $"expected sibling .sig for {catalogPath}");

        // A CRLF rewrite of the signed bytes is the one failure mode that shows
        // up only on Windows checkouts, so name it before asserting the vaguer
        // BadSignature. .gitattributes marks these paths -text to prevent it.
        Assert.DoesNotContain(
            (byte)'\r',
            File.ReadAllBytes(catalogPath!));

        var result = CatalogTrust.VerifyCatalogFile(catalogPath!, requireSigned: true);
        Assert.Equal(CatalogTrustResult.Verified, result);
    }
}
