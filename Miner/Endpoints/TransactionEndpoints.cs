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

        // Submit a new transaction to in-memory mempool with validation against live ledger state
        routes.MapPost("/transactions", (Transaction transaction, Mempool mempool, AccountLedger ledger) =>
        {
            var (success, errorMessage) = mempool.Add(transaction, ledger);
            if (!success)
            {
                return Results.BadRequest(new { error = errorMessage });
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
