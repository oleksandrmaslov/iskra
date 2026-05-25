using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace FlashlightApp.Core;

/// <summary>
/// Ed25519 sign / verify over raw catalog bytes. Pure crypto — no IO, no
/// embedded key. Production trust policy (which public key to trust, what
/// to do with unsigned catalogs) lives in <see cref="CatalogTrust"/>.
/// </summary>
public static class CatalogSignature
{
    public const int PublicKeyBytes  = 32;
    public const int PrivateKeyBytes = 32;
    public const int SignatureBytes  = 64;

    public sealed record Keypair(byte[] PublicKey, byte[] PrivateKey);

    public static Keypair GenerateKeypair()
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        var pub  = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
        var priv = ((Ed25519PrivateKeyParameters)pair.Private).GetEncoded();
        return new Keypair(pub, priv);
    }

    public static byte[] Sign(byte[] message, byte[] privateKey)
    {
        if (privateKey.Length != PrivateKeyBytes)
            throw new ArgumentException(
                $"private key must be {PrivateKeyBytes} bytes (got {privateKey.Length})",
                nameof(privateKey));

        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(privateKey, 0));
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    public static bool Verify(byte[] message, byte[] signature, byte[] publicKey)
    {
        if (publicKey.Length != PublicKeyBytes) return false;
        if (signature.Length != SignatureBytes) return false;

        try
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
            verifier.BlockUpdate(message, 0, message.Length);
            return verifier.VerifySignature(signature);
        }
        catch (CryptoException)
        {
            return false;
        }
    }
}
