using System.Net.Mime;
using System.Text;
using Blockchain.Shared.Common;
using Blockchain.Shared.Models;
using Blockchain.Shared.Services;
using Miner.Common;
using Miner.Services;

namespace Miner.Endpoints;

/// <summary>
/// Read-only web dashboard and explorer endpoints for observing the live blockchain,
/// pending transaction mempool, mining node status, and consensus metrics in a web browser.
/// Strictly observational: contains zero mutation or wallet execution controls.
/// </summary>
public static class ExplorerEndpoints
{
    public static IEndpointRouteBuilder MapExplorerEndpoints(this IEndpointRouteBuilder routes)
    {
        // 1. Interactive read-only HTML dashboard accessible via browser at /, /explorer, /dashboard
        routes.MapGet("/", RenderDashboardAsync);
        routes.MapGet("/explorer", RenderDashboardAsync);
        routes.MapGet("/dashboard", RenderDashboardAsync);

        // 2. Programmatic read-only JSON representation of the entire node state
        routes.MapGet("/explorer/json", async (
            ChainSynchronizationService syncService,
            Mempool mempool,
            MiningOptions options,
            MiningController miningController) =>
        {
            var chain = await syncService.GetLocalChainAsync();
            var nextDiff = await syncService.GetNextDifficultyAsync();
            var cumulativeWork = StructuralBlockchainValidator.CalculateCumulativeWork(chain).ToString();
            var pendingTxs = mempool.GetAll();

            return Results.Ok(new
            {
                node = new
                {
                    machineName = Environment.MachineName,
                    serverTimeUtc = DateTime.UtcNow,
                    state = miningController.State.ToString(),
                    isMining = miningController.State == MiningState.Mining,
                    isSyncing = miningController.IsSyncing,
                    autoMiningEnabled = options.Enabled
                },
                mining = new
                {
                    activeMiningWallet = options.MinerAddress,
                    blockReward = options.BlockReward,
                    currentDifficulty = nextDiff,
                    adjustmentIntervalBlocks = options.AdjustmentIntervalBlocks,
                    targetBlockTimeSeconds = options.TargetBlockTimeSeconds
                },
                chain = new
                {
                    height = chain.Count,
                    cumulativeWork,
                    tipHash = chain[^1].Header.Hash,
                    blocks = chain.OrderByDescending(b => b.Header.Index).Select(b => new
                    {
                        b.Header,
                        b.Transactions,
                        merkleTreeLevels = MerkleTree.BuildTreeLevels(b.Transactions)
                    }).ToList()
                },
                mempool = new
                {
                    count = pendingTxs.Count,
                    transactions = pendingTxs
                }
            });
        });

        return routes;
    }

    private static async Task<IResult> RenderDashboardAsync(
        ChainSynchronizationService syncService,
        Mempool mempool,
        MiningOptions options,
        MiningController miningController)
    {
        var chain = await syncService.GetLocalChainAsync();
        var nextDiff = await syncService.GetNextDifficultyAsync();
        var cumulativeWork = StructuralBlockchainValidator.CalculateCumulativeWork(chain).ToString();
        var pendingTxs = mempool.GetAll();
        var reversedBlocks = chain.OrderByDescending(b => b.Header.Index).ToList();

        var stateBadgeColor = miningController.State switch
        {
            MiningState.Mining => "#10b981", // green
            MiningState.Syncing => "#f59e0b", // amber
            MiningState.Paused => "#ef4444",  // red
            _ => "#6b7280"                   // grey
        };

        var html = new StringBuilder();
        html.Append($$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <meta http-equiv="refresh" content="3">
            <title>Blockchain Full-Node Explorer</title>
            <style>
                :root {
                    --bg: #0f172a;
                    --card-bg: #1e293b;
                    --border: #334155;
                    --text: #f8fafc;
                    --text-muted: #94a3b8;
                    --accent: #38bdf8;
                    --green: #10b981;
                    --yellow: #f59e0b;
                    --purple: #a855f7;
                }
                * { box-sizing: border-box; margin: 0; padding: 0; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, monospace; }
                body { background-color: var(--bg); color: var(--text); padding: 24px; line-height: 1.5; }
                .container { max-width: 1300px; margin: 0 auto; }
                header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 24px; padding-bottom: 16px; border-bottom: 1px solid var(--border); }
                h1 { font-size: 1.5rem; color: var(--accent); display: flex; align-items: center; gap: 8px; }
                .refresh-badge { font-size: 0.8rem; background: #0284c7; color: white; padding: 4px 10px; border-radius: 9999px; }
                .stats-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(220px, 1fr)); gap: 16px; margin-bottom: 28px; }
                .card { background: var(--card-bg); border: 1px solid var(--border); border-radius: 10px; padding: 16px; }
                .card-title { font-size: 0.8rem; text-transform: uppercase; color: var(--text-muted); margin-bottom: 6px; letter-spacing: 0.05em; }
                .card-value { font-size: 1.4rem; font-weight: bold; }
                .status-badge { display: inline-flex; align-items: center; gap: 6px; font-weight: bold; font-size: 0.95rem; }
                .dot { width: 10px; height: 10px; border-radius: 50%; background-color: {{stateBadgeColor}}; display: inline-block; }
                .mono { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; }
                .hash { font-size: 0.85rem; color: var(--accent); word-break: break-all; }
                .addr { color: #34d399; font-weight: 500; }
                h2 { font-size: 1.25rem; margin-bottom: 14px; display: flex; align-items: center; gap: 8px; color: #e2e8f0; }
                .section { margin-bottom: 32px; }
                table { width: 100%; border-collapse: collapse; text-align: left; font-size: 0.88rem; }
                th, td { padding: 10px 12px; border-bottom: 1px solid var(--border); }
                th { background: #0f172a; color: var(--text-muted); font-size: 0.78rem; text-transform: uppercase; }
                tr:hover { background-color: rgba(255,255,255,0.02); }
                .empty-msg { text-align: center; color: var(--text-muted); padding: 24px; font-style: italic; }
                .block-card { background: var(--card-bg); border: 1px solid var(--border); border-radius: 8px; margin-bottom: 14px; padding: 16px; }
                .block-header-row { display: flex; justify-content: space-between; align-items: center; margin-bottom: 10px; flex-wrap: wrap; gap: 8px; }
                .block-title { font-weight: bold; font-size: 1.1rem; color: #fbbf24; }
                .badge { font-size: 0.75rem; padding: 2px 8px; border-radius: 4px; background: #334155; }
                .badge-coinbase { background: #065f46; color: #a7f3d0; }
                .notice-box { font-size: 0.8rem; color: var(--text-muted); margin-top: 16px; text-align: center; padding: 12px; border-top: 1px dashed var(--border); }
                .merkle-details { margin-top: 14px; background: #0b1120; border: 1px solid var(--border); border-radius: 8px; overflow: hidden; }
                .merkle-summary { display: flex; justify-content: space-between; align-items: center; padding: 10px 14px; cursor: pointer; font-size: 0.86rem; font-weight: 600; color: #e2e8f0; background: #162032; user-select: none; }
                .merkle-summary:hover { background: #1e2d44; }
                .merkle-diagram { padding: 16px; display: flex; flex-direction: column; align-items: center; gap: 12px; overflow-x: auto; }
                .merkle-level { display: flex; justify-content: center; gap: 14px; width: 100%; flex-wrap: wrap; }
                .merkle-level-label { width: 100%; text-align: center; font-size: 0.72rem; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: -4px; }
                .merkle-node { background: var(--card-bg); border: 1px solid var(--border); border-radius: 6px; padding: 8px 12px; display: flex; flex-direction: column; align-items: center; min-width: 170px; max-width: 280px; text-align: center; box-shadow: 0 2px 4px rgba(0,0,0,0.25); transition: transform 0.15s, border-color 0.15s; }
                .merkle-node:hover { transform: translateY(-2px); border-color: var(--accent); }
                .merkle-node.node-root { border-color: #a855f7; background: #241242; }
                .merkle-node.node-branch { border-color: #0284c7; background: #0c2b45; }
                .merkle-node.node-leaf { border-color: #059669; background: #064032; }
                .merkle-node.node-coinbase { border-color: #10b981; background: #064e3b; }
                .merkle-node-badge { font-size: 0.68rem; font-weight: bold; padding: 2px 6px; border-radius: 3px; margin-bottom: 4px; text-transform: uppercase; background: rgba(255,255,255,0.15); }
                .merkle-node-hash { font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace; font-size: 0.75rem; word-break: break-all; color: #f1f5f9; }
                .merkle-node-meta { font-size: 0.72rem; color: #94a3b8; margin-top: 3px; }
                .merkle-arrow { color: #64748b; font-size: 1rem; user-select: none; line-height: 1; }
            </style>
        </head>
        <body>
            <div class="container">
                <header>
                    <div>
                        <h1>⛓️ Blockchain Full-Node Explorer</h1>
                        <p style="color: var(--text-muted); font-size: 0.85rem; margin-top: 4px;">
                            Read-only live inspection of local blockchain, mempool & mining metrics
                        </p>
                    </div>
                    <div>
                        <span class="refresh-badge">Live: auto-refreshing 3s</span>
                    </div>
                </header>

                <div class="stats-grid">
                    <div class="card">
                        <div class="card-title">Mining Node Status</div>
                        <div class="card-value status-badge">
                            <span class="dot"></span>
                            {{miningController.State.ToString().ToUpperInvariant()}}
                        </div>
                        <div style="font-size: 0.8rem; color: var(--text-muted); margin-top: 4px;">
                            Auto-Mining: {{(options.Enabled ? "Enabled" : "Disabled")}}
                        </div>
                    </div>
                    <div class="card">
                        <div class="card-title">Chain Height</div>
                        <div class="card-value" style="color: var(--accent);">{{chain.Count}} blocks</div>
                        <div style="font-size: 0.8rem; color: var(--text-muted); margin-top: 4px;">
                            Tip: #{{chain[^1].Header.Index}}
                        </div>
                    </div>
                    <div class="card">
                        <div class="card-title">Proof-of-Work Target</div>
                        <div class="card-value" style="color: var(--yellow);">Difficulty: {{nextDiff}}</div>
                        <div style="font-size: 0.8rem; color: var(--text-muted); margin-top: 4px;">
                            Leading zeroes: {{new string('0', nextDiff)}}
                        </div>
                    </div>
                    <div class="card">
                        <div class="card-title">Cumulative Work (&Sigma; 16<sup>D</sup>)</div>
                        <div class="card-value" style="color: var(--purple); font-size: 1.25rem;">{{cumulativeWork}}</div>
                        <div style="font-size: 0.8rem; color: var(--text-muted); margin-top: 4px;">
                            Fork choice weight
                        </div>
                    </div>
                    <div class="card">
                        <div class="card-title">Pending Mempool</div>
                        <div class="card-value" style="color: {{(pendingTxs.Count > 0 ? "var(--green)" : "var(--text-muted)")}};">
                            {{pendingTxs.Count}} txs
                        </div>
                        <div style="font-size: 0.8rem; color: var(--text-muted); margin-top: 4px;">
                            Awaiting next block
                        </div>
                    </div>
                    <div class="card">
                        <div class="card-title">Active Mining Wallet</div>
                        <div class="mono addr" style="font-size: 0.88rem; word-break: break-all; margin-top: 4px;">
                            {{options.MinerAddress}}
                        </div>
                        <div style="font-size: 0.8rem; color: var(--text-muted); margin-top: 4px;">
                            Coinbase reward: {{options.BlockReward:F2}} coins
                        </div>
                    </div>
                </div>

                <!-- SECTION 1: MEMPOOL -->
                <div class="section">
                    <h2>📬 Pending Mempool Transactions ({{pendingTxs.Count}})</h2>
                    <div class="card" style="padding: 0; overflow-x: auto;">
        """);

        if (pendingTxs.Count == 0)
        {
            html.Append("""
                        <div class="empty-msg">
                            ✓ Mempool is currently empty. All submitted transactions have been mined into blocks.
                        </div>
            """);
        }
        else
        {
            html.Append("""
                        <table>
                            <thead>
                                <tr>
                                    <th>Transaction ID</th>
                                    <th>Sender</th>
                                    <th>Recipient</th>
                                    <th>Amount</th>
                                    <th>Fee</th>
                                    <th>Nonce</th>
                                    <th>Submitted (UTC)</th>
                                </tr>
                            </thead>
                            <tbody>
            """);

            foreach (var tx in pendingTxs)
            {
                html.Append($$"""
                                <tr>
                                    <td class="mono hash">{{tx.Id}}</td>
                                    <td class="mono addr">{{tx.Sender}}</td>
                                    <td class="mono addr">{{tx.Recipient}}</td>
                                    <td style="color: #34d399; font-weight: bold;">{{tx.Amount:F2}}</td>
                                    <td style="color: #fbbf24;">{{tx.Fee:F2}}</td>
                                    <td>{{tx.Nonce}}</td>
                                    <td style="color: var(--text-muted);">{{tx.Timestamp:HH:mm:ss.fff}}</td>
                                </tr>
                """);
            }

            html.Append("""
                            </tbody>
                        </table>
            """);
        }

        html.Append("""
                    </div>
                </div>

                <!-- SECTION 2: BLOCKCHAIN -->
                <div class="section">
                    <h2>📦 Confirmed Blockchain Blocks (Latest to Genesis)</h2>
        """);

        foreach (var block in reversedBlocks)
        {
            var h = block.Header;
            var isGenesis = h.Index == 0;
            html.Append($$"""
                    <div class="block-card">
                        <div class="block-header-row">
                            <span class="block-title">Block #{{h.Index}} {{(isGenesis ? "<span class=\"badge\" style=\"background:#1d4ed8;\">GENESIS</span>" : "")}}</span>
                            <span style="color: var(--text-muted); font-size: 0.85rem;">Mined at: {{h.Timestamp:yyyy-MM-dd HH:mm:ss}} UTC</span>
                        </div>
                        <div style="font-size: 0.84rem; margin-bottom: 12px; display: grid; grid-template-columns: repeat(auto-fit, minmax(280px, 1fr)); gap: 8px;">
                            <div><span style="color: var(--text-muted);">Hash:</span> <span class="mono hash">{{h.Hash}}</span></div>
                            <div><span style="color: var(--text-muted);">PrevHash:</span> <span class="mono hash">{{(string.IsNullOrEmpty(h.PreviousHash) ? "0000000000000000000000000000000000000000000000000000000000000000" : h.PreviousHash)}}</span></div>
                            <div><span style="color: var(--text-muted);">MerkleRoot:</span> <span class="mono hash">{{h.MerkleRoot}}</span></div>
                            <div><span style="color: var(--text-muted);">PoW Nonce:</span> <span class="mono" style="color:#fbbf24; font-weight:bold;">{{h.Nonce}}</span> | <span style="color: var(--text-muted);">Difficulty:</span> <span class="badge">{{h.Difficulty}}</span></div>
                        </div>

                        <div style="margin-top: 10px; border-top: 1px solid var(--border); padding-top: 10px;">
                            <div style="font-size: 0.82rem; font-weight: bold; color: var(--text-muted); margin-bottom: 6px;">
                                Included Transactions ({{block.Transactions.Count}}):
                            </div>
                            <table style="font-size: 0.8rem;">
                                <thead>
                                    <tr>
                                        <th>Type</th>
                                        <th>Transaction ID</th>
                                        <th>Sender</th>
                                        <th>Recipient</th>
                                        <th>Amount</th>
                                        <th>Fee</th>
                                        <th>Nonce</th>
                                    </tr>
                                </thead>
                                <tbody>
            """);

            foreach (var tx in block.Transactions)
            {
                var isCoinbase = AccountLedger.IsSystemOrCoinbase(tx.Sender);
                html.Append($$"""
                                    <tr>
                                        <td><span class="badge {{(isCoinbase ? "badge-coinbase" : "")}}">{{(isCoinbase ? "COINBASE" : "TRANSFER")}}</span></td>
                                        <td class="mono hash" style="font-size: 0.76rem;">{{tx.Id}}</td>
                                        <td class="mono addr" style="font-size: 0.78rem;">{{(isCoinbase ? "System (Coinbase)" : tx.Sender)}}</td>
                                        <td class="mono addr" style="font-size: 0.78rem;">{{tx.Recipient}}</td>
                                        <td style="color: #34d399; font-weight: bold;">{{tx.Amount:F2}}</td>
                                        <td style="color: #fbbf24;">{{tx.Fee:F2}}</td>
                                        <td>{{(isCoinbase ? "-" : tx.Nonce.ToString())}}</td>
                                    </tr>
                """);
            }

            html.Append("""
                                </tbody>
                            </table>
                        </div>
            """);

            html.Append(RenderMerkleTreeHtml(block));

            html.Append("""
                    </div>
            """);
        }

        html.Append("""
                </div>

                <div class="notice-box">
                    ℹ️ Read-only observation dashboard. No wallet private keys or mutation functions are exposed here.
                </div>
            </div>
        </body>
        </html>
        """);

        return Results.Content(html.ToString(), MediaTypeNames.Text.Html, Encoding.UTF8);
    }

    private static string RenderMerkleTreeHtml(Block block)
    {
        var treeLevels = MerkleTree.BuildTreeLevels(block.Transactions);
        var sb = new StringBuilder();

        sb.Append($$"""
                        <!-- MERKLE TREE VISUALIZATION -->
                        <details class="merkle-details" open>
                            <summary class="merkle-summary">
                                <span style="display: flex; align-items: center; gap: 8px;">
                                    <span>🌳 Merkle Tree Structure</span>
                                    <span class="badge" style="background: #581c87; color: #f3e8ff;">{{treeLevels.Count}} {{(treeLevels.Count == 1 ? "Level" : "Levels")}}</span>
                                    <span class="badge" style="background: #0f766e; color: #ccfbf1;">{{block.Transactions.Count}} {{(block.Transactions.Count == 1 ? "Tx" : "Txs")}}</span>
                                </span>
                                <span class="mono hash" style="font-size: 0.75rem; color: #c084fc;">Root: {{(block.Header.MerkleRoot.Length > 16 ? block.Header.MerkleRoot[..8] + "..." + block.Header.MerkleRoot[^8..] : block.Header.MerkleRoot)}}</span>
                            </summary>
                            <div class="merkle-diagram">
        """);

        if (block.Transactions.Count == 0)
        {
            sb.Append($$"""
                                <div class="merkle-level">
                                    <div class="merkle-node node-root" title="{{block.Header.MerkleRoot}}">
                                        <span class="merkle-node-badge" style="background: #7e22ce;">GENESIS ROOT</span>
                                        <span class="mono merkle-node-hash">{{block.Header.MerkleRoot}}</span>
                                        <span class="merkle-node-meta">0 transactions (Empty Merkle Root)</span>
                                    </div>
                                </div>
            """);
        }
        else
        {
            // Render top-down: from Root (treeLevels.Count - 1) down to Leaves (0)
            for (int lvl = treeLevels.Count - 1; lvl >= 0; lvl--)
            {
                var nodes = treeLevels[lvl];
                var isRootLevel = (lvl == treeLevels.Count - 1);
                var isLeafLevel = (lvl == 0);

                var levelTitle = isRootLevel
                    ? (isLeafLevel ? "👑 Root & Single Leaf (Level 0)" : $"👑 Merkle Root (Level {lvl})")
                    : (isLeafLevel ? "🍃 Leaf Hashes / Transactions (Level 0)" : $"🌿 Intermediate Branch Nodes (Level {lvl})");

                sb.Append($$"""
                                <div class="merkle-level-label">{{levelTitle}}</div>
                                <div class="merkle-level">
                """);

                for (int i = 0; i < nodes.Count; i++)
                {
                    var hash = nodes[i];
                    var shortHash = hash.Length > 16 ? $"{hash[..8]}...{hash[^6..]}" : hash;

                    if (isRootLevel && !isLeafLevel)
                    {
                        sb.Append($$"""
                                    <div class="merkle-node node-root" title="Merkle Root: {{hash}}">
                                        <span class="merkle-node-badge" style="background: #7e22ce;">👑 MERKLE ROOT</span>
                                        <span class="mono merkle-node-hash">{{shortHash}}</span>
                                        <span class="merkle-node-meta">Combined hash of Level {{lvl - 1}} children</span>
                                    </div>
                        """);
                    }
                    else if (isLeafLevel)
                    {
                        var isDuplicatedOdd = i >= block.Transactions.Count;
                        var tx = isDuplicatedOdd ? block.Transactions[^1] : block.Transactions[i];
                        var isCoinbase = AccountLedger.IsSystemOrCoinbase(tx.Sender);

                        var badgeColor = isDuplicatedOdd ? "#b45309" : (isCoinbase ? "#059669" : "#0284c7");
                        var badgeText = isDuplicatedOdd
                            ? $"TX #{i} (DUPLICATE ODD)"
                            : (isCoinbase ? $"TX #{i} (COINBASE)" : $"TX #{i} (TRANSFER)");

                        var metaText = isCoinbase
                            ? $"Mint +{tx.Amount:F2} coins"
                            : $"{tx.Amount:F2} coins (fee {tx.Fee:F2})";

                        sb.Append($$"""
                                    <div class="merkle-node {{(isCoinbase ? "node-coinbase" : "node-leaf")}}" title="Transaction ID: {{hash}}">
                                        <span class="merkle-node-badge" style="background: {{badgeColor}};">{{badgeText}}</span>
                                        <span class="mono merkle-node-hash">{{shortHash}}</span>
                                        <span class="merkle-node-meta">{{metaText}}</span>
                                    </div>
                        """);
                    }
                    else
                    {
                        sb.Append($$"""
                                    <div class="merkle-node node-branch" title="Intermediate Hash: {{hash}}">
                                        <span class="merkle-node-badge" style="background: #0369a1;">🌿 BRANCH [{{i}}]</span>
                                        <span class="mono merkle-node-hash">{{shortHash}}</span>
                                        <span class="merkle-node-meta">SHA-256(Left + Right)</span>
                                    </div>
                        """);
                    }
                }

                sb.Append("</div>");

                if (lvl > 0)
                {
                    sb.Append("""<div class="merkle-arrow">▼</div>""");
                }
            }
        }

        sb.Append("""
                            </div>
                        </details>
        """);

        return sb.ToString();
    }
}
