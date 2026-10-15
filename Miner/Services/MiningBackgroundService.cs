using Miner.Common;

namespace Miner.Services;

/// <summary>
/// Background mining service.
/// On node startup, performs initial blockchain catch-up with trusted peers,
/// then runs the continuous Proof-of-Work mining loop while honoring MiningController signals.
/// </summary>
public class MiningBackgroundService : BackgroundService
{
    private readonly ILogger<MiningBackgroundService> _logger;
    private readonly Mempool _mempool;
    private readonly MiningController _miningController;
    private readonly ChainSynchronizationService _syncService;
    private readonly MiningOptions _options;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(2);

    public MiningBackgroundService(
        ILogger<MiningBackgroundService> logger,
        Mempool mempool,
        MiningController miningController,
        ChainSynchronizationService syncService,
        MiningOptions options)
    {
        _logger = logger;
        _mempool = mempool;
        _miningController = miningController;
        _syncService = syncService;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MiningBackgroundService initialized. Initiating startup catch-up...");

        // 1. Initial catch-up with peers before starting the mining loop
        try
        {
            await _syncService.InitialSyncAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initial blockchain synchronization encountered an error, proceeding with current local chain.");
        }

        _logger.LogInformation("Startup catch-up complete. Entering mining loop (Miner address: {Address}, Difficulty: {Difficulty})...",
            _options.MinerAddress, _options.Difficulty);

        // 2. Mining lifecycle loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait while mining is paused (e.g. during block validation, Merkle tree update, or sync)
                await _miningController.WaitUntilActiveAsync(stoppingToken);

                if (!_options.Enabled)
                {
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                // Create a linked token so mining is interrupted immediately if a new block arrives or pause is requested
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _miningController.IterationToken);

                var minedBlock = await _syncService.MineNextBlockAsync(
                    _options.MinerAddress,
                    _options.BlockReward,
                    _options.Difficulty,
                    linkedCts.Token);

                if (minedBlock != null)
                {
                    _logger.LogInformation("Block #{Index} mined successfully! Hash: {Hash}",
                        minedBlock.Header.Index, minedBlock.Header.Hash);
                }

                await Task.Delay(_checkInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // Iteration cancelled due to pause or tip change, cleanly continue loop
                _logger.LogDebug("Mining iteration canceled, restarting candidate loop...");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in mining loop iteration.");
                await Task.Delay(1000, stoppingToken);
            }
        }

        _logger.LogInformation("MiningBackgroundService stopped.");
    }
}
