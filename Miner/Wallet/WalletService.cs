using Blockchain.Shared.Common;
using Blockchain.Shared.Models;
using Miner.Services;

namespace Miner.Wallet;

/// <summary>
/// Full-Node Wallet service: manages local ECDSA keypairs in JSON Lines format,
/// verifies account balances and nonces directly from the in-memory AccountLedger,
/// signs transactions, inserts them into the local Mempool, and broadcasts them to network peers.
/// </summary>
public class WalletService
{
    private readonly AccountLedger _accountLedger;
    private readonly Mempool _mempool;
    private readonly ChainSynchronizationService _syncService;
    private readonly ILogger<WalletService> _logger;

    public string WalletsFilePath { get; set; } = "data/wallets.json";
    public string OutboxTransactionsFilePath { get; set; } = "data/wallet_transactions.json";

    public WalletService(
        AccountLedger accountLedger,
        Mempool mempool,
        ChainSynchronizationService syncService,
        ILogger<WalletService> logger)
    {
        _accountLedger = accountLedger;
        _mempool = mempool;
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves all locally saved wallets with verified balances and nonces from the local ledger.
    /// </summary>
    public async Task<List<Wallet>> GetWalletsAsync(CancellationToken cancellationToken = default)
    {
        var wallets = await JsonLinesHelper.ReadAllAsync<Wallet>(WalletsFilePath, cancellationToken: cancellationToken);

        foreach (var wallet in wallets)
        {
            var accountState = _accountLedger.GetAccount(wallet.Address);
            wallet.Balance = accountState.Balance;
            wallet.Nonce = accountState.Nonce;
        }

        return wallets;
    }

    /// <summary>
    /// Creates a new wallet with an ECDSA (P-256) keypair and saves it to data/wallets.json.
    /// </summary>
    public async Task<Wallet> CreateWalletAsync(string label, CancellationToken cancellationToken = default)
    {
        var (privateKeyHex, publicKeyHex, address) = CryptoHelper.GenerateKeyPair();

        var wallet = new Wallet(address, publicKeyHex, privateKeyHex, label)
        {
            Nonce = 0,
            Balance = 0,
            CreatedAt = DateTime.UtcNow
        };

        await JsonLinesHelper.AppendAsync(WalletsFilePath, wallet, cancellationToken: cancellationToken);
        _logger.LogInformation("New wallet '{Label}' created with address {Address}.", label, address);
        return wallet;
    }

    /// <summary>
    /// Creates, signs, validates, and broadcasts a transaction.
    /// </summary>
    public async Task<(bool Success, Transaction? Transaction, string Message)> CreateAndSendTransactionAsync(
        Wallet senderWallet,
        string recipientAddress,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipientAddress))
        {
            return (false, null, "Recipient address cannot be empty.");
        }

        if (amount <= 0)
        {
            return (false, null, "Transfer amount must be strictly greater than zero.");
        }

        if (string.Equals(senderWallet.Address.Trim(), recipientAddress.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return (false, null, "Sender and recipient addresses cannot be identical.");
        }

        // Determine next sequential Nonce based on confirmed state and pending mempool transactions
        var confirmedNonce = _accountLedger.GetConfirmedNonce(senderWallet.Address);
        var pendingCount = _mempool.GetAll().Count(t => string.Equals(t.Sender, senderWallet.Address, StringComparison.OrdinalIgnoreCase));
        var nextNonce = confirmedNonce + pendingCount;

        // 1. Create transaction
        var tx = new Transaction(senderWallet.Address.Trim(), recipientAddress.Trim(), amount, nextNonce)
        {
            PublicKey = senderWallet.PublicKey
        };

        // 2. Sign transaction hash with sender's private key
        tx.Signature = CryptoHelper.SignData(senderWallet.PrivateKey, tx.Id);

        // 3. Validate and add to local Mempool
        var (added, error) = _mempool.Add(tx, _accountLedger);
        if (!added)
        {
            _logger.LogWarning("Transaction creation failed validation: {Error}", error);
            return (false, null, $"Transaction rejected by local ledger: {error}");
        }

        // 4. Save to outbox history
        await JsonLinesHelper.AppendAsync(OutboxTransactionsFilePath, tx, cancellationToken: cancellationToken);

        // 5. Broadcast to network peers in parallel
        await _syncService.BroadcastTransactionAsync(tx, cancellationToken);

        _logger.LogInformation("Transaction {TxId} successfully created, signed, and broadcasted.", tx.Id);
        return (true, tx, "Transaction accepted into mempool and broadcasted to network peers.");
    }

    /// <summary>
    /// Credits test coins to a local wallet directly in memory (for development and debugging).
    /// </summary>
    public AccountState CreditFaucet(string address, decimal amount)
    {
        return _accountLedger.CreditFaucet(address, amount);
    }
}
