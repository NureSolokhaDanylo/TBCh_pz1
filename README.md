# Educational Proof-of-Work Blockchain & Full-Node Wallet (.NET 10)

A complete, zero-trust Proof-of-Work blockchain node, ECDSA (`secp256k1`) wallet, P2P synchronization engine, security attack simulator, and mining benchmark suite written in C# (.NET 10).

---

## Quick Start (1–2 Commands)

### 1. Run the Interactive Full-Node CLI (All 8 Management Commands + Demos)
```powershell
dotnet run --project Miner
```
Provides an interactive **Spectre.Console** menu with:
1. **Create Wallet** (`secp256k1` ECDSA keypair + SHA-256 `0x` address)
2. **Send Transaction** (4-check mempool validation + P2P broadcast)
3. **Mine Block** (Collects mempool + Coinbase `BaseReward + sum(Fee)`, PoW search, saves to `blocks.json`, broadcasts to peers)
4. **Show Chain** (Displays all blocks, headers, Merkle roots, and transactions)
5. **Show Address Balance** (Calculates confirmed balance, pending mempool delta, and sequential nonce)
6. **Validate Chain** (Full 7-point cryptographic verification from Genesis to tip)
7. **Manage Neighbor Peers** (Add or disconnect P2P neighbor nodes)
8. **Synchronize with Network** (Cumulative PoW fork resolution + returns orphaned transactions to mempool)
9. **Run 5 Security Attacks Demo** (Step 8 automated verification with clear rejection verdicts)
10. **View Mempool & Node Logs**
11. **Run PoW Mining Benchmark** (4 difficulties $\times$ 10 runs + logarithmic SVG chart)

---

### 2. Run the Automated Unit Test Suite (18 xUnit Tests)
```powershell
dotnet test Solution1.slnx
```

---

### 3. Run the 5 Mandatory Security Attacks Demonstration (Headless CLI)
```powershell
dotnet run --project Miner -- --demo-attacks
```
Executes and prints formatted rejection verdicts for:
1. **Attack 1**: Tampering with transaction amount in an historical block without re-mining (`MerkleRoot` mismatch).
2. **Attack 2**: Tampering with an historical block + recomputing `MerkleRoot` and re-mining PoW (`PreviousHash` link broken in next block).
3. **Attack 3**: Forged digital signature / post-signing amount modification (`Check #1 failed: Invalid Signature`).
4. **Attack 4**: Double-spend attempt in Mempool (`Check #2 failed: Insufficient Balance` accounting for pending mempool outflow).
5. **Attack 5**: Replay attack of an already confirmed transaction (`Check #3 & #4 failed: Replay / Duplicate`).

---

### 4. Run the 3-Node Launch & Artificial Fork Resolution Demo
```powershell
powershell -ExecutionPolicy Bypass -File .\demo_fork.ps1
```
Or launch 3 containerized nodes via Docker Compose:
```powershell
docker compose up --build
```
The `demo_fork.ps1` script automatically:
1. Launches 3 local nodes on ports `8091`, `8092`, and `8093`.
2. Connects them, mines an initial block, and verifies transaction/block propagation.
3. Disconnects Node 1 from Node 2 & Node 3 to create a network partition (artificial fork).
4. Mines a 1-block branch on Node 1 containing a transaction (`Alice -> Bob`), while mining a 2-block branch with higher cumulative PoW work on Node 2 & Node 3.
5. Reconnects the nodes, resolves the fork toward the highest cumulative work branch, verifies that the orphaned transaction from Node 1's discarded branch is automatically returned to the Mempool, and mines it into the canonical chain.

---

### 5. Run the PoW Mining Benchmark & Generate Logarithmic Chart
```powershell
dotnet run --project Miner -- --benchmark
```
Runs 10 trials at each difficulty level $D \in \{1, 2, 3, 4\}$ and exports:
- `data/mining_benchmark.csv`
- `data/mining_benchmark_log_chart.svg` (Logarithmic scale $\log_{10}(\text{Time})$ vs. Difficulty $D$ showing theoretical $16^D$ linearity)
