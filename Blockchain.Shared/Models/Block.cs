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
        Header = new BlockHeader(index, previousHash, string.Empty, difficulty);
    }

    public string CalculateMerkleRoot()
    {
        throw new NotImplementedException();
    }

    public bool IsValid()
    {
        throw new NotImplementedException();
    }
}
