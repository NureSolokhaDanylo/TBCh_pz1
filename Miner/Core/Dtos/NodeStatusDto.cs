namespace Blockchain.Shared.Dtos;

/// <summary>
/// Data Transfer Object representing the status of a blockchain node.
/// </summary>
public class NodeStatusDto
{
    public string NodeId { get; set; } = string.Empty;
    public int ChainHeight { get; set; }
    public int PendingTransactionsCount { get; set; }
    public int Difficulty { get; set; }
    public bool IsMining { get; set; }
    public bool IsSyncing { get; set; }

    public NodeStatusDto()
    {
    }

    public NodeStatusDto(string nodeId, int chainHeight, int pendingTransactionsCount, int difficulty, bool isMining, bool isSyncing)
    {
        NodeId = nodeId;
        ChainHeight = chainHeight;
        PendingTransactionsCount = pendingTransactionsCount;
        Difficulty = difficulty;
        IsMining = isMining;
        IsSyncing = isSyncing;
    }
}
