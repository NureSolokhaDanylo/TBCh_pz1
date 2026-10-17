using Blockchain.Shared.Models;
using Miner.Services;

namespace Miner.Endpoints;

/// <summary>
/// Endpoints for submitting and querying transactions in the in-memory mempool.
/// </summary>
public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder routes)
    {
        // Get pending unconfirmed transactions from in-memory mempool
        routes.MapGet("/transactions/pending", (Mempool mempool) => Results.Ok(mempool.GetAll()));

        // Command 2 (REST): Submit a new transaction to mempool (validates 4 checks and broadcasts to peers)
        routes.MapPost("/transactions", async (
            Transaction transaction,
            HttpContext context,
            Mempool mempool,
            AccountLedger ledger,
            ChainSynchronizationService syncService) =>
        {
            var (success, errorMessage) = mempool.Add(transaction, ledger);
            if (!success)
            {
                return Results.BadRequest(new { error = errorMessage });
            }

            // Broadcast to neighbor peers if submitted directly by a user/client (avoid gossip loops)
            if (!context.Request.Headers.ContainsKey("X-Node-Host"))
            {
                await syncService.BroadcastTransactionAsync(transaction);
            }

            return Results.Created($"/transactions/{transaction.Id}", transaction);
        });

        // Get transaction by ID from mempool
        routes.MapGet("/transactions/{id}", (string id, Mempool mempool) =>
        {
            var tx = mempool.GetById(id);
            return tx is not null ? Results.Ok(tx) : Results.NotFound();
        });

        return routes;
    }
}
