using Blockchain.Shared.Common;
using Blockchain.Shared.Models;
using WalletApp.Models;

namespace WalletApp.Services;

/// <summary>
/// Wallet management service: local persistence in JSON Lines format,
/// account creation, transaction creation, and digital signing with Nonce tracking.
/// </summary>
public class WalletService
{
    public string WalletsFilePath { get; set; } = "data/wallets.json";
    public string OutboxTransactionsFilePath { get; set; } = "data/wallet_transactions.json";

    /// <summary>
    /// Retrieves all locally saved wallets.
    /// </summary>
    public async Task<List<Wallet>> GetWalletsAsync(CancellationToken cancellationToken = default)
    {
        return await JsonLinesHelper.ReadAllAsync<Wallet>(WalletsFilePath, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Creates a new wallet with a unique ECDSA keypair and initial Nonce = 0.
    /// </summary>
    public async Task<Wallet> CreateWalletAsync(string label, CancellationToken cancellationToken = default)
    {
        var (privateKey, publicKey, address) = CryptoService.GenerateKeyPair();

        var wallet = new Wallet(address, publicKey, privateKey, label)
        {
            Nonce = 0,
            Balance = 0,
            CreatedAt = DateTime.UtcNow
        };

        await JsonLinesHelper.AppendAsync(WalletsFilePath, wallet, cancellationToken: cancellationToken);
        return wallet;
    }

    /// <summary>
    /// Creates and signs a new transaction.
    /// Assigns the sender wallet's current Nonce, signs it with the private key,
    /// and increments the sender wallet's Nonce by 1.
    /// </summary>
    public async Task<(Transaction Transaction, Wallet UpdatedWallet)> CreateAndSignTransactionAsync(
        Wallet senderWallet,
        string recipientAddress,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        // 1. Create transaction with the sender's current Nonce
        var currentNonce = senderWallet.Nonce;
        var tx = new Transaction(senderWallet.Address, recipientAddress, amount, currentNonce);

        // 2. Sign transaction hash using sender's private key
        tx.Signature = CryptoService.SignData(senderWallet.PrivateKey, tx.Id);

        // 3. Increment wallet Nonce (Ethereum replay protection model)
        senderWallet.Nonce++;

        // 4. Update the wallet in persistent storage
        var allWallets = await GetWalletsAsync(cancellationToken);
        var targetIndex = allWallets.FindIndex(w => string.Equals(w.Address, senderWallet.Address, StringComparison.OrdinalIgnoreCase));
        if (targetIndex >= 0)
        {
            allWallets[targetIndex] = senderWallet;
            await JsonLinesHelper.OverwriteAllAsync(WalletsFilePath, allWallets, cancellationToken: cancellationToken);
        }

        // 5. Store signed transaction into outbox history
        await JsonLinesHelper.AppendAsync(OutboxTransactionsFilePath, tx, cancellationToken: cancellationToken);

        return (tx, senderWallet);
    }

    /// <summary>
    /// Updates the confirmed balance and/or Nonce of a wallet based on node response data.
    /// </summary>
    public async Task<bool> UpdateSyncStateAsync(
        string address,
        decimal newBalance,
        long? newNonce = null,
        CancellationToken cancellationToken = default)
    {
        var wallets = await GetWalletsAsync(cancellationToken);
        var wallet = wallets.FirstOrDefault(w => string.Equals(w.Address, address, StringComparison.OrdinalIgnoreCase));

        if (wallet is null)
        {
            return false;
        }

        wallet.Balance = newBalance;
        if (newNonce.HasValue)
        {
            wallet.Nonce = newNonce.Value;
        }
        wallet.LastSyncedAt = DateTime.UtcNow;

        await JsonLinesHelper.OverwriteAllAsync(WalletsFilePath, wallets, cancellationToken: cancellationToken);
        return true;
    }
}
