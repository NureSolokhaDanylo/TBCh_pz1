using Blockchain.Shared.Models;
using Miner.Services;

namespace Miner.Endpoints;

/// <summary>
/// Endpoints for blockchain blocks, headers, and candidate block ingestion.
/// </summary>
public static class BlockEndpoints
{
    public static IEndpointRouteBuilder MapBlockEndpoints(this IEndpointRouteBuilder routes)
    {
        // ========================================================
        // 1. Full Blocks
        // ========================================================

        // Get the full blockchain (from data/blocks.json)
        routes.MapGet("/blocks", async (ChainSynchronizationService syncService) =>
        {
            var chain = await syncService.GetLocalChainAsync();
            return Results.Ok(chain);
        });

        // Get the latest block in the chain
        routes.MapGet("/blocks/latest", async (ChainSynchronizationService syncService) =>
        {
            var tip = await syncService.GetLatestBlockAsync();
            return Results.Ok(tip);
        });

        // Get block by index
        routes.MapGet("/blocks/{index:int}", async (int index, ChainSynchronizationService syncService) =>
        {
            var chain = await syncService.GetLocalChainAsync();
            var block = chain.FirstOrDefault(b => b.Header.Index == index);
            return block is not null ? Results.Ok(block) : Results.NotFound();
        });

        // Receive a new block from network peers
        routes.MapPost("/blocks", async (Block block, HttpContext context, ChainSynchronizationService syncService) =>
        {
            string? senderHost = context.Request.Headers.TryGetValue("X-Node-Host", out var hostVal)
                ? hostVal.ToString()
                : context.Connection.RemoteIpAddress?.ToString();

            int? senderPort = context.Request.Headers.TryGetValue("X-Node-Port", out var portVal) && int.TryParse(portVal, out var parsedPort)
                ? parsedPort
                : null;

            await syncService.ProcessIncomingBlockAsync(block, senderHost, senderPort);
            return Results.Accepted();
        });

        // ========================================================
        // 2. Block Headers
        // ========================================================

        // Get all block headers (extracted from data/blocks.json)
        routes.MapGet("/headers", async (ChainSynchronizationService syncService) =>
        {
            var chain = await syncService.GetLocalChainAsync();
            return Results.Ok(chain.Select(b => b.Header));
        });

        // Get the latest block header
        routes.MapGet("/headers/latest", async (ChainSynchronizationService syncService) =>
        {
            var tip = await syncService.GetLatestBlockAsync();
            return Results.Ok(tip.Header);
        });

        return routes;
    }
}
