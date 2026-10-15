using Blockchain.Shared.Dtos;
using Miner.Common;
using Miner.Services;

namespace Miner.Endpoints;

/// <summary>
/// Endpoints for node health, status, and peer management.
/// </summary>
public static class NodeEndpoints
{
    public static IEndpointRouteBuilder MapNodeEndpoints(this IEndpointRouteBuilder routes)
    {
        // Health check endpoint
        routes.MapGet("/", () => Results.Ok(new { status = "running", timestamp = DateTime.UtcNow }));

        // Current node status (chain height, mempool size, difficulty, mining and sync state)
        routes.MapGet("/node/status", async (Mempool mempool, ChainSynchronizationService syncService, MiningController miningController) =>
        {
            var chain = await syncService.GetLocalChainAsync();
            var status = new NodeStatusDto(
                nodeId: Environment.MachineName,
                chainHeight: chain.Count,
                pendingTransactionsCount: mempool.Count,
                difficulty: 4,
                isMining: miningController.State == MiningState.Mining,
                isSyncing: miningController.IsSyncing
            );
            return Results.Ok(status);
        });

        // List of trusted active peers (from data/nodes.json)
        routes.MapGet("/node/peers", async () =>
        {
            var trustedPeers = await NodeManager.GetTrustedNodesAsync();
            return Results.Ok(trustedPeers);
        });

        return routes;
    }
}
