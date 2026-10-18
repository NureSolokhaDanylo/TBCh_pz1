# ==============================================================================
# Script to launch Node 3 in interactive mode
# Port: 8083 | Data: data/miner-3 | Web UI: http://localhost:8083/
# ==============================================================================

param(
    [switch]$Reset
)

$ErrorActionPreference = "Stop"
$rootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $rootDir

$port = 8083
$nodeDir = Join-Path $rootDir "data\miner-3"
$peers = @(8081, 8082)

if (-not (Test-Path $nodeDir)) {
    New-Item -ItemType Directory -Path $nodeDir -Force | Out-Null
}

if ($Reset) {
    $blocksFile = Join-Path $nodeDir "blocks.json"
    $logFile = Join-Path $nodeDir "miner.log"
    if (Test-Path $blocksFile) { Remove-Item $blocksFile -Force }
    if (Test-Path $logFile) { Remove-Item $logFile -Force }
}

$bannedFile = Join-Path $nodeDir "banned_nodes.json"
if (-not (Test-Path $bannedFile)) {
    [System.IO.File]::WriteAllBytes($bannedFile, @())
}

$nodesFile = Join-Path $nodeDir "nodes.json"
$peerLines = @()
foreach ($p in $peers) {
    $peerLines += "{`"host`":`"127.0.0.1`",`"port`":$p,`"isTrusted`":true}"
}
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($nodesFile, $peerLines, $utf8NoBom)

$exePath = Join-Path $rootDir "Miner\bin\Debug\net10.0\Miner.exe"
if (-not (Test-Path $exePath)) {
    Write-Host "Building Miner..." -ForegroundColor Yellow
    dotnet build Miner/Miner.csproj -v q --nologo
}

$host.UI.RawUI.WindowTitle = "Node 3 [Port 8083]"
Write-Host "Starting Node 3 on port $port (Web: http://localhost:$port/)..." -ForegroundColor Cyan
& $exePath --port $port --data-dir "$nodeDir"
