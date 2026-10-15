using System.Collections.Concurrent;
using Blockchain.Shared.Models;

namespace Miner.Services;

/// <summary>
/// In-memory ledger tracking confirmed account states (balance and transaction nonce).
/// State is deterministically reconstructed by replaying the blockchain from the Genesis block.
/// </summary>
public class AccountLedger
{
    private readonly ConcurrentDictionary<string, AccountState> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private readonly ILogger<AccountLedger> _logger;

    public const string CoinbaseAddress = "0x0000000000000000000000000000000000000000";

    public AccountLedger(ILogger<AccountLedger> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Retrieves the current confirmed account state for an address.
    /// If the account has no prior transactions, returns a new state with 0 balance and nonce 0.
    /// </summary>
    public AccountState GetAccount(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return new AccountState(string.Empty, 0, 0);
        }

        if (_accounts.TryGetValue(address.Trim(), out var state))
        {
            return new AccountState(state.Address, state.Balance, state.Nonce);
        }

        return new AccountState(address.Trim(), 0, 0);
    }

    /// <summary>
    /// Gets the confirmed balance of an address.
    /// </summary>
    public decimal GetConfirmedBalance(string address) => GetAccount(address).Balance;

    /// <summary>
    /// Gets the confirmed next transaction sequence number (Nonce) of an address.
    /// </summary>
    public long GetConfirmedNonce(string address) => GetAccount(address).Nonce;

    /// <summary>
    /// Reconstructs the entire account state ledger from scratch by replaying transactions from the chain.
    /// </summary>
    public void Rebuild(IEnumerable<Block> chain)
    {
        lock (_stateLock)
        {
            _accounts.Clear();
            _logger.LogInformation("Rebuilding account ledger from blockchain...");

            foreach (var block in chain)
            {
                ApplyBlockCore(block);
            }

            _logger.LogInformation("Account ledger rebuilt successfully. Total active accounts: {Count}.", _accounts.Count);
        }
    }

    /// <summary>
    /// Applies a newly appended block to the live account state.
    /// </summary>
    public void ApplyBlock(Block block)
    {
        lock (_stateLock)
        {
            ApplyBlockCore(block);
        }
    }

    private void ApplyBlockCore(Block block)
    {
        if (block?.Transactions == null) return;

        foreach (var tx in block.Transactions)
        {
            if (tx == null || tx.Amount <= 0) continue;

            // Handle sender debit and nonce increment (unless coinbase / system)
            if (!IsSystemOrCoinbase(tx.Sender))
            {
                var senderAccount = _accounts.GetOrAdd(tx.Sender.Trim(), addr => new AccountState(addr, 0, 0));
                senderAccount.Balance -= tx.Amount;
                senderAccount.Nonce++;
            }

            // Handle recipient credit
            if (!string.IsNullOrWhiteSpace(tx.Recipient))
            {
                var recipientAccount = _accounts.GetOrAdd(tx.Recipient.Trim(), addr => new AccountState(addr, 0, 0));
                recipientAccount.Balance += tx.Amount;
            }
        }
    }

    /// <summary>
    /// Credits test funds to an address directly (Faucet for development and testing).
    /// </summary>
    public AccountState CreditFaucet(string address, decimal amount)
    {
        lock (_stateLock)
        {
            var account = _accounts.GetOrAdd(address.Trim(), addr => new AccountState(addr, 0, 0));
            account.Balance += amount;
            _logger.LogInformation("Faucet credited {Amount} to {Address}. New balance: {Balance}.", amount, address, account.Balance);
            return new AccountState(account.Address, account.Balance, account.Nonce);
        }
    }

    /// <summary>
    /// Validates an incoming transaction against confirmed state and other pending transactions from the same sender.
    /// Checks positive amount, address formats, hash integrity, digital signature, sequential nonce, and available balance.
    /// </summary>
    public (bool IsValid, string? ErrorMessage) ValidateTransaction(
        Transaction tx,
        decimal pendingSpentBySender,
        long pendingCountBySender)
    {
        if (tx == null)
        {
            return (false, "Transaction cannot be null.");
        }

        if (tx.Amount <= 0)
        {
            return (false, $"Transaction amount must be positive. Provided: {tx.Amount}.");
        }

        if (string.IsNullOrWhiteSpace(tx.Sender))
        {
            return (false, "Sender address is required.");
        }

        if (string.IsNullOrWhiteSpace(tx.Recipient))
        {
            return (false, "Recipient address is required.");
        }

        if (string.Equals(tx.Sender.Trim(), tx.Recipient.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Sender and recipient addresses cannot be identical.");
        }

        // Verify transaction hash
        if (string.IsNullOrWhiteSpace(tx.Id))
        {
            tx.Id = tx.CalculateHash();
        }
        else if (!string.Equals(tx.Id, tx.CalculateHash(), StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Transaction Id hash does not match payload content.");
        }

        // Verify digital signature (unless system/coinbase)
        if (!IsSystemOrCoinbase(tx.Sender))
        {
            if (string.IsNullOrWhiteSpace(tx.PublicKey))
            {
                return (false, "Transaction public key is required for signature verification.");
            }

            if (string.IsNullOrWhiteSpace(tx.Signature))
            {
                return (false, "Transaction signature is required.");
            }

            if (!tx.VerifySignature())
            {
                return (false, "Digital signature verification failed or public key does not match sender address.");
            }

            // Verify sequential account Nonce
            var confirmedNonce = GetConfirmedNonce(tx.Sender);
            var expectedNonce = confirmedNonce + pendingCountBySender;
            if (tx.Nonce != expectedNonce)
            {
                return (false, $"Invalid transaction nonce {tx.Nonce}. Expected {expectedNonce} (confirmed: {confirmedNonce}, pending: {pendingCountBySender}).");
            }

            // Verify available balance
            var confirmedBalance = GetConfirmedBalance(tx.Sender);
            var availableBalance = confirmedBalance - pendingSpentBySender;
            if (tx.Amount > availableBalance)
            {
                return (false, $"Insufficient balance. Available: {availableBalance} (confirmed: {confirmedBalance}, pending spent: {pendingSpentBySender}), requested: {tx.Amount}.");
            }
        }

        return (true, null);
    }

    public static bool IsSystemOrCoinbase(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return true;
        var trimmed = address.Trim();
        return string.Equals(trimmed, CoinbaseAddress, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "COINBASE", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, "SYSTEM", StringComparison.OrdinalIgnoreCase);
    }
}
