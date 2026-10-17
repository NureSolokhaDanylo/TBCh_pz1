using System.Collections.Concurrent;
using Blockchain.Shared.Models;

namespace Miner.Services;

/// <summary>
/// In-memory pool for unconfirmed pending transactions.
/// Validates transactions against the 4 mandatory mempool rules before acceptance:
/// 1. Digital signature validity.
/// 2. Available balance sufficiency (Amount + Fee + pending spends).
/// 3. Strictly sequential sender Nonce.
/// 4. No duplicate transaction in mempool or confirmed blocks.
/// </summary>
public class Mempool
{
    private readonly ConcurrentDictionary<string, Transaction> _transactions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// Validates and adds a transaction to the mempool.
    /// </summary>
    public (bool Success, string? ErrorMessage) Add(Transaction transaction, AccountLedger? ledger = null)
    {
        if (transaction == null)
        {
            return (false, "Transaction cannot be null.");
        }

        lock (_lock)
        {
            // Check #1 (Signature & payload integrity) if ledger is provided
            if (ledger != null)
            {
                var pendingFromSender = _transactions.Values
                    .Where(t => string.Equals(t.Sender?.Trim(), transaction.Sender?.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var pendingSpent = pendingFromSender.Sum(t => t.Amount + t.Fee);
                var pendingCount = pendingFromSender.Count;

                // Check #4 (Duplicate in mempool) after signature check if signature is valid, or before if identical ID
                if (!string.IsNullOrWhiteSpace(transaction.Id) && _transactions.ContainsKey(transaction.Id))
                {
                    return (false, $"Check #4 failed (Duplicate): Transaction '{transaction.Id}' already exists in the mempool.");
                }

                var (isValid, errorMessage) = ledger.ValidateTransaction(transaction, pendingSpent, pendingCount);
                if (!isValid)
                {
                    return (false, errorMessage);
                }
            }

            if (string.IsNullOrWhiteSpace(transaction.Id))
            {
                transaction.Id = transaction.CalculateHash();
            }

            if (_transactions.ContainsKey(transaction.Id))
            {
                return (false, $"Check #4 failed (Duplicate): Transaction '{transaction.Id}' already exists in the mempool.");
            }

            _transactions.TryAdd(transaction.Id, transaction);
            return (true, null);
        }
    }

    /// <summary>
    /// Accepts a transaction into memory without ledger validation (e.g., during simple structural tests).
    /// </summary>
    public bool Add(Transaction transaction) => Add(transaction, null).Success;

    /// <summary>
    /// Retrieves all pending transactions currently in the mempool.
    /// </summary>
    public IReadOnlyList<Transaction> GetAll()
    {
        return _transactions.Values
            .OrderBy(t => t.Nonce)
            .ThenBy(t => t.Timestamp)
            .ToList();
    }

    /// <summary>
    /// Retrieves a candidate batch of pending transactions for block assembly, ordered by Nonce then Timestamp.
    /// </summary>
    public IReadOnlyList<Transaction> GetCandidateTransactions()
    {
        return _transactions.Values
            .OrderBy(t => t.Nonce)
            .ThenBy(t => t.Timestamp)
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
    /// Clears all pending transactions from the mempool.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _transactions.Clear();
        }
    }

    /// <summary>
    /// Total count of pending transactions in the mempool.
    /// </summary>
    public int Count => _transactions.Count;

    /// <summary>
    /// Validates all transactions currently in the mempool against the provided AccountLedger,
    /// removing any transactions that have become invalid (e.g. after a chain reorganization or rollback).
    /// </summary>
    public int PruneInvalidTransactions(AccountLedger ledger)
    {
        lock (_lock)
        {
            var txs = _transactions.Values
                .OrderBy(t => t.Nonce)
                .ThenBy(t => t.Timestamp)
                .ToList();

            var toRemove = new List<string>();
            var tempLedgerSpend = new Dictionary<string, (decimal Spent, long Count)>(StringComparer.OrdinalIgnoreCase);

            foreach (var tx in txs)
            {
                var sender = tx.Sender?.Trim() ?? string.Empty;
                var (currentSpent, currentCount) = tempLedgerSpend.TryGetValue(sender, out var c) ? c : (0m, 0L);

                var (isValid, _) = ledger.ValidateTransaction(tx, currentSpent, currentCount);
                if (!isValid)
                {
                    toRemove.Add(tx.Id);
                }
                else
                {
                    tempLedgerSpend[sender] = (currentSpent + tx.Amount + tx.Fee, currentCount + 1);
                }
            }

            foreach (var id in toRemove)
            {
                _transactions.TryRemove(id, out _);
            }

            return toRemove.Count;
        }
    }
}
