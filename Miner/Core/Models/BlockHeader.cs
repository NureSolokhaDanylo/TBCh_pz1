using System.Security.Cryptography;
using System.Text;

namespace Blockchain.Shared.Models;

public class BlockHeader
{
    public int Index { get; set; }
    public string PreviousHash { get; set; } = string.Empty;
    public string MerkleRoot { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    
    /// <summary>
    /// Value for Proof-of-Work mining (Mining Nonce).
    /// </summary>
    public long Nonce { get; set; }
    public int Difficulty { get; set; }
    public string Hash { get; set; } = string.Empty;

    public BlockHeader()
    {
    }

    public BlockHeader(int index, string previousHash, string merkleRoot, int difficulty = 4)
    {
        Index = index;
        PreviousHash = previousHash;
        MerkleRoot = merkleRoot;
        Timestamp = DateTime.UtcNow;
        Nonce = 0;
        Difficulty = difficulty;
    }

    public BlockHeader(int index, string previousHash, string merkleRoot, DateTime timestamp, long nonce, int difficulty, string hash)
    {
        Index = index;
        PreviousHash = previousHash;
        MerkleRoot = merkleRoot;
        Timestamp = timestamp;
        Nonce = nonce;
        Difficulty = difficulty;
        Hash = hash;
    }

    /// <summary>
    /// Computes the SHA-256 hash of the block header.
    /// Canonical data: Index + PreviousHash + MerkleRoot + Timestamp + Difficulty + Nonce.
    /// </summary>
    public string CalculateHash()
    {
        var rawData = $"{Index}:{PreviousHash}:{MerkleRoot}:{Timestamp:O}:{Difficulty}:{Nonce}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawData));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Performs Proof-of-Work by iteratively incrementing Nonce until the computed hash
    /// starts with the required number of leading zeroes specified by difficulty.
    /// </summary>
    public bool Mine(int difficulty, CancellationToken cancellationToken = default)
    {
        Difficulty = difficulty;
        var targetPrefix = new string('0', difficulty);

        while (!cancellationToken.IsCancellationRequested)
        {
            var hash = CalculateHash();
            if (hash.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                Hash = hash;
                return true;
            }

            Nonce++;
        }

        return false;
    }
}
