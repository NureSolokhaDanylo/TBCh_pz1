using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Blockchain.Shared.Common;
using Blockchain.Shared.Dtos;
using Blockchain.Shared.Models;
using Blockchain.Shared.Services;
using Miner.Common;

namespace Miner.Services;

/// <summary>
/// Service responsible for blockchain synchronization, peer catch-up,
/// incoming block buffering, validation, and fork resolution.
/// </summary>
public class ChainSynchronizationService
{
    private readonly IBlockchainValidator _validator;
    private readonly MiningController _miningController;
    private readonly Mempool _mempool;
    private readonly AccountLedger _accountLedger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChainSynchronizationService> _logger;

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
        ILogger<ChainSynchronizationService> logger)
    {
        _validator = validator;
        _miningController = miningController;
        _mempool = mempool;
        _accountLedger = accountLedger;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets the current in-memory count of buffered blocks awaiting synchronization completion.
    /// </summary>
    public int BufferedBlocksCount => _bufferedBlocks.Count;

    /// <summary>
    /// Retrieves the local blockchain from storage. If empty or absent, initializes with Genesis block.
    /// </summary>
    public async Task<List<Block>> GetLocalChainAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(BlocksFilePath) || new FileInfo(BlocksFilePath).Length == 0)
        {
            await JsonLinesHelper.AppendAsync(BlocksFilePath, GenesisBlock.Instance, cancellationToken: cancellationToken);
            return [GenesisBlock.Instance];
        }

        var blocks = await JsonLinesHelper.ReadAllAsync<Block>(BlocksFilePath, cancellationToken: cancellationToken);
        if (blocks.Count == 0)
        {
            await JsonLinesHelper.AppendAsync(BlocksFilePath, GenesisBlock.Instance, cancellationToken: cancellationToken);
            return [GenesisBlock.Instance];
        }

        return blocks;
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
    /// Performs initial catch-up with active trusted peers.
    /// Queries peer statuses, discovers the longest chain, downloads and validates it in memory,
    /// overwrites local chain if valid, or bans peer if corrupt.
    /// Finally drains and processes all incoming blocks received in buffer during sync.
    /// </summary>
    public async Task InitialSyncAsync(CancellationToken cancellationToken = default)
    {
        await _syncLock.WaitAsync(cancellationToken);
        _miningController.SetSyncing(true);

        try
        {
            _logger.LogInformation("Starting blockchain synchronization with trusted peers...");

            var initialChain = await GetLocalChainAsync(cancellationToken);
            _accountLedger.Rebuild(initialChain);

            bool chainUpdated;
            do
            {
                chainUpdated = false;
                var localChain = await GetLocalChainAsync(cancellationToken);
                var localHeight = localChain.Count;

                var trustedPeers = await NodeManager.GetTrustedNodesAsync(cancellationToken: cancellationToken);
                if (trustedPeers.Count == 0)
                {
                    _logger.LogInformation("No trusted peers available to query for sync.");
                    break;
                }

                using var client = _httpClientFactory.CreateClient("PeerClient");

                // Discover chain heights from trusted peers
                var peerHeights = new List<(TrustedNode Peer, int Height)>();
                foreach (var peer in trustedPeers)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        var statusUrl = $"http://{peer.Host}:{peer.Port}/node/status";
                        var status = await client.GetFromJsonAsync<NodeStatusDto>(statusUrl, JsonOptions, cancellationToken);
                        if (status != null && status.ChainHeight > localHeight)
                        {
                            peerHeights.Add((peer, status.ChainHeight));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Could not fetch status from {Host}:{Port}: {Message}", peer.Host, peer.Port, ex.Message);
                    }
                }

                if (peerHeights.Count == 0)
                {
                    _logger.LogInformation("Local chain is already at or ahead of all trusted peers. Local height: {Height}", localHeight);
                    break;
                }

                // Sort peers by longest chain height descending
                var candidatePeers = peerHeights.OrderByDescending(p => p.Height).ToList();

                foreach (var (peer, advertisedHeight) in candidatePeers)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    _logger.LogInformation("Attempting to download chain of height {Height} from peer {Host}:{Port}...",
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

                    if (candidateChain == null || candidateChain.Count <= localHeight)
                    {
                        _logger.LogWarning("Peer {Host}:{Port} returned insufficient blocks (count: {Count}, local: {LocalHeight}).",
                            peer.Host, peer.Port, candidateChain?.Count ?? 0, localHeight);
                        continue;
                    }

                    // Validate entire candidate chain in memory
                    var (isValid, errorMessage) = _validator.ValidateChain(candidateChain);
                    if (!isValid)
                    {
                        _logger.LogWarning("Peer {Host}:{Port} sent an INVALID chain: {Error}. Banning node.", peer.Host, peer.Port, errorMessage);
                        await NodeManager.BanNodeAsync(peer.Host, peer.Port, $"Sent invalid blockchain: {errorMessage}", cancellationToken: cancellationToken);
                        continue; // try next candidate peer
                    }

                    // Candidate chain is valid and longer -> persist to local file
                    await JsonLinesHelper.OverwriteAllAsync(BlocksFilePath, candidateChain, cancellationToken: cancellationToken);
                    PruneMempoolWithChain(candidateChain);
                    _accountLedger.Rebuild(candidateChain);
                    _logger.LogInformation("Blockchain successfully updated to height {Height} from peer {Host}:{Port}.",
                        candidateChain.Count, peer.Host, peer.Port);

                    chainUpdated = true;
                    break; // Re-evaluate loop against new height
                }
            } while (chainUpdated && !cancellationToken.IsCancellationRequested);

            // Drain any blocks buffered in memory during sync
            await DrainBufferedBlocksAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("InitialSyncAsync was canceled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during blockchain initial synchronization.");
        }
        finally
        {
            _miningController.SetSyncing(false);
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Processes an incoming block broadcast from a network peer.
    /// If synchronization is in progress, the block is buffered in memory.
    /// Otherwise, evaluates whether the block is stale, a direct continuation, or a fork.
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

        // Case 1: Stale block (already known or older)
        if (block.Header.Index <= tip.Header.Index)
        {
            _logger.LogInformation("Ignored stale block #{Index} from {Host}:{Port}. Current tip is #{TipIndex}.",
                block.Header.Index, senderHost, senderPort, tip.Header.Index);
            return;
        }

        // Case 2: Direct continuation of the current chain tip
        if (block.Header.Index == tip.Header.Index + 1 &&
            string.Equals(block.Header.PreviousHash, tip.Header.Hash, StringComparison.OrdinalIgnoreCase))
        {
            _miningController.Pause($"Validating candidate block #{block.Header.Index}");
            var (isValid, errorMessage) = _validator.ValidateBlock(block, tip);

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

        // Case 3: Gap or Fork detected
        _logger.LogInformation("Gap or fork detected with block #{Index} from {Host}:{Port} (Local tip: #{TipIndex}). Initiating resolution...",
            block.Header.Index, senderHost, senderPort, tip.Header.Index);

        _miningController.Pause($"Resolving fork/gap for block #{block.Header.Index}");

        if (!string.IsNullOrEmpty(senderHost) && senderPort.HasValue)
        {
            bool resolvedWithSender = await TryResolveWithPeerAsync(senderHost, senderPort.Value, localChain.Count, cancellationToken);
            if (resolvedWithSender)
            {
                _miningController.Restart("Fork resolved with sender chain");
                return;
            }
        }

        // Fallback: full peer sync catch-up if sender resolution failed or sender was unknown
        _miningController.Resume();
        await InitialSyncAsync(cancellationToken);
    }

    private async Task<bool> TryResolveWithPeerAsync(string host, int port, int currentHeight, CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient("PeerClient");
            var blocksUrl = $"http://{host}:{port}/blocks";
            var peerChain = await client.GetFromJsonAsync<List<Block>>(blocksUrl, JsonOptions, cancellationToken);

            if (peerChain == null || peerChain.Count <= currentHeight)
            {
                _logger.LogWarning("Peer {Host}:{Port} chain is not longer than local chain.", host, port);
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
            PruneMempoolWithChain(peerChain);
            _accountLedger.Rebuild(peerChain);
            _logger.LogInformation("Chain successfully reorganized to height {Height} from peer {Host}:{Port}.", peerChain.Count, host, port);
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
    /// Constructs a candidate block with coinbase reward and pending transactions,
    /// performs Proof-of-Work to find a valid nonce, commits the block locally, and broadcasts it to peers.
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

        // 1. Prepare transactions: Coinbase reward first, then pending mempool transactions
        var candidateTxs = new List<Transaction>();
        var coinbaseTx = new Transaction(AccountLedger.CoinbaseAddress, minerAddress, blockReward, 0);
        candidateTxs.Add(coinbaseTx);

        var pendingTxs = _mempool.GetCandidateTransactions();
        candidateTxs.AddRange(pendingTxs);

        // 2. Compute Merkle root of candidate transactions
        var merkleRoot = MerkleTree.ComputeRoot(candidateTxs);

        // 3. Build candidate block header
        var candidateHeader = new BlockHeader(nextIndex, tip.Header.Hash, merkleRoot, difficulty);

        _logger.LogInformation("Mining candidate block #{Index} with {TxCount} transactions (Difficulty: {Difficulty})...",
            nextIndex, candidateTxs.Count, difficulty);

        // 4. Perform Proof-of-Work
        var mined = candidateHeader.Mine(difficulty, cancellationToken);
        if (!mined)
        {
            _logger.LogDebug("Mining of candidate block #{Index} was canceled.", nextIndex);
            return null;
        }

        // 5. Package block
        var block = new Block(candidateHeader, candidateTxs);

        // 6. Commit locally and broadcast
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

            // Broadcast to network peers
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

    private void PruneMempoolWithChain(IEnumerable<Block> chain)
    {
        var txIds = chain.SelectMany(b => b.Transactions).Select(t => t.Id).Where(id => !string.IsNullOrEmpty(id));
        _mempool.Remove(txIds);
    }
}
