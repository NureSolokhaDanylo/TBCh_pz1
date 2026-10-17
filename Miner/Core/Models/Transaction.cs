using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Blockchain.Shared.Models;

public class Transaction
{
    public string Id { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
    public decimal Amount { get; set; }

    /// <summary>
    /// Transaction fee rewarded to the miner of the block containing this transaction.
    /// </summary>
    public decimal Fee { get; set; }

    /// <summary>
    /// Sequential transaction number for the sender wallet (Ethereum-style account nonce to prevent replay attacks).
    /// </summary>
    public long Nonce { get; set; }

    public DateTime Timestamp { get; set; }
    public string? Signature { get; set; }
    public string? PublicKey { get; set; }

    public Transaction()
    {
    }

    public Transaction(string sender, string recipient, decimal amount, long nonce, decimal fee = 0m)
    {
        Sender = sender;
        Recipient = recipient;
        Amount = amount;
        Fee = fee;
        Nonce = nonce;
        Timestamp = DateTime.UtcNow;
        Id = CalculateHash();
    }

    public Transaction(
        string id,
        string sender,
        string recipient,
        decimal amount,
        long nonce,
        DateTime timestamp,
        string? signature = null,
        string? publicKey = null,
        decimal fee = 0m)
    {
        Id = id;
        Sender = sender;
        Recipient = recipient;
        Amount = amount;
        Fee = fee;
        Nonce = nonce;
        Timestamp = timestamp.ToUniversalTime();
        Signature = signature;
        PublicKey = publicKey;
    }

    /// <summary>
    /// Deterministically serializes the transaction fields into an identical byte sequence across all nodes.
    /// Includes Sender, Recipient, Amount, Fee, Nonce, and UTC Timestamp.
    /// </summary>
    public byte[] ToBytes()
    {
        var canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"{(Sender ?? string.Empty).Trim().ToLowerInvariant()}:{(Recipient ?? string.Empty).Trim().ToLowerInvariant()}:{Amount.ToString("G29", CultureInfo.InvariantCulture)}:{Fee.ToString("G29", CultureInfo.InvariantCulture)}:{Nonce}:{Timestamp.ToUniversalTime():O}");
        return Encoding.UTF8.GetBytes(canonical);
    }

    /// <summary>
    /// Computes the SHA-256 hash of the deterministic byte representation of the transaction.
    /// </summary>
    public string CalculateHash()
    {
        var bytes = SHA256.HashData(ToBytes());
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Signs the transaction using the sender's private key and assigns the public key.
    /// </summary>
    public void Sign(string privateKeyHex, string publicKeyHex)
    {
        PublicKey = publicKeyHex;
        Id = CalculateHash();
        Signature = Common.CryptoHelper.SignData(privateKeyHex, Id);
    }

    /// <summary>
    /// Verifies that the signature matches the public key, the public key matches the sender address,
    /// and the signed payload matches the current transaction fields (detecting any post-signing tampering).
    /// </summary>
    public bool VerifySignature(string? explicitPublicKey = null)
    {
        var pubKey = explicitPublicKey ?? PublicKey;
        if (string.IsNullOrWhiteSpace(pubKey) || string.IsNullOrWhiteSpace(Signature))
        {
            return false;
        }

        // 1. Verify that the public key matches the sender address
        var derivedAddress = Common.CryptoHelper.DeriveAddress(pubKey);
        if (!string.Equals(derivedAddress, Sender?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 2. Recompute current payload hash so any modification to Amount/Fee/Recipient after signing fails signature check
        var currentHash = CalculateHash();
        if (!string.IsNullOrWhiteSpace(Id) && !string.Equals(Id, currentHash, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Common.CryptoHelper.VerifySignature(pubKey, currentHash, Signature) ||
               Common.CryptoHelper.VerifySignatureBytes(pubKey, ToBytes(), Signature);
    }
}
