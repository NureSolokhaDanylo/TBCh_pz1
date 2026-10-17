using Blockchain.Shared.Common;
using Blockchain.Shared.Models;
using Blockchain.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Miner.Services;

public record AttackDemoResult(
    int AttackNumber,
    string Title,
    string ScenarioDescription,
    string AttackAction,
    bool DefenseSucceeded,
    string RejectionVerdict
);

/// <summary>
/// Executes the 5 mandatory blockchain security attack scenarios in an isolated sandbox
/// and captures the exact rejection verdicts from ValidateChain and Mempool.
/// </summary>
public class AttackDemonstrationService
{
    private const int DemoDifficulty = 2; // Fast PoW for instant interactive demonstration
    private const decimal BaseReward = 50.0m;

    public IReadOnlyList<AttackDemoResult> RunAllAttacks()
    {
        return
        [
            RunAttack1_TamperOldBlockWithoutRemining(),
            RunAttack2_TamperOldBlockWithRemining(),
            RunAttack3_ForgedOrTamperedSignature(),
            RunAttack4_DoubleSpendInMempool(),
            RunAttack5_ReplayConfirmedTransaction()
        ];
    }

    /// <summary>
    /// Attack 1: Modifying transaction amount in an old block without recalculating Merkle root or PoW.
    /// Caught by ValidateChain due to MerkleRoot mismatch.
    /// </summary>
    public AttackDemoResult RunAttack1_TamperOldBlockWithoutRemining()
    {
        var (chain, alice, bob) = BuildValidSampleChain();

        // Tamper with Block #2's transfer transaction amount (change 20 -> 9999) without recalculating block
        var targetTx = chain[2].Transactions[1];
        var originalAmount = targetTx.Amount;
        targetTx.Amount = 9999m;

        var validator = new StructuralBlockchainValidator { BaseBlockReward = BaseReward };
        var (isValid, errorMessage) = validator.ValidateChain(chain);

        return new AttackDemoResult(
            AttackNumber: 1,
            Title: "Attack 1: Tampering with Transaction Amount in Old Block (No Re-mining)",
            ScenarioDescription: $"Valid 4-block chain (#0..#3). In Block #2, Alice transferred {originalAmount} coins to Bob.",
            AttackAction: $"Attacker modifies transaction amount in Block #2 from {originalAmount} to 9999 coins directly in chain storage without recalculating the block header.",
            DefenseSucceeded: !isValid,
            RejectionVerdict: errorMessage ?? "Unexpectedly valid"
        );
    }

    /// <summary>
    /// Attack 2: Modifying data in an old block WITH Merkle root recalculation and PoW re-mining of that block.
    /// Caught by ValidateChain on the NEXT block due to PreviousHash link mismatch.
    /// </summary>
    public AttackDemoResult RunAttack2_TamperOldBlockWithRemining()
    {
        var (chain, _, _) = BuildValidSampleChain();

        // Tamper with Block #1, recalculate its MerkleRoot, and re-mine its PoW hash
        var block1 = chain[1];
        var oldHash = block1.Header.Hash;
        block1.Transactions[0].Recipient = "0xdeadbeef00000000000000000000000000000000";
        block1.Transactions[0].Id = block1.Transactions[0].CalculateHash();
        block1.Header.MerkleRoot = block1.CalculateMerkleRoot();
        block1.Header.Nonce = 0;
        block1.Header.Mine(DemoDifficulty);

        var validator = new StructuralBlockchainValidator { BaseBlockReward = BaseReward };
        var (isValid, errorMessage) = validator.ValidateChain(chain);

        return new AttackDemoResult(
            AttackNumber: 2,
            Title: "Attack 2: Tampering with Old Block + Recomputing Merkle Root & Re-mining PoW",
            ScenarioDescription: $"Valid 4-block chain (#0..#3). Block #1 originally had hash {oldHash[..16]}..., which is referenced by Block #2.PreviousHash.",
            AttackAction: $"Attacker changes Block #1 recipient to 0xdeadbeef..., recomputes Block #1 MerkleRoot, and re-mines Block #1 PoW (new hash: {block1.Header.Hash[..16]}...).",
            DefenseSucceeded: !isValid,
            RejectionVerdict: errorMessage ?? "Unexpectedly valid"
        );
    }

    /// <summary>
    /// Attack 3: Submitting a transaction with a forged signature or tampered amount after signing.
    /// Caught at Mempool entrance by Check #1 (Invalid Signature).
    /// </summary>
    public AttackDemoResult RunAttack3_ForgedOrTamperedSignature()
    {
        var (chain, alice, bob) = BuildValidSampleChain();
        var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
        ledger.Rebuild(chain);
        var mempool = new Mempool();

        // Alice signs a legitimate transaction for 5 coins (Nonce = 1), then attacker alters Amount to 25 coins
        var tx = new Transaction(alice.Address, bob.Address, 5m, nonce: 1, fee: 1m);
        tx.Sign(alice.PrivKey, alice.PubKey);

        // Attacker modifies amount after signing
        tx.Amount = 25m;

        var (accepted, errorMessage) = mempool.Add(tx, ledger);

        return new AttackDemoResult(
            AttackNumber: 3,
            Title: "Attack 3: Forged Digital Signature / Post-Signing Amount Tampering",
            ScenarioDescription: $"Alice (Address: {alice.Address[..14]}...) signs a valid transaction sending 5 coins to Bob.",
            AttackAction: "Attacker intercepts the signed transaction and modifies Amount from 5 to 25 coins (or signs using another private key) before submitting to Mempool.",
            DefenseSucceeded: !accepted,
            RejectionVerdict: errorMessage ?? "Unexpectedly accepted"
        );
    }

    /// <summary>
    /// Attack 4: Double Spend in Mempool (sending two transactions that exceed available balance when combined).
    /// First tx is accepted; second tx is rejected at Mempool entrance by Check #2 (Insufficient Balance).
    /// </summary>
    public AttackDemoResult RunAttack4_DoubleSpendInMempool()
    {
        var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
        var mempool = new Mempool();

        var (alicePriv, alicePub, aliceAddr) = CryptoHelper.GenerateKeyPair();
        var (_, _, bobAddr) = CryptoHelper.GenerateKeyPair();
        var (_, _, charlieAddr) = CryptoHelper.GenerateKeyPair();

        // Credit Alice with 100 coins (via 2 mined blocks of 50 coins each)
        var chain = new List<Block> { GenesisBlock.Instance };
        for (int i = 1; i <= 2; i++)
        {
            var cb = new Transaction(AccountLedger.CoinbaseAddress, aliceAddr, BaseReward, 0);
            var txs = new List<Transaction> { cb };
            var header = new BlockHeader(i, chain[^1].Header.Hash, MerkleTree.ComputeRoot(txs), DemoDifficulty);
            header.Mine(DemoDifficulty);
            chain.Add(new Block(header, txs));
        }
        ledger.Rebuild(chain);

        // Tx 1: Alice sends full 100 coins to Bob (Nonce = 0)
        var tx1 = new Transaction(aliceAddr, bobAddr, 100m, nonce: 0, fee: 0m);
        tx1.Sign(alicePriv, alicePub);
        var (accepted1, _) = mempool.Add(tx1, ledger);

        // Tx 2: Alice immediately tries to send 100 coins to Charlie (Nonce = 1) before Tx 1 is mined
        var tx2 = new Transaction(aliceAddr, charlieAddr, 100m, nonce: 1, fee: 0m);
        tx2.Sign(alicePriv, alicePub);
        var (accepted2, errorMessage2) = mempool.Add(tx2, ledger);

        return new AttackDemoResult(
            AttackNumber: 4,
            Title: "Attack 4: Double Spend Attack in Mempool",
            ScenarioDescription: $"Alice has a confirmed balance of 100 coins. She submits Tx #1 (100 coins -> Bob, Nonce 0), which enters Mempool (accepted={accepted1}).",
            AttackAction: "While Tx #1 is still pending in Mempool, Alice submits Tx #2 (100 coins -> Charlie, Nonce 1) attempting to spend the same 100 coins a second time.",
            DefenseSucceeded: accepted1 && !accepted2,
            RejectionVerdict: errorMessage2 ?? "Unexpectedly accepted"
        );
    }

    /// <summary>
    /// Attack 5: Replay Attack (re-submitting a valid transaction that was already confirmed in an earlier block).
    /// Rejected at Mempool entrance by Check #3 & #4 (Nonce already used & Duplicate confirmed transaction).
    /// </summary>
    public AttackDemoResult RunAttack5_ReplayConfirmedTransaction()
    {
        var (chain, alice, _) = BuildValidSampleChain();
        var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
        ledger.Rebuild(chain);
        var mempool = new Mempool();

        // Extract the already-confirmed transaction from Block #2 (Alice -> Bob, 20 coins, Nonce = 0)
        var confirmedTx = chain[2].Transactions[1];

        // Re-submit the exact same transaction to the Mempool
        var (accepted, errorMessage) = mempool.Add(confirmedTx, ledger);

        return new AttackDemoResult(
            AttackNumber: 5,
            Title: "Attack 5: Replay Attack (Re-sending Confirmed Transaction)",
            ScenarioDescription: $"Transaction {confirmedTx.Id[..16]}... (Alice -> Bob, 20 coins, Nonce 0) was already mined and confirmed in Block #2. Alice's current confirmed Nonce is {ledger.GetConfirmedNonce(alice.Address)}.",
            AttackAction: "Attacker captures the signed transaction from Block #2 and re-submits it identically to the Mempool to drain Alice's funds again.",
            DefenseSucceeded: !accepted,
            RejectionVerdict: errorMessage ?? "Unexpectedly accepted"
        );
    }

    private static (List<Block> Chain, (string PrivKey, string PubKey, string Address) Alice, (string PrivKey, string PubKey, string Address) Bob) BuildValidSampleChain()
    {
        var alice = CryptoHelper.GenerateKeyPair();
        var bob = CryptoHelper.GenerateKeyPair();

        var chain = new List<Block> { GenesisBlock.Instance };

        // Block #1: Alice mines block and receives 50 coins Coinbase reward
        var cb1 = new Transaction(AccountLedger.CoinbaseAddress, alice.Address, BaseReward, 0, fee: 0m);
        var txs1 = new List<Transaction> { cb1 };
        var h1 = new BlockHeader(1, chain[^1].Header.Hash, MerkleTree.ComputeRoot(txs1), DemoDifficulty);
        h1.Mine(DemoDifficulty);
        chain.Add(new Block(h1, txs1));

        // Block #2: Alice sends 20 coins (fee 1) to Bob (Nonce = 0); Bob mines Block #2 (reward 50 + 1 = 51)
        var transferTx = new Transaction(alice.Address, bob.Address, 20m, nonce: 0, fee: 1m);
        transferTx.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);
        var cb2 = new Transaction(AccountLedger.CoinbaseAddress, bob.Address, BaseReward + transferTx.Fee, 0, fee: 0m);
        var txs2 = new List<Transaction> { cb2, transferTx };
        var h2 = new BlockHeader(2, chain[^1].Header.Hash, MerkleTree.ComputeRoot(txs2), DemoDifficulty);
        h2.Mine(DemoDifficulty);
        chain.Add(new Block(h2, txs2));

        // Block #3: Bob mines Block #3 (reward 50)
        var cb3 = new Transaction(AccountLedger.CoinbaseAddress, bob.Address, BaseReward, 0, fee: 0m);
        var txs3 = new List<Transaction> { cb3 };
        var h3 = new BlockHeader(3, chain[^1].Header.Hash, MerkleTree.ComputeRoot(txs3), DemoDifficulty);
        h3.Mine(DemoDifficulty);
        chain.Add(new Block(h3, txs3));

        return (chain, alice, bob);
    }
}
