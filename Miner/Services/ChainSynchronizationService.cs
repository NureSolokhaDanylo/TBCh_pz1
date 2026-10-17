using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Blockchain.Shared.Common;
using Blockchain.Shared.Dtos;
using Blockchain.Shared.Models;
using Blockchain.Shared.Services;
using Miner.Common;

namespace Miner.Services;

/// <summary>
/// Service responsible for blockchain synchronization, peer catch-up,
/// incoming block buffering, validation, difficulty retargeting, and fork resolution.
/// </summary>
public class ChainSynchronizationService
{
    private readonly IBlockchainValidator _validator;
    private readonly MiningController _miningController;
    private readonly Mempool _mempool;
    private readonly AccountLedger _accountLedger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChainSynchronizationService> _logger;
    private readonly MiningOptions _miningOptions;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly ConcurrentQueue<(Block Block, string? SenderHost, int? SenderPort)> _bufferedBlocks = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string BlocksFilePath { get; set; } = "data/blocks.json";

    public ChainSynchronizationService(
        IBlockchainValidator validator,
        MiningController miningController,
        Mempool mempool,
        AccountLedger accountLedger,
        IHttpClientFactory httpClientFactory,
        ILogger<ChainSynchronizationService> logger,
        MiningOptions? miningOptions = null)
    {
        _validator = validator;
        _miningController = miningController;
        _mempool = mempool;
        _accountLedger = accountLedger;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _miningOptions = miningOptions ?? new MiningOptions();
    }

    /// <summary>
    /// Gets the current in-memory count of buffered blocks awaiting synchronization completion.
    /// </summary>
    public int BufferedBlocksCount => _bufferedBlocks.Count;

    /// <summary>
    /// Reads blocks from disk line-by-line in JSON Lines format with syntax resilience.
    /// Returns the successfully deserialized blocks, and flags if a syntax error occurred.
    /// </summary>
    public async Task<ChainReadResult> ReadLocalChainRawAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(BlocksFilePath) || new FileInfo(BlocksFilePath).Length == 0)
        {
            await JsonLinesHelper.AppendAsync(BlocksFilePath, GenesisBlock.Instance, cancellationToken: cancellationToken);
            return new ChainReadResult([GenesisBlock.Instance], HasSyntaxError: false, FirstSyntaxErrorLine: -1, SyntaxErrorMessage: null);
        }

        var blocks = new List<Block>();
        bool hasSyntaxError = false;
        int syntaxErrorLine = -1;
        string? syntaxErrorMsg = null;

        using var stream = new FileStream(BlocksFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        int lineIndex = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                lineIndex++;
                continue;
            }

            try
            {
                var block = JsonSerializer.Deserialize<Block>(line, JsonLinesHelper.DefaultOptions);
                if (block != null)
                {
                    blocks.Add(block);
                }
                else
                {
                    hasSyntaxError = true;
                    syntaxErrorLine = lineIndex;
                    syntaxErrorMsg = $"Line {lineIndex} deserialized to null.";
                    break;
                }
            }
            catch (Exception ex)
            {
                hasSyntaxError = true;
                syntaxErrorLine = lineIndex;
                syntaxErrorMsg = $"JSON parse error on line {lineIndex}: {ex.Message}";
                break;
            }

            lineIndex++;
        }

        if (blocks.Count == 0 && !hasSyntaxError)
        {
            await JsonLinesHelper.AppendAsync(BlocksFilePath, GenesisBlock.Instance, cancellationToken: cancellationToken);
            return new ChainReadResult([GenesisBlock.Instance], HasSyntaxError: false, FirstSyntaxErrorLine: -1, SyntaxErrorMessage: null);
        }

        return new ChainReadResult(blocks, hasSyntaxError, syntaxErrorLine, syntaxErrorMsg);
    }

    /// <summary>
    /// Retrieves the local blockchain from storage. If empty or absent, initializes with Genesis block.
    /// Safely handles corrupted syntax lines by returning readable blocks.
    /// </summary>
    public async Task<List<Block>> GetLocalChainAsync(CancellationToken cancellationToken = default)
    {
        var raw = await ReadLocalChainRawAsync(cancellationToken);
        if (raw.Blocks.Count == 0)
        {
            return [GenesisBlock.Instance];
        }
        return raw.Blocks;
    }

    /// <summary>
    /// Checks the structural and cryptographic integrity of the local blockchain.
    /// Identifies the first broken block index and calculates the longest valid prefix.
    /// </summary>
    public async Task<(bool IsValid, int FirstBrokenIndex, string? ErrorMessage, List<Block> ValidPrefix, List<Block> RawBlocks)>
        CheckChainIntegrityDetailedAsync(CancellationToken cancellationToken = default)
    {
        var raw = await ReadLocalChainRawAsync(cancellationToken);
        if (raw.Blocks.Count == 0)
        {
            return (false, 0, raw.SyntaxErrorMessage ?? "No valid blocks in file.", [GenesisBlock.Instance], raw.Blocks);
        }

        var (isValid, firstInvalidIndex, validatorError) = _validator.ValidateChainDetailed(raw.Blocks);

        if (!isValid)
        {
            var brokenIndex = firstInvalidIndex;
            var validPrefix = brokenIndex <= 0
                ? new List<Block> { GenesisBlock.Instance }
                : raw.Blocks.Take(brokenIndex).ToList();

            return (false, brokenIndex, validatorError, validPrefix, raw.Blocks);
        }

        if (raw.HasSyntaxError)
        {
            var brokenIndex = raw.Blocks.Count;
            var validPrefix = raw.Blocks.Count == 0
                ? new List<Block> { GenesisBlock.Instance }
                : raw.Blocks;

            return (false, brokenIndex, raw.SyntaxErrorMessage, validPrefix, raw.Blocks);
        }

        return (true, -1, null, raw.Blocks, raw.Blocks);
    }

    /// <summary>
    /// Rolls back the local chain to the specified valid prefix, overwriting blocks.json,
    /// rebuilding the AccountLedger, returning orphaned valid user transactions to the mempool,
    /// pruning invalid mempool transactions, and restarting mining on the restored tip.
    /// </summary>
    public async Task RollbackToValidPrefixAsync(
        List<Block> validPrefix,
        int brokenIndex,
        IReadOnlyList<Block> oldRawBlocks,
        CancellationToken cancellationToken = default)
    {
        var restoredTip = validPrefix[^1];
        _logger.LogWarning(
            "Rolling back blockchain from broken block #{BrokenIndex} to last valid block #{TipIndex} (height {NewHeight})...",
            brokenIndex, restoredTip.Header.Index, validPrefix.Count);

        // 1. Overwrite blocks.json with the clean valid prefix
        await JsonLinesHelper.OverwriteAllAsync(BlocksFilePath, validPrefix, cancellationToken: cancellationToken);

        // 2. Rebuild the in-memory AccountLedger completely from the rolled-back chain
        _accountLedger.Rebuild(validPrefix);

        // 3. Return valid user transactions from discarded blocks back to Mempool (if not already confirmed)
        if (oldRawBlocks.Count > validPrefix.Count)
        {
            var discardedBlocks = oldRawBlocks.Skip(validPrefix.Count).ToList();
            var returnedTxs = ReconcileMempoolOnReorg(discardedBlocks, validPrefix);
            if (returnedTxs > 0)
            {
                _logger.LogInformation("Returned {Count} orphaned transactions from discarded blocks back to Mempool.", returnedTxs);
            }
        }

        // 4. Prune any mempool transactions that are no longer valid against the restored ledger
        var prunedCount = _mempool.PruneInvalidTransactions(_accountLedger);
        if (prunedCount > 0)
        {
            _logger.LogInformation("Pruned {Count} invalid transactions from Mempool after chain rollback.", prunedCount);
        }

        // 5. Restart candidate block mining on the new tip
        _miningController.Restart($"Chain rolled back to block #{restoredTip.Header.Index} due to corruption at #{brokenIndex}");
    }

    /// <summary>
    /// Retrieves the latest block (tip) of the local chain.
    /// </summary>
    public async Task<Block> GetLatestBlockAsync(CancellationToken cancellationToken = default)
    {
        var chain = await GetLocalChainAsync(cancellationToken);
        return chain[^1];
    }

    /// <summary>
    /// Computes the next Proof-of-Work difficulty for the local chain using automatic retargeting every K blocks.
    /// </summary>
    public async Task<int> GetNextDifficultyAsync(CancellationToken cancellationToken = default)
    {
        var chain = await GetLocalChainAsync(cancellationToken);
        return StructuralBlockchainValidator.CalculateNextDifficulty(
            chain,
            _miningOptions.AdjustmentIntervalBlocks,
            _miningOptions.TargetBlockTimeSeconds,
            _miningOptions.Difficulty);
    }

    /// <summary>
    /// Evaluates a candidate chain and switches to it if it is valid and has strictly greater cumulative Proof-of-Work.
    /// Returns any non-coinbase transactions from discarded blocks of the old branch back into the Mempool.
    /// </summary>
    public async Task<(bool Adopted, int ReturnedTxsCount, string Message)> TryAdoptCandidateChainAsync(
        List<Block> candidateChain,
        CancellationToken cancellationToken = default)
    {
        var localChain = await GetLocalChainAsync(cancellationToken);
        var localWork = StructuralBlockchainValidator.CalculateCumulativeWork(localChain);
        var candidateWork = StructuralBlockchainValidator.CalculateCumulativeWork(candidateChain);

        if (candidateWork <= localWork && candidateChain.Count <= localChain.Count)
        {
            return (false, 0, $"Candidate chain (height {candidateChain.Count}, work {candidateWork}) does not have more cumulative work than local chain (height {localChain.Count}, work {localWork}).");
        }

        var (isValid, errorMessage) = _validator.ValidateChain(candidateChain);
        if (!isValid)
        {
            return (false, 0, $"Candidate chain is invalid: {errorMessage}");
        }

        await JsonLinesHelper.OverwriteAllAsync(BlocksFilePath, candidateChain, cancellationToken: cancellationToken);
        _accountLedger.Rebuild(candidateChain);
        var returnedCount = ReconcileMempoolOnReorg(localChain, candidateChain);
        _mempool.PruneInvalidTransactions(_accountLedger);
        _miningController.Restart($"Adopted higher work candidate chain of height {candidateChain.Count}");

        return (true, returnedCount, $"Switched to chain of height {candidateChain.Count} (cumulative work: {candidateWork}). Returned {returnedCount} orphaned transactions to mempool.");
    }

    /// <summary>
    /// Performs synchronization with active trusted peers.
    /// First checks local chain integrity. If corrupted, attempts to query peers for a valid chain.
    /// If neighbor peers are absent or unreachable, automatically rolls back to the last valid block.
    /// </summary>
    public async Task<SyncResult> InitialSyncAsync(CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken);
        _miningController.SetSyncing(true);

        try
        {
            _logger.LogInformation("Starting blockchain synchronization with trusted peers...");

            var (isLocalValid, localBrokenIndex, localValidationError, localValidPrefix, _) =
                await CheckChainIntegrityDetailedAsync(cancellationToken);

            if (!isLocalValid)
            {
                _logger.LogWarning("LOCAL CHAIN CORRUPTION DETECTED at block #{Index}: {Error}",
                    localBrokenIndex, localValidationError);
            }
            else
            {
                _accountLedger.Rebuild(localValidPrefix);
            }

            var trustedPeers = await NodeManager.GetTrustedNodesAsync(cancellationToken: cancellationToken);
            bool chainUpdated = false;
            bool anyChainUpdated = false;

            if (trustedPeers.Count > 0)
            {
                using var client = _httpClientFactory.CreateClient("PeerClient");

                do
                {
                    chainUpdated = false;
                    var localIntegrity = await CheckChainIntegrityDetailedAsync(cancellationToken);
                    var currentLocalValid = localIntegrity.IsValid;
                    var localHeight = currentLocalValid ? localIntegrity.ValidPrefix.Count : 0;
                    var localWork = currentLocalValid
                        ? StructuralBlockchainValidator.CalculateCumulativeWork(localIntegrity.ValidPrefix)
                        : System.Numerics.BigInteger.MinusOne;

                    var peerHeights = new List<(TrustedNode Peer, int Height)>();
                    foreach (var peer in trustedPeers)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        try
                        {
                            var statusUrl = $"http://{peer.Host}:{peer.Port}/node/status";
                            var status = await client.GetFromJsonAsync<NodeStatusDto>(statusUrl, JsonOptions, cancellationToken);
                            if (status != null && (status.ChainHeight >= localHeight || !currentLocalValid))
                            {
                                peerHeights.Add((peer, status.ChainHeight));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug("Could not fetch status from {Host}:{Port}: {Message}", peer.Host, peer.Port, ex.Message);
                        }
                    }

                    if (peerHeights.Count == 0 && currentLocalValid)
                    {
                        _logger.LogInformation("Local chain is already at or ahead of all trusted peers. Local height: {Height}", localHeight);
                        break;
                    }

                    var candidatePeers = peerHeights.OrderByDescending(p => p.Height).ToList();

                    foreach (var (peer, advertisedHeight) in candidatePeers)
                    {
                        if (cancellationToken.IsCancellationRequested) break;

                        _logger.LogInformation("Querying chain of height {Height} from peer {Host}:{Port}...",
                            advertisedHeight, peer.Host, peer.Port);

                        List<Block>? candidateChain = null;
                        try
                        {
                            var blocksUrl = $"http://{peer.Host}:{peer.Port}/blocks";
                            candidateChain = await client.GetFromJsonAsync<List<Block>>(blocksUrl, JsonOptions, cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning("Failed to download blocks from {Host}:{Port}: {Message}", peer.Host, peer.Port, ex.Message);
                            continue;
                        }

                        if (candidateChain == null || candidateChain.Count == 0)
                        {
                            continue;
                        }

                        var (isValid, errorMessage) = _validator.ValidateChain(candidateChain);
                        if (!isValid)
                        {
                            _logger.LogWarning("Peer {Host}:{Port} sent an INVALID chain: {Error}. Banning node.", peer.Host, peer.Port, errorMessage);
                            await NodeManager.BanNodeAsync(peer.Host, peer.Port, $"Sent invalid blockchain: {errorMessage}", cancellationToken: cancellationToken);
                            continue;
                        }

                        var candidateWork = StructuralBlockchainValidator.CalculateCumulativeWork(candidateChain);
                        if (currentLocalValid && candidateWork <= localWork && candidateChain.Count <= localHeight)
                        {
                            continue;
                        }

                        // Candidate chain is valid and either local chain is corrupt or candidate has more work
                        await JsonLinesHelper.OverwriteAllAsync(BlocksFilePath, candidateChain, cancellationToken: cancellationToken);
                        _accountLedger.Rebuild(candidateChain);
                        var returnedTxs = currentLocalValid
                            ? ReconcileMempoolOnReorg(localIntegrity.ValidPrefix, candidateChain)
                            : 0;

                        _mempool.PruneInvalidTransactions(_accountLedger);

                        _logger.LogInformation("Blockchain updated to height {Height} (work: {Work}) from peer {Host}:{Port}. Returned {ReturnedTxs} orphaned txs to mempool.",
                            candidateChain.Count, candidateWork, peer.Host, peer.Port, returnedTxs);

                        chainUpdated = true;
                        anyChainUpdated = true;
                        break;
                    }
                } while (chainUpdated && !cancellationToken.IsCancellationRequested);

                await DrainBufferedBlocksAsync(cancellationToken);
            }
            else
            {
                _logger.LogInformation("No trusted peers available to query for sync.");
            }

            // Final state check after sync attempt
            var finalIntegrity = await CheckChainIntegrityDetailedAsync(cancellationToken);
            if (!finalIntegrity.IsValid)
            {
                // Local blockchain is corrupted and no peer provided a valid chain!
                // Roll back to the last valid block before the corrupted block.
                _logger.LogWarning(
                    "Local blockchain is corrupted at block #{BrokenIndex} ({Error}) and no valid peer chain could be synced. Rolling back to last valid block #{TipIndex}...",
                    finalIntegrity.FirstBrokenIndex, finalIntegrity.ErrorMessage, finalIntegrity.ValidPrefix[^1].Header.Index);

                await RollbackToValidPrefixAsync(
                    finalIntegrity.ValidPrefix,
                    finalIntegrity.FirstBrokenIndex,
                    finalIntegrity.RawBlocks,
                    cancellationToken);

                return new SyncResult(
                    Success: true,
                    IsLocalChainValid: true,
                    ChainUpdated: true,
                    Message: $"Local blockchain was corrupted at block #{finalIntegrity.FirstBrokenIndex}; rolled back to block #{finalIntegrity.ValidPrefix[^1].Header.Index} (height {finalIntegrity.ValidPrefix.Count}). Ledger and difficulty restored.",
                    FinalHeight: finalIntegrity.ValidPrefix.Count,
                    ErrorMessage: null);
            }

            _accountLedger.Rebuild(finalIntegrity.ValidPrefix);

            return new SyncResult(
                Success: true,
                IsLocalChainValid: true,
                ChainUpdated: anyChainUpdated,
                Message: anyChainUpdated ? "Blockchain successfully updated from network." : "Local blockchain verified; in sync with network.",
                FinalHeight: finalIntegrity.ValidPrefix.Count,
                ErrorMessage: null);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("InitialSyncAsync was canceled.");
            return new SyncResult(false, false, false, "Synchronization was canceled.", 0, "Operation canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during blockchain synchronization.");
            return new SyncResult(false, false, false, $"Unexpected error: {ex.Message}", 0, ex.Message);
        }
        finally
        {
            _miningController.SetSyncing(false);
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Processes an incoming block broadcast from a network peer.
    /// </summary>
    public async Task ProcessIncomingBlockAsync(Block block, string? senderHost, int? senderPort, CancellationToken cancellationToken = default)
    {
        if (_miningController.IsSyncing)
        {
            _logger.LogInformation("Node is currently syncing. Enqueued incoming block #{Index} from {Host}:{Port} to buffer.",
                block.Header.Index, senderHost, senderPort);
            _bufferedBlocks.Enqueue((block, senderHost, senderPort));
            return;
        }

        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            await ProcessIncomingBlockCoreAsync(block, senderHost, senderPort, cancellationToken);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task ProcessIncomingBlockCoreAsync(Block block, string? senderHost, int? senderPort, CancellationToken cancellationToken)
    {
        var localChain = await GetLocalChainAsync(cancellationToken);
        var tip = localChain[^1];

        if (block.Header.Index <= tip.Header.Index)
        {
            _logger.LogInformation("Ignored stale block #{Index} from {Host}:{Port}. Current tip is #{TipIndex}.",
                block.Header.Index, senderHost, senderPort, tip.Header.Index);
            return;
        }

        if (block.Header.Index == tip.Header.Index + 1 &&
            string.Equals(block.Header.PreviousHash, tip.Header.Hash, StringComparison.OrdinalIgnoreCase))
        {
            _miningController.Pause($"Validating candidate block #{block.Header.Index}");

            var extendedChain = new List<Block>(localChain) { block };
            var (isValid, errorMessage) = _validator.ValidateChain(extendedChain);

            if (isValid)
            {
                await JsonLinesHelper.AppendAsync(BlocksFilePath, block, cancellationToken: cancellationToken);
                PruneMempoolWithBlock(block);
                _accountLedger.ApplyBlock(block);
                _logger.LogInformation("Block #{Index} accepted and appended to local chain.", block.Header.Index);
                _miningController.Restart($"New block #{block.Header.Index} added to tip");
            }
            else
            {
                _logger.LogWarning("Candidate block #{Index} from {Host}:{Port} is INVALID: {Error}",
                    block.Header.Index, senderHost, senderPort, errorMessage);

                if (!string.IsNullOrEmpty(senderHost) && senderPort.HasValue)
                {
                    await NodeManager.BanNodeAsync(senderHost, senderPort.Value,
                        $"Sent invalid block #{block.Header.Index}: {errorMessage}", cancellationToken: cancellationToken);
                }

                _miningController.Resume();
            }

            return;
        }

        // Gap or Fork detected
        _logger.LogInformation("Gap or fork detected with block #{Index} from {Host}:{Port} (Local tip: #{TipIndex}). Initiating resolution...",
            block.Header.Index, senderHost, senderPort, tip.Header.Index);

        _miningController.Pause($"Resolving fork/gap for block #{block.Header.Index}");

        if (!string.IsNullOrEmpty(senderHost) && senderPort.HasValue)
        {
            bool resolvedWithSender = await TryResolveWithPeerAsync(senderHost, senderPort.Value, localChain, cancellationToken);
            if (resolvedWithSender)
            {
                _miningController.Restart("Fork resolved with sender chain");
                return;
            }
        }

        _miningController.Resume();
        await InitialSyncAsync(cancellationToken);
    }

    private async Task<bool> TryResolveWithPeerAsync(string host, int port, List<Block> localChain, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("PeerClient");
            var blocksUrl = $"http://{host}:{port}/blocks";
            var peerChain = await client.GetFromJsonAsync<List<Block>>(blocksUrl, JsonOptions, cancellationToken);

            if (peerChain == null)
            {
                return false;
            }

            var localWork = StructuralBlockchainValidator.CalculateCumulativeWork(localChain);
            var peerWork = StructuralBlockchainValidator.CalculateCumulativeWork(peerChain);

            if (peerWork <= localWork && peerChain.Count <= localChain.Count)
            {
                _logger.LogWarning("Peer {Host}:{Port} chain does not have greater work than local chain.", host, port);
                return false;
            }

            var (isValid, errorMessage) = _validator.ValidateChain(peerChain);
            if (!isValid)
            {
                _logger.LogWarning("Peer {Host}:{Port} provided invalid chain during fork resolution: {Error}. Banning peer.", host, port, errorMessage);
                await NodeManager.BanNodeAsync(host, port, $"Sent invalid chain during fork resolution: {errorMessage}", cancellationToken: cancellationToken);
                return false;
            }

            await JsonLinesHelper.OverwriteAllAsync(BlocksFilePath, peerChain, cancellationToken: cancellationToken);
            _accountLedger.Rebuild(peerChain);
            var returnedTxs = ReconcileMempoolOnReorg(localChain, peerChain);

            _logger.LogInformation("Chain reorganized to height {Height} from peer {Host}:{Port}. Returned {Count} orphaned txs to mempool.",
                peerChain.Count, host, port, returnedTxs);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error resolving fork with peer {Host}:{Port}: {Message}", host, port, ex.Message);
            return false;
        }
    }

    private async Task DrainBufferedBlocksAsync(CancellationToken cancellationToken)
    {
        if (_bufferedBlocks.IsEmpty) return;

        _logger.LogInformation("Draining {Count} buffered blocks accumulated during sync...", _bufferedBlocks.Count);

        while (_bufferedBlocks.TryDequeue(out var item))
        {
            if (cancellationToken.IsCancellationRequested) break;

            try
            {
                await ProcessIncomingBlockCoreAsync(item.Block, item.SenderHost, item.SenderPort, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing buffered block #{Index}.", item.Block.Header.Index);
            }
        }
    }

    /// <summary>
    /// Constructs a candidate block with Coinbase reward (base reward + sum of block transaction fees)
    /// and pending mempool transactions, performs Proof-of-Work, commits locally, and broadcasts to peers.
    /// </summary>
    public async Task<Block?> MineNextBlockAsync(
        string minerAddress,
        decimal blockReward,
        int difficulty,
        CancellationToken cancellationToken = default)
    {
        var localChain = await GetLocalChainAsync(cancellationToken);
        var tip = localChain[^1];
        var nextIndex = tip.Header.Index + 1;

        // Calculate difficulty automatically if not overridden, or adjust every K blocks
        var effectiveDifficulty = difficulty > 0
            ? StructuralBlockchainValidator.CalculateNextDifficulty(
                localChain,
                _miningOptions.AdjustmentIntervalBlocks,
                _miningOptions.TargetBlockTimeSeconds,
                difficulty)
            : _miningOptions.Difficulty;

        // 1. Gather pending mempool transactions and compute total fees
        var pendingTxs = _mempool.GetCandidateTransactions();
        var totalFees = pendingTxs.Sum(t => t.Fee);
        var totalCoinbaseReward = blockReward + totalFees;

        // 2. Prepare transactions: Coinbase reward first (Base + Fees), then pending transactions
        var candidateTxs = new List<Transaction>(pendingTxs.Count + 1);
        var coinbaseTx = new Transaction(AccountLedger.CoinbaseAddress, minerAddress, totalCoinbaseReward, 0, fee: 0m);
        candidateTxs.Add(coinbaseTx);
        candidateTxs.AddRange(pendingTxs);

        // 3. Compute Merkle root of candidate transactions
        var merkleRoot = MerkleTree.ComputeRoot(candidateTxs);

        // 4. Build candidate block header
        var candidateHeader = new BlockHeader(nextIndex, tip.Header.Hash, merkleRoot, effectiveDifficulty);

        _logger.LogInformation("Mining candidate block #{Index} with {TxCount} transactions (Difficulty: {Difficulty}, Reward: {Reward} = {Base} base + {Fees} fees)...",
            nextIndex, candidateTxs.Count, effectiveDifficulty, totalCoinbaseReward, blockReward, totalFees);

        // 5. Perform Proof-of-Work
        var mined = candidateHeader.Mine(effectiveDifficulty, cancellationToken);
        if (!mined)
        {
            _logger.LogDebug("Mining of candidate block #{Index} was canceled.", nextIndex);
            return null;
        }

        // 6. Package block
        var block = new Block(candidateHeader, candidateTxs);

        // 7. Commit locally and broadcast
        var accepted = await AcceptMinedBlockAsync(block, cancellationToken);
        return accepted ? block : null;
    }

    /// <summary>
    /// Accepts and commits a locally mined block to the chain, updates the ledger and mempool,
    /// and broadcasts the block to all trusted peers.
    /// </summary>
    public async Task<bool> AcceptMinedBlockAsync(Block block, CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            var localChain = await GetLocalChainAsync(cancellationToken);
            var tip = localChain[^1];

            var (isValid, errorMessage) = _validator.ValidateBlock(block, tip);
            if (!isValid)
            {
                _logger.LogWarning("Locally mined block #{Index} failed validation: {Error}", block.Header.Index, errorMessage);
                return false;
            }

            await JsonLinesHelper.AppendAsync(BlocksFilePath, block, cancellationToken: cancellationToken);
            PruneMempoolWithBlock(block);
            _accountLedger.ApplyBlock(block);
            _logger.LogInformation("Successfully committed locally mined block #{Index} with hash {Hash}.",
                block.Header.Index, block.Header.Hash);

            _miningController.Restart($"Mined block #{block.Header.Index}");

            await BroadcastBlockAsync(block, cancellationToken);
            return true;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Broadcasts a block to all active trusted peers in parallel.
    /// </summary>
    public async Task BroadcastBlockAsync(Block block, CancellationToken cancellationToken = default)
    {
        try
        {
            var peers = await NodeManager.GetTrustedNodesAsync(cancellationToken: cancellationToken);
            if (peers.Count == 0) return;

            _logger.LogInformation("Broadcasting block #{Index} to {Count} trusted peers...", block.Header.Index, peers.Count);

            using var client = _httpClientFactory.CreateClient("PeerClient");
            var tasks = peers.Select(async peer =>
            {
                try
                {
                    var peerUrl = $"http://{peer.Host}:{peer.Port}/blocks";
                    using var request = new HttpRequestMessage(HttpMethod.Post, peerUrl)
                    {
                        Content = JsonContent.Create(block, options: JsonOptions)
                    };
                    request.Headers.Add("X-Node-Host", Environment.MachineName);

                    var response = await client.SendAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("Block #{Index} successfully accepted by peer {Host}:{Port}.", block.Header.Index, peer.Host, peer.Port);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Failed to broadcast block #{Index} to peer {Host}:{Port}: {Message}", block.Header.Index, peer.Host, peer.Port, ex.Message);
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error broadcasting block #{Index}: {Message}", block.Header.Index, ex.Message);
        }
    }

    /// <summary>
    /// Broadcasts a transaction to all active trusted peers in parallel.
    /// </summary>
    public async Task BroadcastTransactionAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        try
        {
            var peers = await NodeManager.GetTrustedNodesAsync(cancellationToken: cancellationToken);
            if (peers.Count == 0) return;

            _logger.LogInformation("Broadcasting transaction {TxId} to {Count} trusted peers...", transaction.Id, peers.Count);

            using var client = _httpClientFactory.CreateClient("PeerClient");
            var tasks = peers.Select(async peer =>
            {
                try
                {
                    var peerUrl = $"http://{peer.Host}:{peer.Port}/transactions";
                    using var request = new HttpRequestMessage(HttpMethod.Post, peerUrl)
                    {
                        Content = JsonContent.Create(transaction, options: JsonOptions)
                    };
                    request.Headers.Add("X-Node-Host", Environment.MachineName);

                    var response = await client.SendAsync(request, cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("Transaction {TxId} successfully accepted by peer {Host}:{Port}.", transaction.Id, peer.Host, peer.Port);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Failed to broadcast transaction {TxId} to peer {Host}:{Port}: {Message}", transaction.Id, peer.Host, peer.Port, ex.Message);
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Error broadcasting transaction {TxId}: {Message}", transaction.Id, ex.Message);
        }
    }

    private void PruneMempoolWithBlock(Block block)
    {
        var txIds = block.Transactions.Select(t => t.Id).Where(id => !string.IsNullOrEmpty(id));
        _mempool.Remove(txIds);
    }

    /// <summary>
    /// Removes transactions confirmed in the new winning chain from the mempool,
    /// and returns any orphaned non-coinbase transactions from discarded blocks of the old chain back into the mempool.
    /// </summary>
    private int ReconcileMempoolOnReorg(IReadOnlyList<Block> oldChain, IReadOnlyList<Block> newChain)
    {
        var newChainTxIds = newChain
            .SelectMany(b => b.Transactions)
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _mempool.Remove(newChainTxIds);

        var orphanedTxs = oldChain
            .SelectMany(b => b.Transactions)
            .Where(t => !AccountLedger.IsSystemOrCoinbase(t.Sender) &&
                        !string.IsNullOrWhiteSpace(t.Id) &&
                        !newChainTxIds.Contains(t.Id))
            .OrderBy(t => t.Nonce)
            .ThenBy(t => t.Timestamp)
            .ToList();

        int reinjectedCount = 0;
        foreach (var tx in orphanedTxs)
        {
            var (added, _) = _mempool.Add(tx, _accountLedger);
            if (added)
            {
                reinjectedCount++;
                _logger.LogInformation("Returned orphaned transaction {TxId} from discarded branch back into Mempool.", tx.Id);
            }
        }

        return reinjectedCount;
    }
}

public record SyncResult(
    bool Success,
    bool IsLocalChainValid,
    bool ChainUpdated,
    string Message,
    int FinalHeight,
    string? ErrorMessage);

public record ChainReadResult(
    List<Block> Blocks,
    bool HasSyntaxError,
    int FirstSyntaxErrorLine,
    string? SyntaxErrorMessage);

