namespace Blockchain.Shared.Models;

/// <summary>
/// Hardcoded Genesis block definition (Index = 0).
/// Serves as the immutable root of the blockchain.
/// </summary>
public static class GenesisBlock
{
    public const string GenesisPreviousHash = "0000000000000000000000000000000000000000000000000000000000000000";
    public const string GenesisMerkleRoot = "0000000000000000000000000000000000000000000000000000000000000000";
    public const string GenesisHash = "000000000019d6689c085ae165831e934ff763ae46a2a6c172b3f1b60a8ce26f";

    /// <summary>
    /// Creates and returns the hardcoded Genesis block.
    /// </summary>
    public static Block Create()
    {
        var header = new BlockHeader(
            index: 0,
            previousHash: GenesisPreviousHash,
            merkleRoot: GenesisMerkleRoot,
            timestamp: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            nonce: 0,
            difficulty: 4,
            hash: GenesisHash
        );

        return new Block(header, new List<Transaction>());
    }

    /// <summary>
    /// Singleton instance of the hardcoded Genesis block.
    /// </summary>
    public static readonly Block Instance = Create();
}
