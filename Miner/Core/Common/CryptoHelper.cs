using System.Security.Cryptography;
using System.Text;

namespace Blockchain.Shared.Common;

/// <summary>
/// Common cryptographic utilities for ECDSA (P-256) key generation, signing,
/// signature verification, and Ethereum-style address derivation.
/// </summary>
public static class CryptoHelper
{
    /// <summary>
    /// Generates a new ECDSA (P-256) keypair and derives the wallet address (0x...).
    /// </summary>
    public static (string PrivateKeyHex, string PublicKeyHex, string Address) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var privateKeyBytes = ecdsa.ExportECPrivateKey();
        var publicKeyBytes = ecdsa.ExportSubjectPublicKeyInfo();

        var privateKeyHex = Convert.ToHexString(privateKeyBytes).ToLowerInvariant();
        var publicKeyHex = Convert.ToHexString(publicKeyBytes).ToLowerInvariant();
        var address = DeriveAddress(publicKeyBytes);

        return (privateKeyHex, publicKeyHex, address);
    }

    /// <summary>
    /// Signs a data payload (such as transaction hash) using an ECDSA private key.
    /// </summary>
    public static string SignData(string privateKeyHex, string data)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(Convert.FromHexString(privateKeyHex), out _);

        var dataBytes = Encoding.UTF8.GetBytes(data);
        var signatureBytes = ecdsa.SignData(dataBytes, HashAlgorithmName.SHA256);

        return Convert.ToHexString(signatureBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Derives an Ethereum-style address from public key bytes:
    /// "0x" + first 20 bytes of SHA-256 hash of the public key.
    /// </summary>
    public static string DeriveAddress(byte[] publicKeyBytes)
    {
        var hash = SHA256.HashData(publicKeyBytes);
        var addressBytes = hash.Take(20).ToArray();
        return "0x" + Convert.ToHexString(addressBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Derives an Ethereum-style address from a hex-encoded public key.
    /// </summary>
    public static string DeriveAddress(string publicKeyHex)
    {
        var publicKeyBytes = Convert.FromHexString(publicKeyHex);
        return DeriveAddress(publicKeyBytes);
    }

    /// <summary>
    /// Verifies an ECDSA digital signature over data using a hex-encoded public key.
    /// </summary>
    public static bool VerifySignature(string publicKeyHex, string data, string signatureHex)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(publicKeyHex) ||
                string.IsNullOrWhiteSpace(data) ||
                string.IsNullOrWhiteSpace(signatureHex))
            {
                return false;
            }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromHexString(publicKeyHex), out _);

            var dataBytes = Encoding.UTF8.GetBytes(data);
            var signatureBytes = Convert.FromHexString(signatureHex);

            return ecdsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}
