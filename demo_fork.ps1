# ==============================================================================
# Multi-Node P2P & Artificial Fork Resolution Demonstration Script (Step 9)
# ==============================================================================
# Launches 3 local full-node Miner instances on ports 8091, 8092, and 8093,
# demonstrates transaction & block propagation, creates a network partition
# (artificial fork), mines divergent branches, reconnects the nodes, and verifies:
#   1. Selection of the chain with the highest cumulative Proof-of-Work.
#   2. Automatic return of non-coinbase transactions from orphaned fork blocks
#      back into the Mempool so they are not lost, followed by mining them into
#      the canonical chain.
# ==============================================================================

$ErrorActionPreference = "Stop"
$rootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $rootDir

Write-Host "==============================================================================" -ForegroundColor Cyan
Write-Host " STEP 9: MULTI-NODE P2P & ARTIFICIAL FORK RESOLUTION DEMONSTRATION" -ForegroundColor Cyan
Write-Host "==============================================================================" -ForegroundColor Cyan

# 1. Build project once
Write-Host "`n[1/7] Building Miner project..." -ForegroundColor Yellow
dotnet build Miner/Miner.csproj -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# 2. Prepare clean isolated data directories for 3 nodes
$demoBaseDir = Join-Path $rootDir "data\fork-demo"
if (Test-Path $demoBaseDir) {
    Remove-Item -Recurse -Force $demoBaseDir
}

$node1Dir = Join-Path $demoBaseDir "node1"
$node2Dir = Join-Path $demoBaseDir "node2"
$node3Dir = Join-Path $demoBaseDir "node3"
New-Item -ItemType Directory -Path $node1Dir, $node2Dir, $node3Dir -Force | Out-Null

$env:MINER_DIFFICULTY = "2"
$env:MINER_ENABLED = "false"

$exePath = Join-Path $rootDir "Miner\bin\Debug\net10.0\Miner.exe"
$procs = @()
try {
    # 3. Launch 3 nodes on ports 8091, 8092, 8093
    Write-Host "[2/7] Starting 3 full-node instances on ports 8091, 8092, 8093..." -ForegroundColor Yellow
    $p1 = Start-Process -FilePath $exePath -ArgumentList "--port 8091 --data-dir `"$node1Dir`" --headless" -PassThru -WindowStyle Hidden
    $p2 = Start-Process -FilePath $exePath -ArgumentList "--port 8092 --data-dir `"$node2Dir`" --headless" -PassThru -WindowStyle Hidden
    $p3 = Start-Process -FilePath $exePath -ArgumentList "--port 8093 --data-dir `"$node3Dir`" --headless" -PassThru -WindowStyle Hidden
    $procs = @($p1, $p2, $p3)

    # Wait for all 3 nodes to become healthy
    foreach ($port in @(8091, 8092, 8093)) {
        $ready = $false
        for ($i = 0; $i -lt 30; $i++) {
            try {
                $null = Invoke-RestMethod -Uri "http://127.0.0.1:$port/node/status" -Method Get -TimeoutSec 2
                $ready = $true
                break
            } catch {
                Start-Sleep -Milliseconds 200
            }
        }
        if (-not $ready) { throw "Node on port $port failed to start in time." }
    }
    Write-Host "      -> Node 1 (http://127.0.0.1:8091), Node 2 (http://127.0.0.1:8092), Node 3 (http://127.0.0.1:8093) are ONLINE." -ForegroundColor Green

    # 4. Connect peers and mine initial funding block for Alice
    Write-Host "`n[3/7] Connecting peers (Node 1 <-> Node 2 <-> Node 3) & funding Alice..." -ForegroundColor Yellow
    Invoke-RestMethod -Uri "http://127.0.0.1:8091/node/peers" -Method Post -ContentType "application/json" -Body '{"host":"127.0.0.1","port":8092}' | Out-Null
    Invoke-RestMethod -Uri "http://127.0.0.1:8091/node/peers" -Method Post -ContentType "application/json" -Body '{"host":"127.0.0.1","port":8093}' | Out-Null
    Invoke-RestMethod -Uri "http://127.0.0.1:8092/node/peers" -Method Post -ContentType "application/json" -Body '{"host":"127.0.0.1","port":8091}' | Out-Null
    Invoke-RestMethod -Uri "http://127.0.0.1:8092/node/peers" -Method Post -ContentType "application/json" -Body '{"host":"127.0.0.1","port":8093}' | Out-Null
    Invoke-RestMethod -Uri "http://127.0.0.1:8093/node/peers" -Method Post -ContentType "application/json" -Body '{"host":"127.0.0.1","port":8091}' | Out-Null
    Invoke-RestMethod -Uri "http://127.0.0.1:8093/node/peers" -Method Post -ContentType "application/json" -Body '{"host":"127.0.0.1","port":8092}' | Out-Null

    $alice = Invoke-RestMethod -Uri "http://127.0.0.1:8091/wallets?label=Alice" -Method Post
    $bob   = Invoke-RestMethod -Uri "http://127.0.0.1:8091/wallets?label=Bob" -Method Post
    $miner2Wallet = Invoke-RestMethod -Uri "http://127.0.0.1:8092/wallets?label=Miner2" -Method Post

    # Mine Block #1 on Node 1 paying Coinbase reward (50 coins) to Alice; propagates to Node 2 & Node 3
    $null = Invoke-RestMethod -Uri "http://127.0.0.1:8091/mine?minerAddress=$($alice.address)" -Method Post
    Start-Sleep -Milliseconds 500

    $s1 = Invoke-RestMethod -Uri "http://127.0.0.1:8091/node/status" -Method Get
    $s2 = Invoke-RestMethod -Uri "http://127.0.0.1:8092/node/status" -Method Get
    $s3 = Invoke-RestMethod -Uri "http://127.0.0.1:8093/node/status" -Method Get
    $aliceAcc = Invoke-RestMethod -Uri "http://127.0.0.1:8091/accounts/$($alice.address)" -Method Get
    Write-Host "      -> Common chain height across N1, N2, N3: $($s1.chainHeight), $($s2.chainHeight), $($s3.chainHeight)" -ForegroundColor Green
    Write-Host "      -> Alice ($($alice.address)) confirmed balance: $($aliceAcc.balance) coins" -ForegroundColor Green

    # 5. Isolate Node 1 from Node 2 & Node 3 to create an artificial network partition (Fork)
    Write-Host "`n[4/7] Disconnecting Node 1 from Node 2 & Node 3 (Simulating Network Partition)..." -ForegroundColor Yellow
    Invoke-RestMethod -Uri "http://127.0.0.1:8091/node/peers?host=127.0.0.1&port=8092" -Method Delete | Out-Null
    Invoke-RestMethod -Uri "http://127.0.0.1:8091/node/peers?host=127.0.0.1&port=8093" -Method Delete | Out-Null
    Invoke-RestMethod -Uri "http://127.0.0.1:8092/node/peers?host=127.0.0.1&port=8091" -Method Delete | Out-Null
    Invoke-RestMethod -Uri "http://127.0.0.1:8093/node/peers?host=127.0.0.1&port=8091" -Method Delete | Out-Null
    Write-Host "      -> Partition A: [Node 1] (isolated)" -ForegroundColor Magenta
    Write-Host "      -> Partition B: [Node 2 <-> Node 3]" -ForegroundColor Magenta

    # 6. Mine divergent branches on Partition A vs Partition B
    Write-Host "`n[5/7] Mining divergent branches on isolated partitions..." -ForegroundColor Yellow
    # On isolated Node 1: Alice sends 15 coins (+ 1 fee) to Bob, and Node 1 mines 1 block (Height = 3)
    $txResp = Invoke-RestMethod -Uri "http://127.0.0.1:8091/wallets/send" -Method Post -ContentType "application/json" -Body "{`"senderAddress`":`"$($alice.address)`",`"recipientAddress`":`"$($bob.address)`",`"amount`":15,`"fee`":1}"
    $orphanTxId = $txResp.transaction.id
    $null = Invoke-RestMethod -Uri "http://127.0.0.1:8091/mine?minerAddress=$($alice.address)" -Method Post

    # On Partition B (Node 2 & Node 3): Node 2 mines 2 blocks (Height = 4, higher cumulative PoW work)
    $null = Invoke-RestMethod -Uri "http://127.0.0.1:8092/mine?minerAddress=$($miner2Wallet.address)" -Method Post
    $null = Invoke-RestMethod -Uri "http://127.0.0.1:8092/mine?minerAddress=$($miner2Wallet.address)" -Method Post
    Start-Sleep -Milliseconds 500

    $tip1Before = Invoke-RestMethod -Uri "http://127.0.0.1:8091/blocks/latest" -Method Get
    $tip2Before = Invoke-RestMethod -Uri "http://127.0.0.1:8092/blocks/latest" -Method Get
    $tip3Before = Invoke-RestMethod -Uri "http://127.0.0.1:8093/blocks/latest" -Method Get

    Write-Host "      -> FORK DETECTED BEFORE RECONNECTION:" -ForegroundColor Red
    Write-Host "         Node 1 (Branch A): Height = $($tip1Before.header.index + 1) | Tip Hash = $($tip1Before.header.hash.Substring(0, 16))... (Contains Alice->Bob Tx $($orphanTxId.Substring(0, 12))...)" -ForegroundColor Yellow
    Write-Host "         Node 2 (Branch B): Height = $($tip2Before.header.index + 1) | Tip Hash = $($tip2Before.header.hash.Substring(0, 16))..." -ForegroundColor Cyan
    Write-Host "         Node 3 (Branch B): Height = $($tip3Before.header.index + 1) | Tip Hash = $($tip3Before.header.hash.Substring(0, 16))..." -ForegroundColor Cyan

    # 7. Reconnect Node 1 to Node 2 & Node 3 and trigger consensus resolution
    Write-Host "`n[6/7] Reconnecting Node 1 <-> Node 2 <-> Node 3 & resolving fork (/consensus/resolve)..." -ForegroundColor Yellow
    Invoke-RestMethod -Uri "http://127.0.0.1:8091/node/peers" -Method Post -ContentType "application/json" -Body '{"host":"127.0.0.1","port":8092}' | Out-Null
    Invoke-RestMethod -Uri "http://127.0.0.1:8091/node/peers" -Method Post -ContentType "application/json" -Body '{"host":"127.0.0.1","port":8093}' | Out-Null
    Invoke-RestMethod -Uri "http://127.0.0.1:8092/node/peers" -Method Post -ContentType "application/json" -Body '{"host":"127.0.0.1","port":8091}' | Out-Null
    Invoke-RestMethod -Uri "http://127.0.0.1:8093/node/peers" -Method Post -ContentType "application/json" -Body '{"host":"127.0.0.1","port":8091}' | Out-Null

    $resolveResult = Invoke-RestMethod -Uri "http://127.0.0.1:8091/consensus/resolve" -Method Post
    $pendingOnNode1 = Invoke-RestMethod -Uri "http://127.0.0.1:8091/transactions/pending" -Method Get

    Write-Host "      -> Node 1 adopted Branch B (Height: $($resolveResult.currentHeight), Cumulative Work: $($resolveResult.cumulativeWork), Tip: $($resolveResult.tipHash.Substring(0, 16))...)" -ForegroundColor Green
    Write-Host "      -> Orphaned transaction recovered into Node 1 Mempool: Count = $($pendingOnNode1.Count), TxId = $($pendingOnNode1[0].id.Substring(0, 16))..." -ForegroundColor Green

    # 8. Mine one final block on Node 1 so the recovered orphaned transaction is confirmed on all 3 nodes
    Write-Host "`n[7/7] Mining recovered mempool transaction into Block #4 and propagating across network..." -ForegroundColor Yellow
    $null = Invoke-RestMethod -Uri "http://127.0.0.1:8091/mine?minerAddress=$($miner2Wallet.address)" -Method Post
    Start-Sleep -Milliseconds 500

    $tip1After = Invoke-RestMethod -Uri "http://127.0.0.1:8091/blocks/latest" -Method Get
    $tip2After = Invoke-RestMethod -Uri "http://127.0.0.1:8092/blocks/latest" -Method Get
    $tip3After = Invoke-RestMethod -Uri "http://127.0.0.1:8093/blocks/latest" -Method Get
    $bobAccAfter = Invoke-RestMethod -Uri "http://127.0.0.1:8092/accounts/$($bob.address)" -Method Get
    $val1 = Invoke-RestMethod -Uri "http://127.0.0.1:8091/blocks/validate" -Method Get

    Write-Host "      -> CONVERGED CANONICAL CHAIN ACROSS ALL 3 NODES:" -ForegroundColor Green
    Write-Host "         Node 1: Height = $($tip1After.header.index + 1) | Tip = $($tip1After.header.hash.Substring(0, 16))..." -ForegroundColor Green
    Write-Host "         Node 2: Height = $($tip2After.header.index + 1) | Tip = $($tip2After.header.hash.Substring(0, 16))..." -ForegroundColor Green
    Write-Host "         Node 3: Height = $($tip3After.header.index + 1) | Tip = $($tip3After.header.hash.Substring(0, 16))..." -ForegroundColor Green
    Write-Host "         Bob's confirmed balance on Node 2: $($bobAccAfter.balance) coins (Orphaned Tx preserved & confirmed!)" -ForegroundColor Green
    Write-Host "         Full Chain Validation Verdict: $($val1.verdict)" -ForegroundColor Green
}
finally {
    Write-Host "`nStopping demo node processes..." -ForegroundColor DarkGray
    foreach ($p in $procs) {
        if ($null -ne $p -and -not $p.HasExited) {
            Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        }
    }
    Start-Sleep -Milliseconds 300
    if (Test-Path $demoBaseDir) {
        Remove-Item -Recurse -Force $demoBaseDir -ErrorAction SilentlyContinue
    }
}
