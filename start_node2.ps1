# ==============================================================================
# Script to launch Node 2 in interactive mode
# Port: 8082 | Data: data/miner-2 | Web UI: http://localhost:8082/
# ==============================================================================

param(
    [switch]$Reset
)

$ErrorActionPreference = "Stop"
$rootDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $rootDir

$port = 8082
$nodeDir = Join-Path $rootDir "data\miner-2"
$peers = @(8081, 8083)

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

$host.UI.RawUI.WindowTitle = "Node 2 [Port 8082]"
Write-Host "Starting Node 2 on port $port (Web: http://localhost:$port/)..." -ForegroundColor Cyan
& $exePath --port $port --data-dir "$nodeDir"
