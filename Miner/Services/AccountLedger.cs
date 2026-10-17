using System.Collections.Concurrent;
using Blockchain.Shared.Models;

namespace Miner.Services;

/// <summary>
/// In-memory ledger tracking confirmed account states (balance and transaction nonce)
/// and confirmed transaction IDs.
/// State is deterministically reconstructed by replaying the blockchain from the Genesis block.
/// </summary>
public class AccountLedger
{
    private readonly ConcurrentDictionary<string, AccountState> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _confirmedTransactionIds = new(StringComparer.OrdinalIgnoreCase);
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

        lock (_stateLock)
        {
            if (_accounts.TryGetValue(address.Trim(), out var state))
            {
                return new AccountState(state.Address, state.Balance, state.Nonce);
            }
        }

        return new AccountState(address.Trim(), 0, 0);
    }

    /// <summary>
    /// Gets all active account states currently tracked by the ledger.
    /// </summary>
    public IReadOnlyList<AccountState> GetAllAccounts()
    {
        lock (_stateLock)
        {
            return _accounts.Values
                .Select(s => new AccountState(s.Address, s.Balance, s.Nonce))
                .OrderByDescending(s => s.Balance)
                .ToList();
        }
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
    /// Checks whether a transaction ID has already been confirmed in a block on the chain.
    /// </summary>
    public bool IsTransactionConfirmed(string? transactionId)
    {
        if (string.IsNullOrWhiteSpace(transactionId)) return false;
        lock (_stateLock)
        {
            return _confirmedTransactionIds.Contains(transactionId.Trim());
        }
    }

    /// <summary>
    /// Deterministically calculates the balance and nonce of any address by scanning the provided blockchain from Genesis.
    /// </summary>
    public static AccountState CalculateAccountStateFromChain(IEnumerable<Block> chain, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return new AccountState(string.Empty, 0, 0);
        }

        var target = address.Trim();
        decimal balance = 0m;
        long nonce = 0;

        foreach (var block in chain)
        {
            if (block?.Transactions == null) continue;
            foreach (var tx in block.Transactions)
            {
                if (tx == null || tx.Amount <= 0) continue;

                if (!IsSystemOrCoinbase(tx.Sender) &&
                    string.Equals(tx.Sender.Trim(), target, StringComparison.OrdinalIgnoreCase))
                {
                    balance -= (tx.Amount + tx.Fee);
                    nonce++;
                }

                if (!string.IsNullOrWhiteSpace(tx.Recipient) &&
                    string.Equals(tx.Recipient.Trim(), target, StringComparison.OrdinalIgnoreCase))
                {
                    balance += tx.Amount;
                }
            }
        }

        return new AccountState(target, balance, nonce);
    }

    /// <summary>
    /// Reconstructs the entire account state ledger from scratch by replaying transactions from the chain.
    /// </summary>
    public void Rebuild(IEnumerable<Block> chain)
    {
        lock (_stateLock)
        {
            _accounts.Clear();
            _confirmedTransactionIds.Clear();
            _logger.LogInformation("Rebuilding account ledger from blockchain...");

            foreach (var block in chain)
            {
                ApplyBlockCore(block);
            }

            _logger.LogInformation("Account ledger rebuilt successfully. Total active accounts: {Count}, confirmed txs: {TxCount}.",
                _accounts.Count, _confirmedTransactionIds.Count);
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

            if (!string.IsNullOrWhiteSpace(tx.Id))
            {
                _confirmedTransactionIds.Add(tx.Id.Trim());
            }

            // Handle sender debit (Amount + Fee) and nonce increment (unless coinbase / system)
            if (!IsSystemOrCoinbase(tx.Sender))
            {
                var senderAccount = _accounts.GetOrAdd(tx.Sender.Trim(), addr => new AccountState(addr, 0, 0));
                senderAccount.Balance -= (tx.Amount + tx.Fee);
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
    /// Credits test funds to an address directly in memory (for development and unit testing).
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
    /// Validates an incoming transaction against the 4 mandatory mempool rules:
    /// 1. Valid digital signature (and untampered payload).
    /// 2. Sufficient sender balance accounting for pending mempool transactions (Amount + Fee).
    /// 3. Strictly sequential sender Nonce.
    /// 4. No duplicate transaction in mempool or confirmed blocks.
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

        if (tx.Fee < 0)
        {
            return (false, $"Transaction fee cannot be negative. Provided: {tx.Fee}.");
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

        if (!IsSystemOrCoinbase(tx.Sender))
        {
            // Mandatory Check #1: Digital signature validity and payload integrity
            if (string.IsNullOrWhiteSpace(tx.PublicKey) ||
                string.IsNullOrWhiteSpace(tx.Signature) ||
                !tx.VerifySignature())
            {
                return (false, "Check #1 failed (Invalid Signature): Digital signature verification failed or transaction data was tampered with after signing.");
            }

            // Mandatory Check #4 & #3 (Replay / Duplicate in confirmed chain)
            var confirmedNonce = GetConfirmedNonce(tx.Sender);
            var expectedNonce = confirmedNonce + pendingCountBySender;

            if (IsTransactionConfirmed(tx.Id))
            {
                return (false, $"Check #3 & #4 failed (Replay / Duplicate): Transaction '{tx.Id}' is already confirmed in a block on the blockchain and sender nonce {tx.Nonce} has already been used (expected next nonce: {expectedNonce}).");
            }

            // Mandatory Check #2: Sufficient sender funds including pending mempool spends (Amount + Fee)
            var confirmedBalance = GetConfirmedBalance(tx.Sender);
            var availableBalance = confirmedBalance - pendingSpentBySender;
            var requiredTotal = tx.Amount + tx.Fee;
            if (requiredTotal > availableBalance)
            {
                return (false, $"Check #2 failed (Insufficient Balance): Sender available balance is {availableBalance} (confirmed: {confirmedBalance}, pending in mempool: {pendingSpentBySender}), which is less than required {requiredTotal} (amount: {tx.Amount} + fee: {tx.Fee}).");
            }

            // Mandatory Check #3: Strictly sequential sender Nonce
            if (tx.Nonce != expectedNonce)
            {
                return (false, $"Check #3 failed (Invalid Nonce): Sender nonce {tx.Nonce} is not sequential. Expected {expectedNonce} (confirmed nonce: {confirmedNonce}, pending in mempool: {pendingCountBySender}).");
            }
        }

        if (string.IsNullOrWhiteSpace(tx.Id))
        {
            tx.Id = tx.CalculateHash();
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
