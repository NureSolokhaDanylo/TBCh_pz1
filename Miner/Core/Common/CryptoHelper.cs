using System.Security.Cryptography;
using System.Text;

namespace Blockchain.Shared.Common;

/// <summary>
/// Cryptographic utilities for ECDSA (secp256k1) key generation, signing,
/// signature verification, and address derivation.
/// </summary>
public static class CryptoHelper
{
    private static ECCurve Secp256k1Curve => ECCurve.CreateFromFriendlyName("secp256k1");

    /// <summary>
    /// Generates a new ECDSA (secp256k1) keypair and derives the wallet address (0x...).
    /// </summary>
    public static (string PrivateKeyHex, string PublicKeyHex, string Address) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(Secp256k1Curve);

        var privateKeyBytes = ecdsa.ExportECPrivateKey();
        var publicKeyBytes = ecdsa.ExportSubjectPublicKeyInfo();

        var privateKeyHex = Convert.ToHexString(privateKeyBytes).ToLowerInvariant();
        var publicKeyHex = Convert.ToHexString(publicKeyBytes).ToLowerInvariant();
        var address = DeriveAddress(publicKeyBytes);

        return (privateKeyHex, publicKeyHex, address);
    }

    /// <summary>
    /// Signs raw byte payload using an ECDSA private key.
    /// </summary>
    public static string SignBytes(string privateKeyHex, byte[] dataBytes)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(Convert.FromHexString(privateKeyHex), out _);

        var signatureBytes = ecdsa.SignData(dataBytes, HashAlgorithmName.SHA256);
        return Convert.ToHexString(signatureBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Signs a string payload (such as transaction hash) using an ECDSA private key.
    /// </summary>
    public static string SignData(string privateKeyHex, string data)
    {
        return SignBytes(privateKeyHex, Encoding.UTF8.GetBytes(data));
    }

    /// <summary>
    /// Derives a wallet address from public key bytes:
    /// "0x" + first 20 bytes of SHA-256 hash of the public key (40 hex chars).
    /// </summary>
    public static string DeriveAddress(byte[] publicKeyBytes)
    {
        var hash = SHA256.HashData(publicKeyBytes);
        var addressBytes = hash.Take(20).ToArray();
        return "0x" + Convert.ToHexString(addressBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Derives a wallet address from a hex-encoded public key.
    /// </summary>
    public static string DeriveAddress(string publicKeyHex)
    {
        var publicKeyBytes = Convert.FromHexString(publicKeyHex);
        return DeriveAddress(publicKeyBytes);
    }

    /// <summary>
    /// Verifies an ECDSA digital signature over raw bytes using a hex-encoded public key.
    /// </summary>
    public static bool VerifySignatureBytes(string publicKeyHex, byte[] dataBytes, string signatureHex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(publicKeyHex) ||
                dataBytes == null || dataBytes.Length == 0 ||
                string.IsNullOrWhiteSpace(signatureHex))
            {
                return false;
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromHexString(publicKeyHex), out _);

            var signatureBytes = Convert.FromHexString(signatureHex);
            return ecdsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies an ECDSA digital signature over string data using a hex-encoded public key.
    /// </summary>
    public static bool VerifySignature(string publicKeyHex, string data, string signatureHex)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        return VerifySignatureBytes(publicKeyHex, Encoding.UTF8.GetBytes(data), signatureHex);
    }
}
