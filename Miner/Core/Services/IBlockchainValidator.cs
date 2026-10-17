using Blockchain.Shared.Models;

namespace Blockchain.Shared.Services;

/// <summary>
/// Validator interface for blockchain and block integrity.
/// Allows pluggable validation while cryptographic components (Merkle tree, balance ledger) are being built.
/// </summary>
public interface IBlockchainValidator
{
    /// <summary>
    /// Validates an entire blockchain candidate in memory.
    /// </summary>
    (bool IsValid, string? ErrorMessage) ValidateChain(IReadOnlyList<Block> chain);

    /// <summary>
    /// Validates an individual candidate block against the current chain tip.
    /// </summary>
    (bool IsValid, string? ErrorMessage) ValidateBlock(Block block, Block previousTip);

    /// <summary>
    /// Validates an entire blockchain and identifies the index of the first invalid block.
    /// Returns FirstInvalidBlockIndex = -1 if the entire chain is valid.
    /// </summary>
    (bool IsValid, int FirstInvalidBlockIndex, string? ErrorMessage) ValidateChainDetailed(IReadOnlyList<Block> chain);
}
