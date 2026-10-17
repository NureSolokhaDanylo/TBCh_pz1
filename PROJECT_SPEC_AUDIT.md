# Project Specification & Audit (Blockchain Requirements)

This document tracks all project requirements and their verified implementation locations across the solution.

### Status Legend
- `[x] COMPLETED` – Fully implemented and verified by automated unit tests & CLI/REST commands.

---

## 1. Blockchain Core (Internal Logic)

### Wallet and Cryptography
- `[x] COMPLETED` **Keypair Generation**: Strictly ECDSA (`secp256k1`).
  - *Implementation*: `Miner/Core/Common/CryptoHelper.cs` (`ECCurve.CreateFromFriendlyName("secp256k1")`).
- `[x] COMPLETED` **Wallet Address Derivation**: Wallet address derived as a SHA-256 hash of the public key (`"0x" + SHA256(pubKeyBytes)[..20]`).
  - *Implementation*: `CryptoHelper.DeriveAddress(byte[] publicKeyBytes)`.
- `[x] COMPLETED` **Deterministic Byte Serialization**: Deterministic serialization of transaction and block header into bytes (`ToBytes()`), producing identical byte arrays across all nodes.
  - *Implementation*: `Transaction.ToBytes()` (`Miner/Core/Models/Transaction.cs`) and `BlockHeader.ToBytes()` (`Miner/Core/Models/BlockHeader.cs`).
- `[x] COMPLETED` **Digital Signatures**: Transaction signing with private key and verification with public key over deterministic payload bytes.
  - *Implementation*: `CryptoHelper.SignBytes`, `CryptoHelper.VerifySignatureBytes`, `Transaction.Sign`, and `Transaction.VerifySignature`.

### Transaction and Mempool
- `[x] COMPLETED` **Transaction Fields**: `Sender`, `Recipient`, `Amount`, `Fee`, `Nonce`, `Signature`.
  - *Implementation*: `Miner/Core/Models/Transaction.cs`.
- `[x] COMPLETED` **4 Mandatory Mempool Checks** (returning clear, human-readable rejection reasons):
  1. `[x] COMPLETED` **Check #1 — Valid digital signature**: Verified in `AccountLedger.ValidateTransaction` (`Check #1 failed (Invalid Signature)`).
  2. `[x] COMPLETED` **Check #2 — Sender balance sufficiency** (`Amount + Fee + pendingOutflow <= confirmedBalance`): Verified in `AccountLedger.ValidateTransaction` (`Check #2 failed (Insufficient Balance)`).
  3. `[x] COMPLETED` **Check #3 — Strict sequential Nonce** (`tx.Nonce == confirmedNonce + pendingSenderCount`): Verified in `AccountLedger.ValidateTransaction` (`Check #3 failed (Invalid Nonce)`).
  4. `[x] COMPLETED` **Check #4 — No duplicate transactions** in mempool or confirmed blockchain blocks: Verified in `Mempool.Add` and `AccountLedger.IsTransactionConfirmed` (`Check #4 failed (Duplicate Transaction)`).

### Block and Merkle Tree
- `[x] COMPLETED` **Block Structure**: Separation of block into `Header` (`Index`, `Timestamp`, `PreviousHash`, `MerkleRoot`, `Difficulty`, `Nonce`, `Hash`) and `Transactions` list.
  - *Implementation*: `BlockHeader.cs` and `Block.cs`.
- `[x] COMPLETED` **Header-Only Block Hashing**: Block hash computed as SHA-256 strictly over `Header.ToBytes()`.
  - *Implementation*: `BlockHeader.CalculateHash()`.
- `[x] COMPLETED` **Custom Merkle Tree with Odd-Leaf Duplication**: Custom Merkle tree calculation duplicating the last element on odd levels and recalculating each leaf hash directly from transaction payload bytes.
  - *Implementation*: `Miner/Core/Common/MerkleTree.cs` (`MerkleTree.ComputeRoot`).

### Mining (Proof of Work) and Difficulty Adjustment
- `[x] COMPLETED` **Coinbase Transaction**: First transaction in every mined block (`COINBASE`) rewards the miner with `BaseReward + sum(Fee)` of all included transactions.
  - *Implementation*: `ChainSynchronizationService.MineNextBlockAsync`.
- `[x] COMPLETED` **Proof-of-Work Nonce Iteration**: Nonce increment until block header hash starts with `Difficulty` leading zeroes.
  - *Implementation*: `BlockHeader.Mine(int difficulty, CancellationToken)`.
- `[x] COMPLETED` **Automatic Difficulty Adjustment**: Recalculates difficulty every $K=10$ blocks (`AdjustmentIntervalBlocks = 10`, `TargetBlockTimeSeconds = 10`) by comparing actual elapsed mining time against expected target time.
  - *Implementation*: `StructuralBlockchainValidator.CalculateNextDifficulty`.

### Blockchain, Persistence and Full Validation
- `[x] COMPLETED` **Genesis Block**: Single hardcoded Genesis block identical across all nodes.
  - *Implementation*: `GenesisBlock.Instance`.
- `[x] COMPLETED` **Address Balance Calculation**: Deterministic calculation of balance and nonce for any address across the chain.
  - *Implementation*: `AccountLedger.Rebuild` and `AccountLedger.GetAccount`.
- `[x] COMPLETED` **Chain Persistence**: Saved in `data/blocks.json` (JSON Lines) and automatically loaded and re-verified on node startup.
  - *Implementation*: `Program.cs` & `ChainSynchronizationService`.
- `[x] COMPLETED` **Full Chain Validation (`ValidateChain`)**: Comprehensive 7-point verification from Genesis to tip in `StructuralBlockchainValidator.ValidateChain`:
  1. `[x] COMPLETED` Block header hash integrity and PoW difficulty target satisfaction.
  2. `[x] COMPLETED` `PreviousHash` matches actual hash of block $i-1$.
  3. `[x] COMPLETED` `Header.MerkleRoot` matches recalculated `MerkleTree.ComputeRoot(block.Transactions)`.
  4. `[x] COMPLETED` All non-coinbase transaction ECDSA (`secp256k1`) signatures verified.
  5. `[x] COMPLETED` Sequential sender nonces without gaps or duplicates.
  6. `[x] COMPLETED` No sender overspent (`Amount + Fee <= balance`) at any historical block.
  7. `[x] COMPLETED` Miner Coinbase reward does not exceed `BaseReward + sum(Fee)`.

---

## 2. Management Commands (REST API & CLI — Step 7)

All 8 mandatory user commands are available in both the interactive Spectre.Console CLI (`ConsoleUiService`) and the REST API:
1. `[x] COMPLETED` **1. Create Wallet**: CLI Option `1` / `POST /wallets`
2. `[x] COMPLETED` **2. Send Transaction**: CLI Option `2` / `POST /transactions` & `POST /wallets/send`
3. `[x] COMPLETED` **3. Mine Block**: CLI Option `3` / `POST /mine`
4. `[x] COMPLETED` **4. Show Chain**: CLI Option `4` / `GET /blocks`
5. `[x] COMPLETED` **5. Show Address Balance**: CLI Option `5` / `GET /accounts/{address}`
6. `[x] COMPLETED` **6. Validate Chain**: CLI Option `6` / `GET /blocks/validate`
7. `[x] COMPLETED` **7. Manage Neighbor Peers (Add / Disconnect)**: CLI Option `7` / `GET|POST|DELETE /node/peers`
8. `[x] COMPLETED` **8. Synchronize with Network**: CLI Option `8` / `POST /consensus/resolve` (selects chain with highest cumulative PoW work $\sum 16^D$ and returns orphaned fork transactions back to Mempool).

---

## 3. Five Mandatory Attacks for Demonstration & Report Screenshots (Step 8)

Implemented in `Miner/Services/AttackDemonstrationService.cs` and runnable via CLI Option `9` or `dotnet run --project Miner -- --demo-attacks`:
- `[x] COMPLETED` **Attack 1: Tampering with amount in an old block transaction (without re-mining)** -> Blocked by `ValidateChain` (`Merkle root mismatch`).
- `[x] COMPLETED` **Attack 2: Tampering with old block data WITH Merkle root & PoW re-mining** -> Blocked by `ValidateChain` on the subsequent block (`Broken chain link: Block PreviousHash does not match previous block hash`).
- `[x] COMPLETED` **Attack 3: Transaction with forged/invalid signature or post-signing amount modification** -> Blocked by Mempool `Check #1 failed (Invalid Signature)`.
- `[x] COMPLETED` **Attack 4: Double Spend in Mempool** (two 100-coin transactions from a wallet with 100 coins) -> First accepted, second blocked by Mempool `Check #2 failed (Insufficient Balance)`.
- `[x] COMPLETED` **Attack 5: Replay Attack** (re-submitting an already confirmed transaction from a mined block) -> Blocked by Mempool `Check #3 & #4 failed (Replay / Duplicate)`.

---

## 4. Tests, Network, Experiment and Bonuses

### Automated Tests (Unit Tests)
- `[x] COMPLETED` **18 Green xUnit Tests in `Miner.Tests/BlockchainSpecificationTests.cs`** (`dotnet test Solution1.slnx`):
  - Merkle tree with even, odd (duplication), and empty transaction lists + tampering detection.
  - `secp256k1` key generation, `0x` address derivation, deterministic byte serialization, and ECDSA signing/verification.
  - All 4 mandatory Mempool checks.
  - All 5 mandatory security attacks.
  - Automatic difficulty retargeting (increase, decrease, hold steady).
  - Fork resolution by cumulative Proof-of-Work and return of orphaned transactions to Mempool.

### Live Network & Fork Demonstration
- `[x] COMPLETED` **3-Node Deployment & Automated Artificial Fork Script**:
  - `demo_fork.ps1` launches 3 full nodes (`8091`, `8092`, `8093`), demonstrates P2P propagation, isolates Node 1 to create an artificial fork, mines divergent branches, reconnects the nodes, verifies adoption of the highest cumulative PoW chain, and confirms that the orphaned transaction from Node 1's discarded branch returns to Mempool and gets mined into the canonical chain.
  - `docker-compose.yml` orchestrates 3 containerized nodes (`8081`, `8082`, `8083`).

### Experiment & Logarithmic Graph
- `[x] COMPLETED` **PoW Mining Benchmark & Logarithmic SVG Chart**:
  - Implemented in `Miner/Services/MiningBenchmarkService.cs` and runnable via CLI Option `11` or `dotnet run --project Miner -- --benchmark`.
  - Runs 10 trials for each difficulty level $D \in \{1, 2, 3, 4\}$ and exports `data/mining_benchmark.csv` and `data/mining_benchmark_log_chart.svg` plotting $\log_{10}(\text{Mean Time ms}) \pm \sigma$ vs. Difficulty $D$ alongside the theoretical $16^D$ slope.
