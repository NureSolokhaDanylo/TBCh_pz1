using Blockchain.Shared.Common;
using Blockchain.Shared.Models;

namespace Miner.Common;

/// <summary>
/// Static manager for network nodes (peers) and blacklist (banned nodes).
/// Persists data in JSON Lines format.
/// </summary>
public static class NodeManager
{
    private static readonly SemaphoreSlim Lock = new(1, 1);

    public static string DefaultNodesFilePath { get; set; } = "data/nodes.json";
    public static string DefaultBannedFilePath { get; set; } = "data/banned_nodes.json";

    /// <summary>
    /// Retrieves all nodes from the nodes.json file.
    /// </summary>
    public static async Task<List<TrustedNode>> GetNodesAsync(
        string? nodesFilePath = null,
        CancellationToken cancellationToken = default)
    {
        var path = nodesFilePath ?? DefaultNodesFilePath;
        await Lock.WaitAsync(cancellationToken);
        try
        {
            return await JsonLinesHelper.ReadAllAsync<TrustedNode>(path, cancellationToken: cancellationToken);
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Retrieves only verified and trusted nodes (IsTrusted == true and not banned).
    /// </summary>
    public static async Task<List<TrustedNode>> GetTrustedNodesAsync(
        string? nodesFilePath = null,
        string? bannedFilePath = null,
        CancellationToken cancellationToken = default)
    {
        var nPath = nodesFilePath ?? DefaultNodesFilePath;
        var bPath = bannedFilePath ?? DefaultBannedFilePath;

        await Lock.WaitAsync(cancellationToken);
        try
        {
            var banned = await JsonLinesHelper.ReadAllAsync<BannedNode>(bPath, cancellationToken: cancellationToken);
            var nodes = await JsonLinesHelper.ReadAllAsync<TrustedNode>(nPath, cancellationToken: cancellationToken);

            return nodes
                .Where(n => n.IsTrusted && !banned.Any(b => IsMatch(b.Host, b.Port, n.Host, n.Port)))
                .ToList();
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Retrieves the list of all banned nodes.
    /// </summary>
    public static async Task<List<BannedNode>> GetBannedNodesAsync(
        string? bannedFilePath = null,
        CancellationToken cancellationToken = default)
    {
        var path = bannedFilePath ?? DefaultBannedFilePath;
        await Lock.WaitAsync(cancellationToken);
        try
        {
            return await JsonLinesHelper.ReadAllAsync<BannedNode>(path, cancellationToken: cancellationToken);
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Checks if a node is present in the banned list.
    /// </summary>
    public static async Task<bool> IsBannedAsync(
        string host,
        int port,
        string? bannedFilePath = null,
        CancellationToken cancellationToken = default)
    {
        var path = bannedFilePath ?? DefaultBannedFilePath;
        await Lock.WaitAsync(cancellationToken);
        try
        {
            var bannedNodes = await JsonLinesHelper.ReadAllAsync<BannedNode>(path, cancellationToken: cancellationToken);
            return bannedNodes.Any(n => IsMatch(n.Host, n.Port, host, port));
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Adds a node to the peer list.
    /// Returns false if the node is already present or is in the ban list.
    /// </summary>
    public static async Task<bool> AddNodeAsync(
        TrustedNode node,
        string? nodesFilePath = null,
        string? bannedFilePath = null,
        CancellationToken cancellationToken = default)
    {
        var nPath = nodesFilePath ?? DefaultNodesFilePath;
        var bPath = bannedFilePath ?? DefaultBannedFilePath;

        await Lock.WaitAsync(cancellationToken);
        try
        {
            // 1. Verify the node is not banned
            var banned = await JsonLinesHelper.ReadAllAsync<BannedNode>(bPath, cancellationToken: cancellationToken);
            if (banned.Any(b => IsMatch(b.Host, b.Port, node.Host, node.Port)))
            {
                return false;
            }

            // 2. Verify the node does not already exist
            var existing = await JsonLinesHelper.ReadAllAsync<TrustedNode>(nPath, cancellationToken: cancellationToken);
            if (existing.Any(e => IsMatch(e.Host, e.Port, node.Host, node.Port)))
            {
                return false;
            }

            // 3. Append new entry to nodes.json
            await JsonLinesHelper.AppendAsync(nPath, node, cancellationToken: cancellationToken);
            return true;
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Bans a node: appends to banned_nodes.json and removes from nodes.json.
    /// </summary>
    public static async Task<bool> BanNodeAsync(
        string host,
        int port,
        string? reason = null,
        string? nodesFilePath = null,
        string? bannedFilePath = null,
        CancellationToken cancellationToken = default)
    {
        var nPath = nodesFilePath ?? DefaultNodesFilePath;
        var bPath = bannedFilePath ?? DefaultBannedFilePath;

        await Lock.WaitAsync(cancellationToken);
        try
        {
            // 1. Check if already banned
            var banned = await JsonLinesHelper.ReadAllAsync<BannedNode>(bPath, cancellationToken: cancellationToken);
            if (banned.Any(b => IsMatch(b.Host, b.Port, host, port)))
            {
                return false;
            }

            // 2. Add to banned list
            var bannedRecord = new BannedNode(host, port, reason);
            await JsonLinesHelper.AppendAsync(bPath, bannedRecord, cancellationToken: cancellationToken);

            // 3. Remove from active node list if present
            var activeNodes = await JsonLinesHelper.ReadAllAsync<TrustedNode>(nPath, cancellationToken: cancellationToken);
            var filtered = activeNodes.Where(n => !IsMatch(n.Host, n.Port, host, port)).ToList();
            if (filtered.Count != activeNodes.Count)
            {
                await JsonLinesHelper.OverwriteAllAsync(nPath, filtered, cancellationToken: cancellationToken);
            }

            return true;
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Unbans a node: removes it from banned_nodes.json.
    /// </summary>
    public static async Task<bool> UnbanNodeAsync(
        string host,
        int port,
        string? bannedFilePath = null,
        CancellationToken cancellationToken = default)
    {
        var path = bannedFilePath ?? DefaultBannedFilePath;

        await Lock.WaitAsync(cancellationToken);
        try
        {
            var banned = await JsonLinesHelper.ReadAllAsync<BannedNode>(path, cancellationToken: cancellationToken);
            var filtered = banned.Where(b => !IsMatch(b.Host, b.Port, host, port)).ToList();

            if (filtered.Count == banned.Count)
            {
                return false; // Node was not in the ban list
            }

            await JsonLinesHelper.OverwriteAllAsync(path, filtered, cancellationToken: cancellationToken);
            return true;
        }
        finally
        {
            Lock.Release();
        }
    }

    /// <summary>
    /// Removes a node from the active peer list without banning it.
    /// </summary>
    public static async Task<bool> RemoveNodeAsync(
        string host,
        int port,
        string? nodesFilePath = null,
        CancellationToken cancellationToken = default)
    {
        var path = nodesFilePath ?? DefaultNodesFilePath;

        await Lock.WaitAsync(cancellationToken);
        try
        {
            var activeNodes = await JsonLinesHelper.ReadAllAsync<TrustedNode>(path, cancellationToken: cancellationToken);
            var filtered = activeNodes.Where(n => !IsMatch(n.Host, n.Port, host, port)).ToList();

            if (filtered.Count == activeNodes.Count)
            {
                return false;
            }

            await JsonLinesHelper.OverwriteAllAsync(path, filtered, cancellationToken: cancellationToken);
            return true;
        }
        finally
        {
            Lock.Release();
        }
    }

    private static bool IsMatch(string hostA, int portA, string hostB, int portB)
    {
        return portA == portB && string.Equals(hostA?.Trim(), hostB?.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
