using Blockchain.Shared.Common;

namespace Blockchain.Shared.Models;

public class Block
{
    public BlockHeader Header { get; set; } = new();
    public List<Transaction> Transactions { get; set; } = new();

    public Block()
    {
    }

    public Block(BlockHeader header, List<Transaction>? transactions = null)
    {
        Header = header;
        Transactions = transactions ?? new List<Transaction>();
    }

    public Block(int index, string previousHash, List<Transaction>? transactions = null, int difficulty = 4)
    {
        Transactions = transactions ?? new List<Transaction>();
        Header = new BlockHeader(index, previousHash, MerkleTree.ComputeRoot(Transactions), difficulty);
    }

    /// <summary>
    /// Computes the cryptographic Merkle root of all transactions in this block.
    /// </summary>
    public string CalculateMerkleRoot()
    {
        return MerkleTree.ComputeRoot(Transactions);
    }

    /// <summary>
    /// Checks basic cryptographic self-consistency of the block.
    /// </summary>
    public bool IsValid()
    {
        if (Header == null) return false;

        // 1. Verify Merkle root matches transactions
        if (!string.Equals(Header.MerkleRoot, CalculateMerkleRoot(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 2. Verify header hash matches recalculated hash
        if (!string.Equals(Header.Hash, Header.CalculateHash(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 3. Verify Proof-of-Work difficulty target
        var targetPrefix = new string('0', Header.Difficulty);
        return Header.Hash.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
