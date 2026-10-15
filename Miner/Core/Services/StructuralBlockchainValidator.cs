using Blockchain.Shared.Models;

namespace Blockchain.Shared.Services;

/// <summary>
/// Blockchain validator verifying structural continuity, Proof-of-Work difficulty targets,
/// Merkle roots, and cryptographic hash integrity.
/// </summary>
public class StructuralBlockchainValidator : IBlockchainValidator
{
    /// <inheritdoc />
    public (bool IsValid, string? ErrorMessage) ValidateChain(IReadOnlyList<Block> chain)
    {
        if (chain == null || chain.Count == 0)
        {
            return (false, "Chain is empty or null.");
        }

        // Validate Genesis block
        var genesis = chain[0];
        if (genesis.Header.Index != 0)
        {
            return (false, $"First block index must be 0, but was {genesis.Header.Index}.");
        }

        if (!string.Equals(genesis.Header.Hash, GenesisBlock.GenesisHash, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Genesis block hash '{genesis.Header.Hash}' does not match expected '{GenesisBlock.GenesisHash}'.");
        }

        if (!string.Equals(genesis.Header.PreviousHash, GenesisBlock.GenesisPreviousHash, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Genesis block previous hash '{genesis.Header.PreviousHash}' does not match expected '{GenesisBlock.GenesisPreviousHash}'.");
        }

        // Validate subsequent blocks
        for (int i = 1; i < chain.Count; i++)
        {
            var prevBlock = chain[i - 1];
            var currentBlock = chain[i];

            var (isValid, errorMessage) = ValidateBlock(currentBlock, prevBlock);
            if (!isValid)
            {
                return (false, $"Block at index {i} failed validation: {errorMessage}");
            }
        }

        return (true, null);
    }

    /// <inheritdoc />
    public (bool IsValid, string? ErrorMessage) ValidateBlock(Block block, Block previousTip)
    {
        if (block == null)
        {
            return (false, "Candidate block is null.");
        }

        if (previousTip == null)
        {
            return (false, "Previous chain tip is null.");
        }

        if (block.Header.Index != previousTip.Header.Index + 1)
        {
            return (false, $"Block index {block.Header.Index} does not match expected tip + 1 ({previousTip.Header.Index + 1}).");
        }

        if (!string.Equals(block.Header.PreviousHash, previousTip.Header.Hash, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Block PreviousHash '{block.Header.PreviousHash}' does not match current tip hash '{previousTip.Header.Hash}'.");
        }

        if (string.IsNullOrWhiteSpace(block.Header.Hash))
        {
            return (false, $"Candidate block {block.Header.Index} has an empty hash.");
        }

        // 1. Verify Merkle root matches transactions
        var computedMerkleRoot = block.CalculateMerkleRoot();
        if (!string.Equals(block.Header.MerkleRoot, computedMerkleRoot, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Block MerkleRoot '{block.Header.MerkleRoot}' does not match computed Merkle root '{computedMerkleRoot}'.");
        }

        // 2. Verify header hash integrity
        var computedHash = block.Header.CalculateHash();
        if (!string.Equals(block.Header.Hash, computedHash, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Block Hash '{block.Header.Hash}' does not match computed hash '{computedHash}'.");
        }

        // 3. Verify Proof-of-Work difficulty target
        if (block.Header.Difficulty > 0)
        {
            var targetPrefix = new string('0', block.Header.Difficulty);
            if (!block.Header.Hash.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Block Hash '{block.Header.Hash}' does not satisfy difficulty target {block.Header.Difficulty}.");
            }
        }

        return (true, null);
    }
}
