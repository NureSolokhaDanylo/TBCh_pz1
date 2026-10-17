using Blockchain.Shared.Services;
using Miner.Common;
using Miner.Services;

namespace Miner.Endpoints;

/// <summary>
/// Endpoints for mining triggers, full chain validation, and network consensus synchronization.
/// </summary>
public static class ConsensusEndpoints
{
    public static IEndpointRouteBuilder MapConsensusEndpoints(this IEndpointRouteBuilder routes)
    {
        // Command 3 (REST): Trigger on-demand mining of a new block with pending transactions and Coinbase reward
        routes.MapPost("/mine", async (string? minerAddress, ChainSynchronizationService syncService, MiningOptions options) =>
        {
            var recipient = string.IsNullOrWhiteSpace(minerAddress) ? options.MinerAddress : minerAddress.Trim();
            var block = await syncService.MineNextBlockAsync(recipient, options.BlockReward, options.Difficulty);
            return block != null
                ? Results.Ok(block)
                : Results.StatusCode(StatusCodes.Status500InternalServerError);
        });

        // Command 6 (REST): Full chain validation from Genesis to tip
        routes.MapGet("/blocks/validate", async (ChainSynchronizationService syncService, IBlockchainValidator validator) =>
        {
            var chain = await syncService.GetLocalChainAsync();
            var (isValid, errorMessage) = validator.ValidateChain(chain);
            var cumulativeWork = StructuralBlockchainValidator.CalculateCumulativeWork(chain).ToString();

            return Results.Ok(new
            {
                isValid,
                blockCount = chain.Count,
                cumulativeWork,
                verdict = isValid ? "OK: Entire blockchain is cryptographically valid." : errorMessage
            });
        });

        // Command 8 (REST): Trigger chain synchronization and fork resolution (Greatest Cumulative Work + Orphaned Tx Return)
        routes.MapPost("/consensus/resolve", async (ChainSynchronizationService syncService, Mempool mempool) =>
        {
            var syncResult = await syncService.InitialSyncAsync();
            var chain = await syncService.GetLocalChainAsync();
            var work = StructuralBlockchainValidator.CalculateCumulativeWork(chain).ToString();
            return Results.Ok(new
            {
                success = syncResult.Success,
                isLocalChainValid = syncResult.IsLocalChainValid,
                chainUpdated = syncResult.ChainUpdated,
                message = syncResult.Message,
                errorMessage = syncResult.ErrorMessage,
                chainHeight = chain.Count,
                currentHeight = chain.Count,
                tipHash = chain[^1].Header.Hash,
                cumulativeWork = work,
                pendingMempoolCount = mempool.Count
            });
        });

        return routes;
    }
}
