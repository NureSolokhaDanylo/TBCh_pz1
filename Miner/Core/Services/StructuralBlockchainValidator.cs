using System.Numerics;
using Blockchain.Shared.Models;
using Miner.Services;

namespace Blockchain.Shared.Services;

/// <summary>
/// Full blockchain validator verifying:
/// 1. Block header hash integrity and Proof-of-Work difficulty target.
/// 2. PreviousHash cryptographic link continuity.
/// 3. MerkleRoot integrity against recalculated transaction hashes.
/// 4. ECDSA digital signatures on all user transactions.
/// 5. Strictly sequential sender nonces without gaps or replays.
/// 6. Non-negative sender balances at every block in history.
/// 7. Uninflated Coinbase miner reward (BaseReward + sum of block transaction fees).
/// </summary>
public class StructuralBlockchainValidator : IBlockchainValidator
{
    public decimal BaseBlockReward { get; set; } = 50.0m;

    /// <inheritdoc />
    public (bool IsValid, string? ErrorMessage) ValidateChain(IReadOnlyList<Block> chain)
    {
        var (isValid, _, errorMessage) = ValidateChainDetailed(chain);
        return (isValid, errorMessage);
    }

    /// <inheritdoc />
    public (bool IsValid, int FirstInvalidBlockIndex, string? ErrorMessage) ValidateChainDetailed(IReadOnlyList<Block> chain)
    {
        if (chain == null || chain.Count == 0)
        {
            return (false, 0, "Chain is empty or null.");
        }

        // Validate Genesis block (Block #0)
        var genesis = chain[0];
        if (genesis.Header.Index != 0)
        {
            return (false, 0, $"Block #0 failed validation: First block index must be 0, but was {genesis.Header.Index}.");
        }

        if (!string.Equals(genesis.Header.Hash, GenesisBlock.GenesisHash, StringComparison.OrdinalIgnoreCase))
        {
            return (false, 0, $"Block #0 failed validation: Genesis block hash '{genesis.Header.Hash}' does not match expected '{GenesisBlock.GenesisHash}'.");
        }

        if (!string.Equals(genesis.Header.PreviousHash, GenesisBlock.GenesisPreviousHash, StringComparison.OrdinalIgnoreCase))
        {
            return (false, 0, $"Block #0 failed validation: Genesis block previous hash '{genesis.Header.PreviousHash}' does not match expected '{GenesisBlock.GenesisPreviousHash}'.");
        }

        var genesisMerkle = genesis.CalculateMerkleRoot();
        if (!string.Equals(genesis.Header.MerkleRoot, genesisMerkle, StringComparison.OrdinalIgnoreCase))
        {
            return (false, 0, $"Block #0 failed validation: Genesis MerkleRoot '{genesis.Header.MerkleRoot}' does not match computed '{genesisMerkle}'.");
        }

        // State tracking from Genesis
        var balances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var nonces = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var seenTxIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Apply any Genesis transactions if present
        foreach (var tx in genesis.Transactions)
        {
            if (!string.IsNullOrWhiteSpace(tx.Recipient))
            {
                var recipient = tx.Recipient.Trim();
                balances[recipient] = balances.GetValueOrDefault(recipient, 0m) + tx.Amount;
            }
        }

        // Validate subsequent blocks (#1 .. #N)
        for (int i = 1; i < chain.Count; i++)
        {
            var prevBlock = chain[i - 1];
            var currentBlock = chain[i];

            var (isStructurallyValid, structuralError) = ValidateBlock(currentBlock, prevBlock);
            if (!isStructurallyValid)
            {
                return (false, i, $"Block #{i} failed validation: {structuralError}");
            }

            // Validate transactions and ledger state transitions inside Block #i
            var txs = currentBlock.Transactions ?? [];
            var coinbaseTxs = txs.Where(t => AccountLedger.IsSystemOrCoinbase(t.Sender)).ToList();
            var userTxs = txs.Where(t => !AccountLedger.IsSystemOrCoinbase(t.Sender)).ToList();

            if (coinbaseTxs.Count > 1)
            {
                return (false, i, $"Block #{i} failed validation: Contains {coinbaseTxs.Count} Coinbase transactions (at most 1 allowed).");
            }

            decimal totalFees = 0m;
            foreach (var utx in userTxs)
            {
                if (utx.Fee < 0)
                {
                    return (false, i, $"Block #{i} failed validation: Transaction '{utx.Id}' has negative fee {utx.Fee}.");
                }
                totalFees += utx.Fee;
            }

            if (coinbaseTxs.Count == 1)
            {
                var coinbase = coinbaseTxs[0];
                var expectedReward = BaseBlockReward + totalFees;
                if (coinbase.Amount != expectedReward)
                {
                    return (false, i, $"Block #{i} failed validation: Coinbase reward is {coinbase.Amount}, but expected {expectedReward} (base reward {BaseBlockReward} + fees {totalFees}).");
                }
            }

            foreach (var tx in txs)
            {
                var calculatedTxHash = tx.CalculateHash();
                if (!string.IsNullOrWhiteSpace(tx.Id) &&
                    !string.Equals(tx.Id, calculatedTxHash, StringComparison.OrdinalIgnoreCase))
                {
                    return (false, i, $"Block #{i} failed validation: Transaction ID '{tx.Id}' does not match calculated hash '{calculatedTxHash}' (transaction data tampered).");
                }

                if (!seenTxIds.Add(calculatedTxHash))
                {
                    return (false, i, $"Block #{i} failed validation: Duplicate transaction '{calculatedTxHash}' detected in blockchain.");
                }

                if (AccountLedger.IsSystemOrCoinbase(tx.Sender))
                {
                    if (!string.IsNullOrWhiteSpace(tx.Recipient))
                    {
                        var recipient = tx.Recipient.Trim();
                        balances[recipient] = balances.GetValueOrDefault(recipient, 0m) + tx.Amount;
                    }
                }
                else
                {
                    if (tx.Amount <= 0)
                    {
                        return (false, i, $"Block #{i} failed validation: Transaction '{calculatedTxHash}' has non-positive amount {tx.Amount}.");
                    }

                    // 4. Verify digital signature
                    if (!tx.VerifySignature())
                    {
                        return (false, i, $"Block #{i} failed validation: Transaction '{calculatedTxHash}' from '{tx.Sender}' has an invalid digital signature.");
                    }

                    var sender = tx.Sender.Trim();
                    var recipient = tx.Recipient.Trim();

                    // 5. Verify sequential sender nonce
                    var expectedNonce = nonces.GetValueOrDefault(sender, 0L);
                    if (tx.Nonce != expectedNonce)
                    {
                        return (false, i, $"Block #{i} failed validation: Transaction '{calculatedTxHash}' from '{sender}' has invalid nonce {tx.Nonce} (expected sequential nonce {expectedNonce}).");
                    }

                    // 6. Verify sender balance sufficiency (Amount + Fee)
                    var currentBalance = balances.GetValueOrDefault(sender, 0m);
                    var totalSpend = tx.Amount + tx.Fee;
                    if (currentBalance < totalSpend)
                    {
                        return (false, i, $"Block #{i} failed validation: Sender '{sender}' overspent in transaction '{calculatedTxHash}' (available balance: {currentBalance}, spent: {totalSpend}).");
                    }

                    balances[sender] = currentBalance - totalSpend;
                    nonces[sender] = expectedNonce + 1;
                    balances[recipient] = balances.GetValueOrDefault(recipient, 0m) + tx.Amount;
                }
            }
        }

        return (true, -1, null);
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

        // 2. Verify PreviousHash link
        if (!string.Equals(block.Header.PreviousHash, previousTip.Header.Hash, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Broken chain link: Block PreviousHash '{block.Header.PreviousHash}' does not match previous block hash '{previousTip.Header.Hash}'.");
        }

        if (string.IsNullOrWhiteSpace(block.Header.Hash))
        {
            return (false, $"Candidate block {block.Header.Index} has an empty hash.");
        }

        // 3. Verify Merkle root matches transactions
        var computedMerkleRoot = block.CalculateMerkleRoot();
        if (!string.Equals(block.Header.MerkleRoot, computedMerkleRoot, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Merkle root mismatch: Header MerkleRoot '{block.Header.MerkleRoot}' does not match computed Merkle root '{computedMerkleRoot}'.");
        }

        // 1. Verify header hash integrity and PoW difficulty target
        var computedHash = block.Header.CalculateHash();
        if (!string.Equals(block.Header.Hash, computedHash, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Block Hash '{block.Header.Hash}' does not match computed header hash '{computedHash}'.");
        }

        if (block.Header.Difficulty > 0)
        {
            var targetPrefix = new string('0', block.Header.Difficulty);
            if (!block.Header.Hash.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Block Hash '{block.Header.Hash}' does not satisfy Proof-of-Work difficulty target ({block.Header.Difficulty} leading zeroes).");
            }
        }

        return (true, null);
    }

    /// <summary>
    /// Automatically recalculates Proof-of-Work difficulty every K blocks (adjustmentInterval).
    /// Compares actual mining time of the last K blocks against target time (K * targetBlockTimeSeconds).
    /// </summary>
    public static int CalculateNextDifficulty(
        IReadOnlyList<Block> chain,
        int adjustmentInterval = 10,
        int targetBlockTimeSeconds = 10,
        int defaultDifficulty = 4)
    {
        if (chain == null || chain.Count == 0)
        {
            return defaultDifficulty;
        }

        var tip = chain[^1];
        var currentDifficulty = tip.Header.Difficulty > 0 ? tip.Header.Difficulty : defaultDifficulty;

        if (adjustmentInterval <= 0 || tip.Header.Index == 0 || tip.Header.Index % adjustmentInterval != 0)
        {
            return currentDifficulty;
        }

        if (chain.Count <= adjustmentInterval)
        {
            return currentDifficulty;
        }

        var startIndex = chain.Count - 1 - adjustmentInterval;
        var startBlock = chain[startIndex];
        int intervals = adjustmentInterval;

        // If startBlock is the hardcoded Genesis block (2026-01-01) and Block #1 was mined much later, measure from Block #1
        if (startIndex == 0 && chain.Count > 1)
        {
            var gapFromGenesis = (chain[1].Header.Timestamp - startBlock.Header.Timestamp).TotalSeconds;
            if (gapFromGenesis > targetBlockTimeSeconds * 100)
            {
                startBlock = chain[1];
                intervals = adjustmentInterval - 1;
            }
        }

        if (intervals <= 0)
        {
            return currentDifficulty;
        }

        var actualTimeSeconds = (tip.Header.Timestamp - startBlock.Header.Timestamp).TotalSeconds;
        var expectedTimeSeconds = (double)intervals * targetBlockTimeSeconds;

        if (actualTimeSeconds < expectedTimeSeconds / 2.0)
        {
            return currentDifficulty + 1;
        }

        if (actualTimeSeconds > expectedTimeSeconds * 2.0)
        {
            return Math.Max(1, currentDifficulty - 1);
        }

        return currentDifficulty;
    }

    /// <summary>
    /// Computes the total cumulative Proof-of-Work of a chain: Sum(16^Difficulty) over all blocks.
    /// </summary>
    public static BigInteger CalculateCumulativeWork(IReadOnlyList<Block>? chain)
    {
        if (chain == null || chain.Count == 0)
        {
            return BigInteger.Zero;
        }

        BigInteger totalWork = BigInteger.Zero;
        foreach (var block in chain)
        {
            var diff = Math.Max(0, block.Header.Difficulty);
            totalWork += BigInteger.Pow(16, diff);
        }

        return totalWork;
    }
}
