using System.Security.Cryptography;
using System.Text;
using Blockchain.Shared.Common;
using Blockchain.Shared.Dtos;
using Blockchain.Shared.Models;
using Blockchain.Shared.Services;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Miner.Common;
using Miner.Services;

namespace Miner.Tests;

public class BlockchainSpecificationTests
{
    [Fact]
    public void Test01_Secp256k1_KeyGeneration_And_AddressDerivation_ProducesValidAddress()
    {
        var (privHex, pubHex, address) = CryptoHelper.GenerateKeyPair();

        Assert.False(string.IsNullOrWhiteSpace(privHex));
        Assert.False(string.IsNullOrWhiteSpace(pubHex));
        Assert.StartsWith("0x", address);
        Assert.Equal(42, address.Length);

        var rederived = CryptoHelper.DeriveAddress(pubHex);
        Assert.Equal(address, rederived);
    }

    [Fact]
    public void Test02_DeterministicByteSerialization_IdenticalFields_ProduceIdenticalBytes()
    {
        var timestamp = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        var txA = new Transaction("id", "0xaaa", "0xbbb", 50m, 1, timestamp, fee: 1m);
        var txB = new Transaction("id", "0xAAA", "0xBBB", 50.0000m, 1, timestamp, fee: 1.00m);

        Assert.Equal(txA.ToBytes(), txB.ToBytes());
        Assert.Equal(txA.CalculateHash(), txB.CalculateHash());

        var headerA = new BlockHeader(1, "0000abc", "1234def", timestamp, 42, 4, "");
        var headerB = new BlockHeader(1, "0000ABC", "1234DEF", timestamp, 42, 4, "");

        Assert.Equal(headerA.ToBytes(), headerB.ToBytes());
        Assert.Equal(headerA.CalculateHash(), headerB.CalculateHash());
    }

    [Fact]
    public void Test03_DigitalSignature_SignAndVerify_ValidAndInvalidCases()
    {
        var alice = CryptoHelper.GenerateKeyPair();
        var bob = CryptoHelper.GenerateKeyPair();

        var tx = new Transaction(alice.Address, bob.Address, 25m, nonce: 0, fee: 0.5m);
        tx.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);

        Assert.True(tx.VerifySignature());

        // Signing with Bob's private key while claiming Alice's sender address must fail verification
        var forgedTx = new Transaction(alice.Address, bob.Address, 25m, nonce: 0, fee: 0.5m);
        forgedTx.Sign(bob.PrivateKeyHex, bob.PublicKeyHex);
        Assert.False(forgedTx.VerifySignature());
    }

    [Fact]
    public void Test04_MerkleTree_EvenNumberOfTransactions_ComputesCorrectRoot()
    {
        var tx1 = new Transaction("0x1", "0x2", 10m, 0);
        var tx2 = new Transaction("0x2", "0x3", 20m, 0);
        var tx3 = new Transaction("0x3", "0x4", 30m, 0);
        var tx4 = new Transaction("0x4", "0x5", 40m, 0);

        var h1 = tx1.CalculateHash();
        var h2 = tx2.CalculateHash();
        var h3 = tx3.CalculateHash();
        var h4 = tx4.CalculateHash();

        var h12 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(h1 + h2))).ToLowerInvariant();
        var h34 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(h3 + h4))).ToLowerInvariant();
        var expectedRoot = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(h12 + h34))).ToLowerInvariant();

        var actualRoot = MerkleTree.ComputeRoot([tx1, tx2, tx3, tx4]);
        Assert.Equal(expectedRoot, actualRoot);
    }

    [Fact]
    public void Test05_MerkleTree_OddNumberOfTransactions_DuplicatesLastLeafOnOddLevels()
    {
        var tx1 = new Transaction("0x1", "0x2", 10m, 0);
        var tx2 = new Transaction("0x2", "0x3", 20m, 0);
        var tx3 = new Transaction("0x3", "0x4", 30m, 0);

        var h1 = tx1.CalculateHash();
        var h2 = tx2.CalculateHash();
        var h3 = tx3.CalculateHash();

        // Odd count (3): h3 is duplicated to pair with itself -> (h1+h2) and (h3+h3)
        var h12 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(h1 + h2))).ToLowerInvariant();
        var h33 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(h3 + h3))).ToLowerInvariant();
        var expectedRoot = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(h12 + h33))).ToLowerInvariant();

        var actualRoot = MerkleTree.ComputeRoot([tx1, tx2, tx3]);
        Assert.Equal(expectedRoot, actualRoot);
    }

    [Fact]
    public void Test06_Mempool_Check1_RejectsInvalidOrTamperedSignature()
    {
        var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
        var mempool = new Mempool();
        var alice = CryptoHelper.GenerateKeyPair();
        var bob = CryptoHelper.GenerateKeyPair();

        ledger.CreditFaucet(alice.Address, 100m);

        var tx = new Transaction(alice.Address, bob.Address, 10m, nonce: 0, fee: 1m);
        tx.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);
        tx.Signature = "00" + tx.Signature![2..]; // Corrupt signature

        var (accepted, error) = mempool.Add(tx, ledger);
        Assert.False(accepted);
        Assert.Contains("Check #1", error);
    }

    [Fact]
    public void Test07_Mempool_Check2_RejectsInsufficientBalanceAccountingForPendingAndFee()
    {
        var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
        var mempool = new Mempool();
        var alice = CryptoHelper.GenerateKeyPair();
        var bob = CryptoHelper.GenerateKeyPair();

        ledger.CreditFaucet(alice.Address, 100m);

        // Tx 1 spends 60 + 5 fee = 65 coins (leaving 35 available)
        var tx1 = new Transaction(alice.Address, bob.Address, 60m, nonce: 0, fee: 5m);
        tx1.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);
        Assert.True(mempool.Add(tx1, ledger).Success);

        // Tx 2 tries to spend 35 + 1 fee = 36 coins (exceeds 35 remaining)
        var tx2 = new Transaction(alice.Address, bob.Address, 35m, nonce: 1, fee: 1m);
        tx2.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);
        var (accepted2, error2) = mempool.Add(tx2, ledger);

        Assert.False(accepted2);
        Assert.Contains("Check #2", error2);
    }

    [Fact]
    public void Test08_Mempool_Check3_RejectsOutOfOrderOrSkippedNonce()
    {
        var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
        var mempool = new Mempool();
        var alice = CryptoHelper.GenerateKeyPair();
        var bob = CryptoHelper.GenerateKeyPair();

        ledger.CreditFaucet(alice.Address, 100m);

        // Expected nonce is 0, but sender provides nonce 2
        var tx = new Transaction(alice.Address, bob.Address, 10m, nonce: 2, fee: 1m);
        tx.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);

        var (accepted, error) = mempool.Add(tx, ledger);
        Assert.False(accepted);
        Assert.Contains("Check #3", error);
    }

    [Fact]
    public void Test09_Mempool_Check4_RejectsDuplicateInMempoolAndConfirmedChain()
    {
        var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
        var mempool = new Mempool();
        var alice = CryptoHelper.GenerateKeyPair();
        var bob = CryptoHelper.GenerateKeyPair();

        ledger.CreditFaucet(alice.Address, 100m);

        var tx = new Transaction(alice.Address, bob.Address, 10m, nonce: 0, fee: 1m);
        tx.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);

        Assert.True(mempool.Add(tx, ledger).Success);

        // Submitting identical transaction again to mempool must fail Check #4
        var (acceptedDup, errorDup) = mempool.Add(tx, ledger);
        Assert.False(acceptedDup);
        Assert.Contains("Check #4", errorDup);
    }

    [Fact]
    public void Test10_Attack1_TamperAmountInOldBlockWithoutRemining_CaughtByValidateChain()
    {
        var demo = new AttackDemonstrationService();
        var result = demo.RunAttack1_TamperOldBlockWithoutRemining();

        Assert.True(result.DefenseSucceeded);
        Assert.Contains("Merkle root mismatch", result.RejectionVerdict, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test11_Attack2_TamperOldBlockWithRemining_CaughtByNextBlockPrevHash()
    {
        var demo = new AttackDemonstrationService();
        var result = demo.RunAttack2_TamperOldBlockWithRemining();

        Assert.True(result.DefenseSucceeded);
        Assert.Contains("Block #2", result.RejectionVerdict);
        Assert.Contains("PreviousHash", result.RejectionVerdict);
    }

    [Fact]
    public void Test12_Attack3_ForgedSignatureOrModifiedAmount_RejectedByMempoolCheck1()
    {
        var demo = new AttackDemonstrationService();
        var result = demo.RunAttack3_ForgedOrTamperedSignature();

        Assert.True(result.DefenseSucceeded);
        Assert.Contains("Check #1", result.RejectionVerdict);
    }

    [Fact]
    public void Test13_Attack4_DoubleSpendInMempool_SecondTransactionRejectedByCheck2()
    {
        var demo = new AttackDemonstrationService();
        var result = demo.RunAttack4_DoubleSpendInMempool();

        Assert.True(result.DefenseSucceeded);
        Assert.Contains("Check #2", result.RejectionVerdict);
    }

    [Fact]
    public void Test14_Attack5_ReplayConfirmedTransaction_RejectedByMempoolCheck3And4()
    {
        var demo = new AttackDemonstrationService();
        var result = demo.RunAttack5_ReplayConfirmedTransaction();

        Assert.True(result.DefenseSucceeded);
        Assert.Contains("Replay / Duplicate", result.RejectionVerdict);
    }

    [Fact]
    public void Test15_Mining_CoinbaseReward_IncludesBaseRewardPlusSumOfTransactionFees()
    {
        var alice = CryptoHelper.GenerateKeyPair();
        var bob = CryptoHelper.GenerateKeyPair();
        var miner = CryptoHelper.GenerateKeyPair();

        var chain = new List<Block> { GenesisBlock.Instance };

        // Block #1 funds Alice with 50 coins
        var cb1 = new Transaction(AccountLedger.CoinbaseAddress, alice.Address, 50m, 0);
        var b1 = new Block(new BlockHeader(1, chain[0].Header.Hash, MerkleTree.ComputeRoot([cb1]), 1), [cb1]);
        b1.Header.Mine(1);
        chain.Add(b1);

        // Block #2: Alice sends 10 coins with Fee = 3.5 coins; Miner claims inflated 100 coins instead of 53.5
        var userTx = new Transaction(alice.Address, bob.Address, 10m, nonce: 0, fee: 3.5m);
        userTx.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);

        var inflatedCoinbase = new Transaction(AccountLedger.CoinbaseAddress, miner.Address, 100m, 0);
        var b2Invalid = new Block(new BlockHeader(2, b1.Header.Hash, MerkleTree.ComputeRoot([inflatedCoinbase, userTx]), 1), [inflatedCoinbase, userTx]);
        b2Invalid.Header.Mine(1);

        var validator = new StructuralBlockchainValidator { BaseBlockReward = 50m };
        var (isValidInflated, errInflated) = validator.ValidateChain([chain[0], b1, b2Invalid]);
        Assert.False(isValidInflated);
        Assert.Contains("Coinbase reward", errInflated);

        // Correct Coinbase reward = 50 + 3.5 = 53.5
        var validCoinbase = new Transaction(AccountLedger.CoinbaseAddress, miner.Address, 53.5m, 0);
        var b2Valid = new Block(new BlockHeader(2, b1.Header.Hash, MerkleTree.ComputeRoot([validCoinbase, userTx]), 1), [validCoinbase, userTx]);
        b2Valid.Header.Mine(1);

        var (isValidCorrect, _) = validator.ValidateChain([chain[0], b1, b2Valid]);
        Assert.True(isValidCorrect);
    }

    [Fact]
    public void Test16_DifficultyAdjustment_IncreasesWhenMinedTooFast_DecreasesWhenMinedTooSlow()
    {
        var baseTime = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        // Build chain of 10 mined blocks (#1..#10) with fast 1-second intervals (target = 10s/block)
        var fastChain = new List<Block> { GenesisBlock.Instance };
        for (int i = 1; i <= 10; i++)
        {
            var h = new BlockHeader(i, fastChain[^1].Header.Hash, MerkleTree.EmptyRoot, baseTime.AddSeconds(i * 1), 0, 3, "000abc");
            fastChain.Add(new Block(h, []));
        }

        var increasedDiff = StructuralBlockchainValidator.CalculateNextDifficulty(fastChain, adjustmentInterval: 10, targetBlockTimeSeconds: 10, defaultDifficulty: 3);
        Assert.Equal(4, increasedDiff);

        // Build chain of 10 mined blocks (#1..#10) with slow 30-second intervals (target = 10s/block)
        var slowChain = new List<Block> { GenesisBlock.Instance };
        for (int i = 1; i <= 10; i++)
        {
            var h = new BlockHeader(i, slowChain[^1].Header.Hash, MerkleTree.EmptyRoot, baseTime.AddSeconds(i * 30), 0, 3, "000abc");
            slowChain.Add(new Block(h, []));
        }

        var decreasedDiff = StructuralBlockchainValidator.CalculateNextDifficulty(slowChain, adjustmentInterval: 10, targetBlockTimeSeconds: 10, defaultDifficulty: 3);
        Assert.Equal(2, decreasedDiff);
    }

    [Fact]
    public async Task Test17_ForkResolution_SwitchesToHigherCumulativeWorkChain_AndReturnsOrphanedTxToMempool()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"blocks_fork_test_{Guid.NewGuid():N}.json");
        try
        {
            var validator = new StructuralBlockchainValidator { BaseBlockReward = 50m };
            var controller = new MiningController(NullLogger<MiningController>.Instance);
            var mempool = new Mempool();
            var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
            var syncService = new ChainSynchronizationService(
                validator,
                controller,
                mempool,
                ledger,
                new DummyHttpClientFactory(),
                NullLogger<ChainSynchronizationService>.Instance,
                new MiningOptions { Difficulty = 1, BlockReward = 50m })
            {
                BlocksFilePath = tempFile
            };

            var alice = CryptoHelper.GenerateKeyPair();
            var bob = CryptoHelper.GenerateKeyPair();
            var minerA = CryptoHelper.GenerateKeyPair();
            var minerB = CryptoHelper.GenerateKeyPair();

            // Common Block #1: Funds Alice with 50 coins
            var cb1 = new Transaction(AccountLedger.CoinbaseAddress, alice.Address, 50m, 0);
            var b1 = new Block(new BlockHeader(1, GenesisBlock.GenesisHash, MerkleTree.ComputeRoot([cb1]), 1), [cb1]);
            b1.Header.Mine(1);

            // Local Branch (Node B): Block #2 contains Alice's transaction to Bob
            var orphanedTx = new Transaction(alice.Address, bob.Address, 15m, nonce: 0, fee: 1m);
            orphanedTx.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);
            var cb2Local = new Transaction(AccountLedger.CoinbaseAddress, minerB.Address, 51m, 0);
            var b2Local = new Block(new BlockHeader(2, b1.Header.Hash, MerkleTree.ComputeRoot([cb2Local, orphanedTx]), 1), [cb2Local, orphanedTx]);
            b2Local.Header.Mine(1);

            var localChain = new List<Block> { GenesisBlock.Instance, b1, b2Local };
            await JsonLinesHelper.OverwriteAllAsync(tempFile, localChain);
            ledger.Rebuild(localChain);

            Assert.Equal(0, mempool.Count); // orphanedTx was already mined in b2Local

            // Competing Branch (Node A): Has 2 blocks (#2 and #3) on top of #1 without orphanedTx (greater cumulative work)
            var cb2Peer = new Transaction(AccountLedger.CoinbaseAddress, minerA.Address, 50m, 0);
            var b2Peer = new Block(new BlockHeader(2, b1.Header.Hash, MerkleTree.ComputeRoot([cb2Peer]), 1), [cb2Peer]);
            b2Peer.Header.Mine(1);

            var cb3Peer = new Transaction(AccountLedger.CoinbaseAddress, minerA.Address, 50m, 0);
            var b3Peer = new Block(new BlockHeader(3, b2Peer.Header.Hash, MerkleTree.ComputeRoot([cb3Peer]), 1), [cb3Peer]);
            b3Peer.Header.Mine(1);

            var peerChain = new List<Block> { GenesisBlock.Instance, b1, b2Peer, b3Peer };

            // Resolve fork by adopting peerChain
            var (adopted, returnedCount, _) = await syncService.TryAdoptCandidateChainAsync(peerChain);

            Assert.True(adopted);
            Assert.Equal(1, returnedCount);
            Assert.Equal(1, mempool.Count);
            Assert.NotNull(mempool.GetById(orphanedTx.Id));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Test18_FullChainValidation_DetectsOverspendingInMinedBlock()
    {
        var alice = CryptoHelper.GenerateKeyPair();
        var bob = CryptoHelper.GenerateKeyPair();

        // Alice has 0 coins, yet a malicious miner mines a block where Alice sends 50 coins to Bob
        var overspendTx = new Transaction(alice.Address, bob.Address, 50m, nonce: 0, fee: 0m);
        overspendTx.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);

        var cb = new Transaction(AccountLedger.CoinbaseAddress, bob.Address, 50m, 0);
        var txs = new List<Transaction> { cb, overspendTx };
        var b1 = new Block(new BlockHeader(1, GenesisBlock.GenesisHash, MerkleTree.ComputeRoot(txs), 1), txs);
        b1.Header.Mine(1);

        var validator = new StructuralBlockchainValidator { BaseBlockReward = 50m };
        var (isValid, error) = validator.ValidateChain([GenesisBlock.Instance, b1]);

        Assert.False(isValid);
        Assert.Contains("overspent", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Test19_ValidateChainDetailed_DetectsExactFirstInvalidBlockIndex()
    {
        var alice = CryptoHelper.GenerateKeyPair();
        var bob = CryptoHelper.GenerateKeyPair();

        var cb1 = new Transaction(AccountLedger.CoinbaseAddress, alice.Address, 50m, 0);
        var b1 = new Block(new BlockHeader(1, GenesisBlock.GenesisHash, MerkleTree.ComputeRoot([cb1]), 1), [cb1]);
        b1.Header.Mine(1);

        var tx2 = new Transaction(alice.Address, bob.Address, 10m, nonce: 0, fee: 0m);
        tx2.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);
        var cb2 = new Transaction(AccountLedger.CoinbaseAddress, alice.Address, 50m, 0);
        var b2 = new Block(new BlockHeader(2, b1.Header.Hash, MerkleTree.ComputeRoot([cb2, tx2]), 1), [cb2, tx2]);
        b2.Header.Mine(1);

        var cb3 = new Transaction(AccountLedger.CoinbaseAddress, alice.Address, 50m, 0);
        var b3 = new Block(new BlockHeader(3, b2.Header.Hash, MerkleTree.ComputeRoot([cb3]), 1), [cb3]);
        b3.Header.Mine(1);

        var chain = new List<Block> { GenesisBlock.Instance, b1, b2, b3 };
        var validator = new StructuralBlockchainValidator { BaseBlockReward = 50m };

        // 1. Valid chain should return index -1
        var (isValid, firstInvalidIndex, error) = validator.ValidateChainDetailed(chain);
        Assert.True(isValid);
        Assert.Equal(-1, firstInvalidIndex);
        Assert.Null(error);

        // 2. Corrupting Block #2's transaction amount should report firstInvalidIndex = 2
        var corruptedChain = new List<Block> { GenesisBlock.Instance, b1, b2, b3 };
        var tamperedTx2 = new Transaction(alice.Address, bob.Address, 9999m, nonce: 0, fee: 0m); // tampered amount
        var corruptedB2 = new Block(b2.Header, [cb2, tamperedTx2]);
        corruptedChain[2] = corruptedB2;

        var (isCorruptedValid, corruptIndex, corruptError) = validator.ValidateChainDetailed(corruptedChain);
        Assert.False(isCorruptedValid);
        Assert.Equal(2, corruptIndex);
        Assert.NotNull(corruptError);

        // 3. Corrupting Genesis hash should report firstInvalidIndex = 0
        var tamperedGenesisHeader = new BlockHeader(0, GenesisBlock.GenesisPreviousHash, GenesisBlock.Instance.Header.MerkleRoot, DateTime.UtcNow, 0, 0, "000badhash");
        var tamperedGenesis = new Block(tamperedGenesisHeader, []);
        var genesisCorruptChain = new List<Block> { tamperedGenesis, b1, b2, b3 };

        var (isGenValid, genIndex, _) = validator.ValidateChainDetailed(genesisCorruptChain);
        Assert.False(isGenValid);
        Assert.Equal(0, genIndex);
    }

    [Fact]
    public async Task Test20_CorruptionRecovery_RollsBackToLastValidBlock_WhenNoPeersAvailable()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"blocks_rollback_test_{Guid.NewGuid():N}.json");
        try
        {
            var alice = CryptoHelper.GenerateKeyPair();
            var bob = CryptoHelper.GenerateKeyPair();

            // Block #1: Alice receives 50 coins coinbase
            var cb1 = new Transaction(AccountLedger.CoinbaseAddress, alice.Address, 50m, 0);
            var b1 = new Block(new BlockHeader(1, GenesisBlock.GenesisHash, MerkleTree.ComputeRoot([cb1]), 1), [cb1]);
            b1.Header.Mine(1);

            // Block #2: Alice sends 15 coins to Bob
            var userTx = new Transaction(alice.Address, bob.Address, 15m, nonce: 0, fee: 0m);
            userTx.Sign(alice.PrivateKeyHex, alice.PublicKeyHex);
            var cb2 = new Transaction(AccountLedger.CoinbaseAddress, alice.Address, 50m, 0);
            var b2 = new Block(new BlockHeader(2, b1.Header.Hash, MerkleTree.ComputeRoot([cb2, userTx]), 1), [cb2, userTx]);
            b2.Header.Mine(1);

            // Create initial valid 3-block chain
            var validChain = new List<Block> { GenesisBlock.Instance, b1, b2 };
            await JsonLinesHelper.OverwriteAllAsync(tempFile, validChain);

            // Now manually tamper with Block #2 in the file (modifying amount to 9999 without remining)
            var tamperedTx = new Transaction(alice.Address, bob.Address, 9999m, nonce: 0, fee: 0m) { Id = userTx.Id, Signature = userTx.Signature, PublicKey = userTx.PublicKey };
            var tamperedB2 = new Block(b2.Header, [cb2, tamperedTx]);
            var tamperedChain = new List<Block> { GenesisBlock.Instance, b1, tamperedB2 };
            await JsonLinesHelper.OverwriteAllAsync(tempFile, tamperedChain);

            var validator = new StructuralBlockchainValidator { BaseBlockReward = 50m };
            var controller = new MiningController(NullLogger<MiningController>.Instance);
            var mempool = new Mempool();
            var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
            var syncService = new ChainSynchronizationService(
                validator,
                controller,
                mempool,
                ledger,
                new DummyHttpClientFactory(),
                NullLogger<ChainSynchronizationService>.Instance,
                new MiningOptions { Difficulty = 1, BlockReward = 50m })
            {
                BlocksFilePath = tempFile
            };

            // Run synchronization with 0 peers available
            var syncResult = await syncService.InitialSyncAsync();

            Assert.True(syncResult.Success);
            Assert.True(syncResult.IsLocalChainValid);
            Assert.True(syncResult.ChainUpdated);
            Assert.Equal(2, syncResult.FinalHeight); // Rolled back from 3 blocks to 2 blocks (#0 and #1)

            // Verify local chain on disk is clean and contains exactly blocks #0 and #1
            var restoredChain = await syncService.GetLocalChainAsync();
            Assert.Equal(2, restoredChain.Count);
            Assert.Equal(1, restoredChain[^1].Header.Index);
            Assert.Equal(b1.Header.Hash, restoredChain[^1].Header.Hash);

            // Verify AccountLedger balances after rollback: Alice has 50 coins from Block #1, Bob has 0
            Assert.Equal(50m, ledger.GetConfirmedBalance(alice.Address));
            Assert.Equal(0m, ledger.GetConfirmedBalance(bob.Address));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Test21_CorruptionRecovery_SyncsFromPeer_WhenPeerIsAvailableWithValidChain()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"blocks_peer_rec_{Guid.NewGuid():N}.json");
        var nodesFile = Path.Combine(Path.GetTempPath(), $"nodes_test_{Guid.NewGuid():N}.json");
        var bannedFile = Path.Combine(Path.GetTempPath(), $"banned_test_{Guid.NewGuid():N}.json");

        var prevNodesPath = NodeManager.DefaultNodesFilePath;
        var prevBannedPath = NodeManager.DefaultBannedFilePath;

        try
        {
            NodeManager.DefaultNodesFilePath = nodesFile;
            NodeManager.DefaultBannedFilePath = bannedFile;
            await NodeManager.AddNodeAsync(new TrustedNode("peer.test", 8080, true));

            var alice = CryptoHelper.GenerateKeyPair();

            // Build clean valid peer chain with 3 blocks
            var cb1 = new Transaction(AccountLedger.CoinbaseAddress, alice.Address, 50m, 0);
            var b1 = new Block(new BlockHeader(1, GenesisBlock.GenesisHash, MerkleTree.ComputeRoot([cb1]), 1), [cb1]);
            b1.Header.Mine(1);

            var cb2 = new Transaction(AccountLedger.CoinbaseAddress, alice.Address, 50m, 0);
            var b2 = new Block(new BlockHeader(2, b1.Header.Hash, MerkleTree.ComputeRoot([cb2]), 1), [cb2]);
            b2.Header.Mine(1);

            var peerChain = new List<Block> { GenesisBlock.Instance, b1, b2 };

            // Local chain has corrupted Block #2
            var tamperedB2 = new Block(new BlockHeader(2, "000badprevioushash", MerkleTree.EmptyRoot, 1), []);
            var localCorruptChain = new List<Block> { GenesisBlock.Instance, b1, tamperedB2 };
            await JsonLinesHelper.OverwriteAllAsync(tempFile, localCorruptChain);

            // Mock HTTP Client Factory that returns peer status and peerChain
            var mockFactory = new MockHttpClientFactory(new CustomHttpMessageHandler(req =>
            {
                var uri = req.RequestUri?.ToString() ?? "";
                if (uri.Contains("/node/status"))
                {
                    var status = new NodeStatusDto("peer.test", 3, 0, 1, false, false);
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(status) };
                }
                if (uri.Contains("/blocks"))
                {
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = JsonContent.Create(peerChain) };
                }
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }));

            var validator = new StructuralBlockchainValidator { BaseBlockReward = 50m };
            var controller = new MiningController(NullLogger<MiningController>.Instance);
            var mempool = new Mempool();
            var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
            var syncService = new ChainSynchronizationService(
                validator,
                controller,
                mempool,
                ledger,
                mockFactory,
                NullLogger<ChainSynchronizationService>.Instance,
                new MiningOptions { Difficulty = 1, BlockReward = 50m })
            {
                BlocksFilePath = tempFile
            };

            var syncResult = await syncService.InitialSyncAsync();

            Assert.True(syncResult.Success);
            Assert.True(syncResult.IsLocalChainValid);
            Assert.True(syncResult.ChainUpdated);
            Assert.Equal(3, syncResult.FinalHeight);

            var finalLocalChain = await syncService.GetLocalChainAsync();
            Assert.Equal(3, finalLocalChain.Count);
            Assert.Equal(b2.Header.Hash, finalLocalChain[^1].Header.Hash);
            Assert.Equal(100m, ledger.GetConfirmedBalance(alice.Address));
        }
        finally
        {
            NodeManager.DefaultNodesFilePath = prevNodesPath;
            NodeManager.DefaultBannedFilePath = prevBannedPath;
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (File.Exists(nodesFile)) File.Delete(nodesFile);
            if (File.Exists(bannedFile)) File.Delete(bannedFile);
        }
    }

    [Fact]
    public async Task Test22_PreMiningCorruptionDetection_RollsBackAndContinuesMining()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"blocks_mining_rec_{Guid.NewGuid():N}.json");
        try
        {
            var miner = CryptoHelper.GenerateKeyPair();

            // Block #0 (Genesis) + Block #1 (valid)
            var cb1 = new Transaction(AccountLedger.CoinbaseAddress, miner.Address, 50m, 0);
            var b1 = new Block(new BlockHeader(1, GenesisBlock.GenesisHash, MerkleTree.ComputeRoot([cb1]), 1), [cb1]);
            b1.Header.Mine(1);

            // Corrupt Block #1's hash directly in storage
            var corruptedHeader = new BlockHeader(1, GenesisBlock.GenesisHash, b1.Header.MerkleRoot, DateTime.UtcNow, 0, 1, "000corruptedhash");
            var corruptedB1 = new Block(corruptedHeader, [cb1]);
            await JsonLinesHelper.OverwriteAllAsync(tempFile, [GenesisBlock.Instance, corruptedB1]);

            var validator = new StructuralBlockchainValidator { BaseBlockReward = 50m };
            var controller = new MiningController(NullLogger<MiningController>.Instance);
            var mempool = new Mempool();
            var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
            var syncService = new ChainSynchronizationService(
                validator,
                controller,
                mempool,
                ledger,
                new DummyHttpClientFactory(),
                NullLogger<ChainSynchronizationService>.Instance,
                new MiningOptions { Difficulty = 1, BlockReward = 50m, MinerAddress = miner.Address })
            {
                BlocksFilePath = tempFile
            };

            // Call InitialSyncAsync: it must detect corruption at Block #1, rollback to Block #0 (Genesis)
            var syncResult = await syncService.InitialSyncAsync();
            Assert.True(syncResult.Success);
            Assert.True(syncResult.IsLocalChainValid);
            Assert.Equal(1, syncResult.FinalHeight);

            // Next mined block cleanly succeeds Genesis as Block #1
            var minedBlock = await syncService.MineNextBlockAsync(miner.Address, 50m, 1);

            Assert.NotNull(minedBlock);
            Assert.Equal(1, minedBlock.Header.Index);
            Assert.Equal(GenesisBlock.GenesisHash, minedBlock.Header.PreviousHash);

            var chain = await syncService.GetLocalChainAsync();
            Assert.Equal(2, chain.Count);
            Assert.Equal(GenesisBlock.GenesisHash, chain[0].Header.Hash);
            Assert.Equal(minedBlock.Header.Hash, chain[1].Header.Hash);

            var (isValid, _) = validator.ValidateChain(chain);
            Assert.True(isValid);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Test23_SyntaxCorruptionRecovery_RollsBackToLastValidBlock()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"blocks_syntax_test_{Guid.NewGuid():N}.json");
        try
        {
            var miner = CryptoHelper.GenerateKeyPair();

            var cb1 = new Transaction(AccountLedger.CoinbaseAddress, miner.Address, 50m, 0);
            var b1 = new Block(new BlockHeader(1, GenesisBlock.GenesisHash, MerkleTree.ComputeRoot([cb1]), 1), [cb1]);
            b1.Header.Mine(1);

            // Write valid Genesis + Block #1
            await JsonLinesHelper.OverwriteAllAsync(tempFile, [GenesisBlock.Instance, b1]);

            // Append invalid JSON syntax at line 3
            await File.AppendAllTextAsync(tempFile, "{\"header\": { broken json syntax line without closing brace\n");

            var validator = new StructuralBlockchainValidator { BaseBlockReward = 50m };
            var controller = new MiningController(NullLogger<MiningController>.Instance);
            var mempool = new Mempool();
            var ledger = new AccountLedger(NullLogger<AccountLedger>.Instance);
            var syncService = new ChainSynchronizationService(
                validator,
                controller,
                mempool,
                ledger,
                new DummyHttpClientFactory(),
                NullLogger<ChainSynchronizationService>.Instance,
                new MiningOptions { Difficulty = 1, BlockReward = 50m })
            {
                BlocksFilePath = tempFile
            };

            // InitialSyncAsync should catch the syntax error on line 3, rollback to valid blocks #0 and #1, and overwrite cleanly
            var syncResult = await syncService.InitialSyncAsync();

            Assert.True(syncResult.Success);
            Assert.True(syncResult.IsLocalChainValid);
            Assert.Equal(2, syncResult.FinalHeight);

            // File on disk must now be completely readable without syntax errors
            var restoredChain = await syncService.GetLocalChainAsync();
            Assert.Equal(2, restoredChain.Count);
            Assert.Equal(b1.Header.Hash, restoredChain[^1].Header.Hash);

            // Mining next block succeeds cleanly on top of Block #1
            var nextBlock = await syncService.MineNextBlockAsync(miner.Address, 50m, 1);
            Assert.NotNull(nextBlock);
            Assert.Equal(2, nextBlock.Header.Index);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Test24_MerkleTree_BuildTreeLevels_MatchesComputeRootAndHierarchy()
    {
        // 1. Empty transactions list
        var emptyLevels = MerkleTree.BuildTreeLevels([]);
        Assert.Single(emptyLevels);
        Assert.Equal(MerkleTree.EmptyRoot, emptyLevels[0][0]);

        // 2. Single transaction
        var tx1 = new Transaction("0x0000000000000000000000000000000000000000", "0x1111111111111111111111111111111111111111", 50m, 0);
        var singleLevels = MerkleTree.BuildTreeLevels([tx1]);
        Assert.Single(singleLevels);
        Assert.Equal(tx1.CalculateHash().ToLowerInvariant(), singleLevels[0][0]);
        Assert.Equal(MerkleTree.ComputeRoot([tx1]), singleLevels[^1][0]);

        // 3. Three transactions (odd count requiring duplication)
        var tx2 = new Transaction("0x1111111111111111111111111111111111111111", "0x2222222222222222222222222222222222222222", 10m, 0, 1m);
        var tx3 = new Transaction("0x2222222222222222222222222222222222222222", "0x3333333333333333333333333333333333333333", 5m, 0, 0.5m);
        var threeTxs = new List<Transaction> { tx1, tx2, tx3 };

        var multiLevels = MerkleTree.BuildTreeLevels(threeTxs);
        // Level 0: 3 leaves
        // Level 1: 2 intermediate branch nodes (hash(0,1) and hash(2,2))
        // Level 2: 1 root node
        Assert.Equal(3, multiLevels.Count);
        Assert.Equal(3, multiLevels[0].Count);
        Assert.Equal(2, multiLevels[1].Count);
        Assert.Single(multiLevels[2]);

        var expectedRoot = MerkleTree.ComputeRoot(threeTxs);
        Assert.Equal(expectedRoot, multiLevels[^1][0]);
    }

    private class DummyHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private class MockHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public MockHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler);
    }

    private class CustomHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public CustomHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
