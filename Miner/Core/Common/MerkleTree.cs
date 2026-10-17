using System.Security.Cryptography;
using System.Text;
using Blockchain.Shared.Models;

namespace Blockchain.Shared.Common;

/// <summary>
/// Merkle tree implementation for computing cryptographic Merkle root of transactions.
/// Uses SHA-256 for pairing and duplicates the last element when a level has an odd number of nodes.
/// </summary>
public static class MerkleTree
{
    public const string EmptyRoot = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Computes the Merkle root hash for a collection of transactions by hashing each transaction's deterministic bytes.
    /// </summary>
    public static string ComputeRoot(IReadOnlyList<Transaction>? transactions)
    {
        if (transactions == null || transactions.Count == 0)
        {
            return EmptyRoot;
        }

        var leafHashes = transactions.Select(t =>
        {
            var calculatedHash = t.CalculateHash();
            if (string.IsNullOrWhiteSpace(t.Id))
            {
                t.Id = calculatedHash;
            }
            return calculatedHash.ToLowerInvariant();
        }).ToList();

        return ComputeRootFromHashes(leafHashes);
    }

    /// <summary>
    /// Computes the Merkle root from a list of leaf transaction hashes.
    /// </summary>
    public static string ComputeRootFromHashes(IReadOnlyList<string>? leafHashes)
    {
        if (leafHashes == null || leafHashes.Count == 0)
        {
            return EmptyRoot;
        }

        var currentLevel = leafHashes.Select(h => h.Trim().ToLowerInvariant()).ToList();

        while (currentLevel.Count > 1)
        {
            // If the level has an odd number of elements, duplicate the last element
            if (currentLevel.Count % 2 != 0)
            {
                currentLevel.Add(currentLevel[^1]);
            }

            var nextLevel = new List<string>(currentLevel.Count / 2);
            for (int i = 0; i < currentLevel.Count; i += 2)
            {
                var combined = currentLevel[i] + currentLevel[i + 1];
                var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
                nextLevel.Add(Convert.ToHexString(hashBytes).ToLowerInvariant());
            }

            currentLevel = nextLevel;
        }

        return currentLevel[0];
    }

    /// <summary>
    /// Computes and returns all levels of the Merkle tree from bottom (leaves = Level 0) to top (root = Level N).
    /// </summary>
    public static List<List<string>> BuildTreeLevels(IReadOnlyList<Transaction>? transactions)
    {
        var levels = new List<List<string>>();
        if (transactions == null || transactions.Count == 0)
        {
            levels.Add([EmptyRoot]);
            return levels;
        }

        var leafHashes = transactions.Select(t =>
        {
            var calculatedHash = t.CalculateHash();
            if (string.IsNullOrWhiteSpace(t.Id))
            {
                t.Id = calculatedHash;
            }
            return calculatedHash.ToLowerInvariant();
        }).ToList();

        var currentLevel = leafHashes.Select(h => h.Trim().ToLowerInvariant()).ToList();
        levels.Add(new List<string>(currentLevel));

        while (currentLevel.Count > 1)
        {
            if (currentLevel.Count % 2 != 0)
            {
                currentLevel.Add(currentLevel[^1]);
            }

            var nextLevel = new List<string>(currentLevel.Count / 2);
            for (int i = 0; i < currentLevel.Count; i += 2)
            {
                var combined = currentLevel[i] + currentLevel[i + 1];
                var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
                nextLevel.Add(Convert.ToHexString(hashBytes).ToLowerInvariant());
            }

            levels.Add(new List<string>(nextLevel));
            currentLevel = nextLevel;
        }

        return levels;
    }
}
