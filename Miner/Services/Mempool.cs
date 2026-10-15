using System.Collections.Concurrent;
using Blockchain.Shared.Models;

namespace Miner.Services;

/// <summary>
/// In-memory pool for unconfirmed pending transactions.
/// Validates transactions against confirmed ledger state and pending sender spends before acceptance.
/// </summary>
public class Mempool
{
    private readonly ConcurrentDictionary<string, Transaction> _transactions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// Validates and adds a transaction to the mempool.
    /// Checks cryptographic signature, sequential account Nonce, and sufficient available balance.
    /// </summary>
    public (bool Success, string? ErrorMessage) Add(Transaction transaction, AccountLedger? ledger = null)
    {
        if (transaction == null)
        {
            return (false, "Transaction cannot be null.");
        }

        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(transaction.Id))
            {
                transaction.Id = transaction.CalculateHash();
            }

            if (_transactions.ContainsKey(transaction.Id))
            {
                return (false, "Transaction already exists in mempool.");
            }

            if (ledger != null)
            {
                // Calculate total amount already spent by this sender in the mempool
                var pendingFromSender = _transactions.Values
                    .Where(t => string.Equals(t.Sender, transaction.Sender, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var pendingSpent = pendingFromSender.Sum(t => t.Amount);
                var pendingCount = pendingFromSender.Count;

                var (isValid, errorMessage) = ledger.ValidateTransaction(transaction, pendingSpent, pendingCount);
                if (!isValid)
                {
                    return (false, errorMessage);
                }
            }

            _transactions.TryAdd(transaction.Id, transaction);
            return (true, null);
        }
    }

    /// <summary>
    /// Accepts a transaction into memory without ledger validation (e.g., during tests).
    /// </summary>
    public bool Add(Transaction transaction) => Add(transaction, null).Success;

    /// <summary>
    /// Retrieves all pending transactions currently in the mempool.
    /// </summary>
    public IReadOnlyList<Transaction> GetAll()
    {
        return _transactions.Values.ToList();
    }

    /// <summary>
    /// Retrieves a candidate batch of pending transactions for block assembly.
    /// </summary>
    public IReadOnlyList<Transaction> GetCandidateTransactions()
    {
        return _transactions.Values
            .OrderBy(t => t.Timestamp)
            .ThenBy(t => t.Nonce)
            .ToList();
    }

    /// <summary>
    /// Retrieves a transaction by its ID if present in the mempool.
    /// </summary>
    public Transaction? GetById(string id)
    {
        return _transactions.GetValueOrDefault(id);
    }

    /// <summary>
    /// Removes specified transactions from the mempool (e.g., after being mined into a block).
    /// </summary>
    public void Remove(IEnumerable<string> transactionIds)
    {
        foreach (var id in transactionIds)
        {
            _transactions.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Total count of pending transactions in the mempool.
    /// </summary>
    public int Count => _transactions.Count;
}
