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

// Determine execution mode (interactive terminal vs headless container)
bool isInteractive = Environment.UserInteractive &&
                     !Console.IsInputRedirected &&
                     string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HEADLESS"));

// Logging configuration
var logBuffer = new InMemoryLogBuffer(500);
builder.Services.AddSingleton(logBuffer);

if (isInteractive)
{
    // In interactive mode, clear default console logger so Spectre.Console prompts remain clean
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(new BufferedLoggerProvider(logBuffer, "data/miner.log"));
}
else
{
    // In headless mode (e.g. Docker container), log to standard console and buffer
    builder.Logging.AddProvider(new BufferedLoggerProvider(logBuffer, "data/miner.log"));
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

// Register wallet and UI services
builder.Services.AddSingleton<WalletService>();
builder.Services.AddSingleton<ConsoleUiService>();

builder.Services.AddHttpClient("PeerClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
});

// Register mining background service
builder.Services.AddHostedService<MiningBackgroundService>();

var app = builder.Build();

// Ensure single blockchain file (data/blocks.json) contains hardcoded Genesis block if empty
var blocksPath = "data/blocks.json";
if (!File.Exists(blocksPath) || new FileInfo(blocksPath).Length == 0)
{
    await JsonLinesHelper.AppendAsync(blocksPath, GenesisBlock.Instance);
}

// Map modular P2P endpoint groups
app.MapNodeEndpoints();
app.MapBlockEndpoints();
app.MapTransactionEndpoints();
app.MapConsensusEndpoints();

if (isInteractive)
{
    // Start background ASP.NET Core server
    await app.StartAsync();

    // Run interactive Spectre.Console UI loop
    var uiService = app.Services.GetRequiredService<ConsoleUiService>();
    await uiService.RunAsync();

    // Graceful shutdown on user exit
    await app.StopAsync();
}
else
{
    // Headless daemon mode for Docker containers
    await app.RunAsync();
}
