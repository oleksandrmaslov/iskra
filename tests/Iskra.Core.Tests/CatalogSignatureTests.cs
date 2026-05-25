using System.Text;
using FlashlightApp.Core;

namespace FlashlightApp.Core.Tests;

public class CatalogSignatureTests
{
    [Fact]
    public void GenerateKeypair_returns_32_byte_public_and_private_keys()
    {
        var kp = CatalogSignature.GenerateKeypair();
        Assert.Equal(CatalogSignature.PublicKeyBytes,  kp.PublicKey.Length);
        Assert.Equal(CatalogSignature.PrivateKeyBytes, kp.PrivateKey.Length);
    }

    [Fact]
    public void Sign_returns_64_byte_signature()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var msg = Encoding.UTF8.GetBytes("hello catalog");
        var sig = CatalogSignature.Sign(msg, kp.PrivateKey);
        Assert.Equal(CatalogSignature.SignatureBytes, sig.Length);
    }

    [Fact]
    public void Sign_then_Verify_round_trips()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var msg = Encoding.UTF8.GetBytes("flashlight catalog v1");
        var sig = CatalogSignature.Sign(msg, kp.PrivateKey);
        Assert.True(CatalogSignature.Verify(msg, sig, kp.PublicKey));
    }

    [Fact]
    public void Verify_rejects_tampered_message()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var msg = Encoding.UTF8.GetBytes("flashlight catalog v1");
        var sig = CatalogSignature.Sign(msg, kp.PrivateKey);

        var tampered = (byte[])msg.Clone();
        tampered[5] ^= 0xff;

        Assert.False(CatalogSignature.Verify(tampered, sig, kp.PublicKey));
    }

    [Fact]
    public void Verify_rejects_signature_from_different_keypair()
    {
        var kp1 = CatalogSignature.GenerateKeypair();
        var kp2 = CatalogSignature.GenerateKeypair();
        var msg = Encoding.UTF8.GetBytes("payload");
        var sig = CatalogSignature.Sign(msg, kp1.PrivateKey);

        Assert.False(CatalogSignature.Verify(msg, sig, kp2.PublicKey));
    }

    [Fact]
    public void Verify_rejects_garbled_signature()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var msg = Encoding.UTF8.GetBytes("payload");
        var sig = CatalogSignature.Sign(msg, kp.PrivateKey);
        sig[10] ^= 0xff;

        Assert.False(CatalogSignature.Verify(msg, sig, kp.PublicKey));
    }

    [Fact]
    public void Verify_returns_false_for_wrong_length_inputs()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var msg = Encoding.UTF8.GetBytes("payload");
        var sig = CatalogSignature.Sign(msg, kp.PrivateKey);

        Assert.False(CatalogSignature.Verify(msg, sig, new byte[10]));            // bad pub
        Assert.False(CatalogSignature.Verify(msg, new byte[10], kp.PublicKey));   // bad sig
    }

    [Fact]
    public void Sign_throws_on_wrong_length_private_key()
    {
        var msg = Encoding.UTF8.GetBytes("payload");
        Assert.Throws<ArgumentException>(() =>
            CatalogSignature.Sign(msg, new byte[10]));
    }

    [Fact]
    public void Empty_message_round_trips()
    {
        var kp = CatalogSignature.GenerateKeypair();
        var sig = CatalogSignature.Sign(Array.Empty<byte>(), kp.PrivateKey);
        Assert.True(CatalogSignature.Verify(Array.Empty<byte>(), sig, kp.PublicKey));
    }
}
