using Blockchain.Shared.Dtos;
using Blockchain.Shared.Models;
using Miner.Common;
using Miner.Services;
using Miner.Wallet;

namespace Miner.Endpoints;

/// <summary>
/// Endpoints for node health, status, wallet creation, account balance, and peer management.
/// </summary>
public static class NodeEndpoints
{
    public static IEndpointRouteBuilder MapNodeEndpoints(this IEndpointRouteBuilder routes)
    {
        // Health check endpoint
        routes.MapGet("/health", () => Results.Ok(new { status = "running", timestamp = DateTime.UtcNow }));

        // Current node status (chain height, mempool size, difficulty, mining and sync state)
        routes.MapGet("/node/status", async (Mempool mempool, ChainSynchronizationService syncService, MiningController miningController) =>
        {
            var chain = await syncService.GetLocalChainAsync();
            var nextDiff = await syncService.GetNextDifficultyAsync();
            var status = new NodeStatusDto(
                nodeId: Environment.MachineName,
                chainHeight: chain.Count,
                pendingTransactionsCount: mempool.Count,
                difficulty: nextDiff,
                isMining: miningController.State == MiningState.Mining,
                isSyncing: miningController.IsSyncing
            );
            return Results.Ok(status);
        });

        // Command 1 (REST): Create new wallet (secp256k1 keypair and address)
        routes.MapPost("/wallets", async (string? label, WalletService walletService) =>
        {
            var wallet = await walletService.CreateWalletAsync(string.IsNullOrWhiteSpace(label) ? "Wallet" : label);
            return Results.Ok(wallet);
        });

        // Command 2 (REST helper): Sign and send a transaction using a local wallet stored on this node
        routes.MapPost("/wallets/send", async (SendFromWalletRequest req, WalletService walletService, CancellationToken ct) =>
        {
            var wallets = await walletService.GetWalletsAsync(ct);
            var senderWallet = wallets.FirstOrDefault(w => string.Equals(w.Address, req.SenderAddress, StringComparison.OrdinalIgnoreCase));
            if (senderWallet == null)
            {
                return Results.NotFound(new { error = $"Local wallet with address '{req.SenderAddress}' not found on this node." });
            }

            var (success, tx, message) = await walletService.CreateAndSendTransactionAsync(
                senderWallet,
                req.RecipientAddress,
                req.Amount,
                req.Fee,
                ct);

            return success
                ? Results.Ok(new { message, transaction = tx })
                : Results.BadRequest(new { error = message });
        });

        // Command 5 (REST): Get balance and nonce of any address across the chain
        routes.MapGet("/accounts/{address}", (string address, AccountLedger ledger) =>
        {
            var account = ledger.GetAccount(address);
            return Results.Ok(new AccountStateDto(account.Address, account.Balance, account.Nonce));
        });

        // Command 7 (REST): List trusted active peers
        routes.MapGet("/node/peers", async () =>
        {
            var trustedPeers = await NodeManager.GetTrustedNodesAsync();
            return Results.Ok(trustedPeers);
        });

        // Command 7 (REST): Add a neighbor peer
        routes.MapPost("/node/peers", async (TrustedNode peer) =>
        {
            if (peer == null || string.IsNullOrWhiteSpace(peer.Host) || peer.Port <= 0)
            {
                return Results.BadRequest(new { error = "Valid host and port are required." });
            }

            peer.IsTrusted = true;
            peer.LastSeen = DateTime.UtcNow;
            var added = await NodeManager.AddNodeAsync(peer);
            return added
                ? Results.Ok(new { message = $"Peer {peer.Host}:{peer.Port} added.", peer })
                : Results.Conflict(new { error = $"Peer {peer.Host}:{peer.Port} already exists or is banned." });
        });

        // Command 7 (REST): Remove/disconnect a neighbor peer (for fork demonstration)
        routes.MapDelete("/node/peers", async (string host, int port) =>
        {
            var removed = await NodeManager.RemoveNodeAsync(host, port);
            return removed
                ? Results.Ok(new { message = $"Peer {host}:{port} disconnected and removed." })
                : Results.NotFound(new { error = $"Peer {host}:{port} not found in active peers." });
        });

        // Get currently active mining address
        routes.MapGet("/node/mining-wallet", (MiningOptions options) => Results.Ok(new { activeMiningAddress = options.MinerAddress }));

        // Reassign the active mining address (Coinbase recipient)
        routes.MapPost("/node/mining-wallet", (MiningWalletRequest req, MiningOptions options) =>
        {
            if (string.IsNullOrWhiteSpace(req?.Address) || !req.Address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || req.Address.Length != 42)
            {
                return Results.BadRequest(new { error = "Invalid address format. Expected '0x' followed by 40 hex characters." });
            }

            options.MinerAddress = req.Address.Trim();
            return Results.Ok(new { message = "Mining reward recipient successfully updated.", activeMiningAddress = options.MinerAddress });
        });

        return routes;
    }
}

public record SendFromWalletRequest(string SenderAddress, string RecipientAddress, decimal Amount, decimal Fee = 0m);
public record MiningWalletRequest(string Address);

