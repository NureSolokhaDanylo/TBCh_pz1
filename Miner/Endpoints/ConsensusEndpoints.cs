using Miner.Common;
using Miner.Services;

namespace Miner.Endpoints;

/// <summary>
/// Endpoints for mining triggers and network consensus synchronization.
/// </summary>
public static class ConsensusEndpoints
{
    public static IEndpointRouteBuilder MapConsensusEndpoints(this IEndpointRouteBuilder routes)
    {
        // Trigger on-demand mining of a new block with pending transactions
        routes.MapPost("/mine", async (ChainSynchronizationService syncService, MiningOptions options) =>
        {
            var block = await syncService.MineNextBlockAsync(options.MinerAddress, options.BlockReward, options.Difficulty);
            return block != null
                ? Results.Ok(block)
                : Results.StatusCode(StatusCodes.Status500InternalServerError);
        });

        // Trigger chain synchronization and conflict resolution (Longest Chain Rule)
        routes.MapPost("/consensus/resolve", async (ChainSynchronizationService syncService) =>
        {
            await syncService.InitialSyncAsync();
            return Results.Ok(new { message = "Consensus synchronization triggered." });
        });

        return routes;
    }
}
