# ==============================================================================
# Script to launch 3 independent Miner nodes in 3 separate interactive console windows
# ==============================================================================
# Node 1: Port 8081 | Data: data/miner-1 | Web UI: http://localhost:8081/
# Node 2: Port 8082 | Data: data/miner-2 | Web UI: http://localhost:8082/
# Node 3: Port 8083 | Data: data/miner-3 | Web UI: http://localhost:8083/
#
# Optional flag:
#   -Reset   Clears blocks.json on all 3 nodes so they start cleanly from Genesis
# ==============================================================================

param(
    [switch]$Reset
)

$ErrorActionPreference = "Stop"
$rootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $rootDir

Write-Host "==================================================================" -ForegroundColor Cyan
Write-Host " LAUNCHING 3 MINER NODES (INTERACTIVE CLI + WEB REST API)" -ForegroundColor Cyan
Write-Host "==================================================================" -ForegroundColor Cyan

# Stop any running Miner instances first so ports 8081..8083 are free
Get-Process -Name "Miner" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

# 1. Build project once
Write-Host "`n[1/3] Building Miner project..." -ForegroundColor Yellow
dotnet build Miner/Miner.csproj -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# 2. Ensure data directories exist and peers are pre-configured (UTF-8 without BOM)
Write-Host "[2/3] Configuring isolated data directories & P2P peers..." -ForegroundColor Yellow

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

$nodes = @(
    @{ Name = "Node 1"; Port = 8081; Dir = (Join-Path $rootDir "data\miner-1"); Peers = @(8082, 8083) },
    @{ Name = "Node 2"; Port = 8082; Dir = (Join-Path $rootDir "data\miner-2"); Peers = @(8081, 8083) },
    @{ Name = "Node 3"; Port = 8083; Dir = (Join-Path $rootDir "data\miner-3"); Peers = @(8081, 8082) }
)

foreach ($node in $nodes) {
    if (-not (Test-Path $node.Dir)) {
        New-Item -ItemType Directory -Path $node.Dir -Force | Out-Null
    }

    $blocksFile = Join-Path $node.Dir "blocks.json"
    $nodesFile = Join-Path $node.Dir "nodes.json"
    $bannedFile = Join-Path $node.Dir "banned_nodes.json"
    $logFile = Join-Path $node.Dir "miner.log"

    if ($Reset) {
        if (Test-Path $blocksFile) { Remove-Item $blocksFile -Force }
        if (Test-Path $logFile) { Remove-Item $logFile -Force }
    }

    # Always write clean 0-byte banned_nodes.json
    [System.IO.File]::WriteAllBytes($bannedFile, @())

    # Write trusted peers in nodes.json using UTF-8 without BOM
    $peerLines = @()
    foreach ($p in $node.Peers) {
        $peerLines += "{`"host`":`"127.0.0.1`",`"port`":$p,`"isTrusted`":true}"
    }
    [System.IO.File]::WriteAllLines($nodesFile, $peerLines, $utf8NoBom)
}

# 3. Launch 3 separate terminal windows
Write-Host "[3/3] Opening 3 interactive terminal windows..." -ForegroundColor Yellow

$exePath = Join-Path $rootDir "Miner\bin\Debug\net10.0\Miner.exe"

foreach ($node in $nodes) {
    $args = "--port $($node.Port) --data-dir `"$($node.Dir)`""
    Start-Process -FilePath "cmd.exe" -ArgumentList "/c title $($node.Name) [Port $($node.Port)] & `"$exePath`" $args"
    Start-Sleep -Milliseconds 400
}

Write-Host "`nAll 3 nodes launched successfully!" -ForegroundColor Green
Write-Host "------------------------------------------------------------------" -ForegroundColor DarkGray
Write-Host "Node 1: Console Title 'Node 1 [Port 8081]' | Web Dashboard: http://localhost:8081/" -ForegroundColor White
Write-Host "Node 2: Console Title 'Node 2 [Port 8082]' | Web Dashboard: http://localhost:8082/" -ForegroundColor White
Write-Host "Node 3: Console Title 'Node 3 [Port 8083]' | Web Dashboard: http://localhost:8083/" -ForegroundColor White
Write-Host "------------------------------------------------------------------" -ForegroundColor DarkGray
Write-Host "Tip: Run '.\start_3_nodes.ps1 -Reset' if you want to wipe old blocks and start from Genesis." -ForegroundColor Cyan
