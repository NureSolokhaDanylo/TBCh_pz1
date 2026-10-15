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

    public string CalculateHash()
    {
        throw new NotImplementedException();
    }

    public void Mine(int difficulty)
    {
        throw new NotImplementedException();
    }
}
