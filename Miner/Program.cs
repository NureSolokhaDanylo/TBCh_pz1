using Blockchain.Shared.Common;
using Blockchain.Shared.Dtos;
using Blockchain.Shared.Models;
using Miner.Common;
using Miner.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("PeerClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHostedService<PeerDiscoveryService>();

var app = builder.Build();

// ========================================================
// 1. Node Status, Accounts & Peers
// ========================================================

// Health check endpoint
app.MapGet("/", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

// Current node status (chain height, mempool size, difficulty)
app.MapGet("/node/status", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

// Account state (balance and wallet nonce)
app.MapGet("/accounts/{address}", (string address) => Results.StatusCode(StatusCodes.Status501NotImplemented));

// List of trusted nodes (from data/nodes.json)
app.MapGet("/node/peers", async () =>
{
    var trustedPeers = await NodeManager.GetTrustedNodesAsync();
    return Results.Ok(trustedPeers);
});

// ========================================================
// 2. Blocks
// ========================================================

// Get the full blockchain (from data/blocks.json)
app.MapGet("/blocks", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

// Get the latest block in the chain
app.MapGet("/blocks/latest", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

// Get block by index
app.MapGet("/blocks/{index:int}", (int index) => Results.StatusCode(StatusCodes.Status501NotImplemented));

// Receive a new block from network peers
app.MapPost("/blocks", (Block block) => Results.StatusCode(StatusCodes.Status501NotImplemented));

// ========================================================
// 3. Block Headers
// ========================================================

// Get all block headers (from data/headers.json) for SPV / fast sync
app.MapGet("/headers", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

// Get the latest block header
app.MapGet("/headers/latest", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

// ========================================================
// 4. Transactions & Mempool
// ========================================================

// Get pending unconfirmed transactions (mempool)
app.MapGet("/transactions/pending", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

// Submit a new transaction to the mempool
app.MapPost("/transactions", (Transaction transaction) => Results.StatusCode(StatusCodes.Status501NotImplemented));

// Get transaction by ID
app.MapGet("/transactions/{id}", (string id) => Results.StatusCode(StatusCodes.Status501NotImplemented));

// ========================================================
// 5. Mining & Consensus
// ========================================================

// Trigger mining of a new block with pending transactions
app.MapPost("/mine", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

// Trigger chain synchronization and conflict resolution (Longest Chain Rule)
app.MapPost("/consensus/resolve", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

app.Run();
