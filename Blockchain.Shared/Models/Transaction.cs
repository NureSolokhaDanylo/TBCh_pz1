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
    /// Sequential transaction number for the sender wallet (Ethereum-style account nonce to prevent replay attacks).
    /// </summary>
    public long Nonce { get; set; }
    
    public DateTime Timestamp { get; set; }
    public string? Signature { get; set; }

    public Transaction()
    {
    }

    public Transaction(string sender, string recipient, decimal amount, long nonce)
    {
        Sender = sender;
        Recipient = recipient;
        Amount = amount;
        Nonce = nonce;
        Timestamp = DateTime.UtcNow;
        Id = CalculateHash();
    }

    public Transaction(string id, string sender, string recipient, decimal amount, long nonce, DateTime timestamp, string? signature = null)
    {
        Id = id;
        Sender = sender;
        Recipient = recipient;
        Amount = amount;
        Nonce = nonce;
        Timestamp = timestamp;
        Signature = signature;
    }

    /// <summary>
    /// Computes the SHA-256 hash of transaction data (Sender + Recipient + Amount + Nonce + Timestamp).
    /// </summary>
    public string CalculateHash()
    {
        var rawData = $"{Sender}:{Recipient}:{Amount}:{Nonce}:{Timestamp:O}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void Sign(string privateKey)
    {
        // Stub for manual implementation in miner when needed
        throw new NotImplementedException();
    }

    public bool VerifySignature(string publicKey)
    {
        // Stub for signature verification
        throw new NotImplementedException();
    }
}
