using System.Text;
using Blockchain.Shared.Common;
using Blockchain.Shared.Models;
using Blockchain.Shared.Services;
using Miner.Common;
using Miner.Endpoints;
using Miner.Logging;
using Miner.Services;
using Miner.UI;
using Miner.Wallet;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

// Parse optional CLI flags (--port, --data-dir, --demo-attacks, --benchmark, --headless)
int? customPort = null;
string defaultDataDir = Directory.Exists("../data") ? Path.GetFullPath("../data") : "data";
string dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? defaultDataDir;
bool runDemoAttacksOnly = args.Contains("--demo-attacks", StringComparer.OrdinalIgnoreCase);
bool runBenchmarkOnly = args.Contains("--benchmark", StringComparer.OrdinalIgnoreCase);
bool forceHeadless = args.Contains("--headless", StringComparer.OrdinalIgnoreCase) ||
                     !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HEADLESS"));

for (int i = 0; i < args.Length - 1; i++)
{
    if (string.Equals(args[i], "--port", StringComparison.OrdinalIgnoreCase) && int.TryParse(args[i + 1], out var p))
    {
        customPort = p;
    }
    else if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase))
    {
        dataDir = args[i + 1];
    }
}

if (customPort.HasValue)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{customPort.Value}");
}

// Configure file paths for NodeManager
Directory.CreateDirectory(dataDir);
NodeManager.DefaultNodesFilePath = Path.Combine(dataDir, "nodes.json");
NodeManager.DefaultBannedFilePath = Path.Combine(dataDir, "banned_nodes.json");

bool isInteractive = Environment.UserInteractive &&
                     !Console.IsInputRedirected &&
                     !forceHeadless &&
                     !runDemoAttacksOnly &&
                     !runBenchmarkOnly;

var logBuffer = new InMemoryLogBuffer(500);
builder.Services.AddSingleton(logBuffer);

if (isInteractive || runDemoAttacksOnly || runBenchmarkOnly)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(new BufferedLoggerProvider(logBuffer, Path.Combine(dataDir, "miner.log")));
}
else
{
    builder.Logging.AddProvider(new BufferedLoggerProvider(logBuffer, Path.Combine(dataDir, "miner.log")));
}

// Register configuration and options
builder.Services.AddSingleton<MiningOptions>();

// Register in-memory transaction pool (Mempool)
builder.Services.AddSingleton<Mempool>();

// Register validator and orchestration services
builder.Services.AddSingleton<AccountLedger>();
builder.Services.AddSingleton<IBlockchainValidator, StructuralBlockchainValidator>();
builder.Services.AddSingleton<MiningController>();
builder.Services.AddSingleton<ChainSynchronizationService>();

// Register wallet, attack demo, benchmark, and UI services
builder.Services.AddSingleton<WalletService>();
builder.Services.AddSingleton<AttackDemonstrationService>();
builder.Services.AddSingleton<MiningBenchmarkService>();
builder.Services.AddSingleton<ConsoleUiService>();

builder.Services.AddHttpClient("PeerClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});

// Register mining background service (only when running as node)
if (!runDemoAttacksOnly && !runBenchmarkOnly)
{
    builder.Services.AddHostedService<MiningBackgroundService>();
}

var app = builder.Build();

// Configure data directory paths on services
var blocksPath = Path.Combine(dataDir, "blocks.json");
var syncService = app.Services.GetRequiredService<ChainSynchronizationService>();
syncService.BlocksFilePath = blocksPath;

var walletService = app.Services.GetRequiredService<WalletService>();
walletService.WalletsFilePath = Path.Combine(dataDir, "wallets.json");
walletService.OutboxTransactionsFilePath = Path.Combine(dataDir, "wallet_transactions.json");

// Automatically ensure an active mining wallet exists (create one if absent)
var miningOptions = app.Services.GetRequiredService<MiningOptions>();
await walletService.EnsureMiningWalletAsync(miningOptions);

// Ensure single blockchain file contains hardcoded Genesis block if empty
if (!File.Exists(blocksPath) || new FileInfo(blocksPath).Length == 0)
{
    await JsonLinesHelper.AppendAsync(blocksPath, GenesisBlock.Instance);
}

// Perform initial chain integrity check, synchronization with peers, or rollback recovery
var logger = app.Services.GetRequiredService<ILogger<Program>>();
var syncResult = await syncService.InitialSyncAsync();
if (syncResult.ChainUpdated)
{
    logger.LogInformation("Startup chain verification / synchronization completed: {Message}", syncResult.Message);
}
else
{
    logger.LogInformation("Startup chain verification completed. Blockchain is valid at height {Height}.", syncResult.FinalHeight);
}

if (runDemoAttacksOnly)
{
    var ui = app.Services.GetRequiredService<ConsoleUiService>();
    ui.RenderAttacksDemo(pauseAfter: false);
    return;
}

if (runBenchmarkOnly)
{
    var ui = app.Services.GetRequiredService<ConsoleUiService>();
    ui.RunMiningBenchmark(pauseAfter: false);
    return;
}

// Map modular P2P and explorer endpoint groups
app.MapExplorerEndpoints();
app.MapNodeEndpoints();
app.MapBlockEndpoints();
app.MapTransactionEndpoints();
app.MapConsensusEndpoints();

if (isInteractive)
{
    await app.StartAsync();

    var uiService = app.Services.GetRequiredService<ConsoleUiService>();
    await uiService.RunAsync();

    await app.StopAsync();
}
else
{
    await app.RunAsync();
}
