using Blockchain.Shared.Common;
using Blockchain.Shared.Models;
using Blockchain.Shared.Services;
using Miner.Common;
using Miner.Logging;
using Miner.Services;
using Miner.Wallet;
using Spectre.Console;

namespace Miner.UI;

/// <summary>
/// Interactive terminal user interface powered by Spectre.Console.
/// Implements the 8 core blockchain management commands, the 5 mandatory attack demonstrations,
/// and live log/mempool inspection.
/// </summary>
public class ConsoleUiService
{
    private readonly WalletService _walletService;
    private readonly ChainSynchronizationService _syncService;
    private readonly AccountLedger _accountLedger;
    private readonly IBlockchainValidator _validator;
    private readonly Mempool _mempool;
    private readonly MiningController _miningController;
    private readonly MiningOptions _miningOptions;
    private readonly AttackDemonstrationService _attackDemoService;
    private readonly MiningBenchmarkService _benchmarkService;
    private readonly InMemoryLogBuffer _logBuffer;

    public ConsoleUiService(
        WalletService walletService,
        ChainSynchronizationService syncService,
        AccountLedger accountLedger,
        IBlockchainValidator validator,
        Mempool mempool,
        MiningController miningController,
        MiningOptions miningOptions,
        AttackDemonstrationService attackDemoService,
        MiningBenchmarkService benchmarkService,
        InMemoryLogBuffer logBuffer)
    {
        _walletService = walletService;
        _syncService = syncService;
        _accountLedger = accountLedger;
        _validator = validator;
        _mempool = mempool;
        _miningController = miningController;
        _miningOptions = miningOptions;
        _attackDemoService = attackDemoService;
        _benchmarkService = benchmarkService;
        _logBuffer = logBuffer;
    }

    /// <summary>
    /// Starts the interactive console loop until the user chooses to exit or cancellation is requested.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                AnsiConsole.Clear();
                await RenderHeaderAsync(cancellationToken);

                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[yellow]Select a command:[/]")
                        .PageSize(14)
                        .AddChoices([
                            "1. ➕ Create Wallet (Generate secp256k1 Keys & Address)",
                            "2. 💸 Send Transaction (Validate 4 Checks, Mempool & Broadcast)",
                            "3. ⛏️ Change Mining Wallet (Reassign Coinbase Reward Recipient)",
                            "4. 📦 Show Blockchain (Display All Blocks & Transactions)",
                            "5. 💰 Show Address Balance (Local Wallets & Any Address Lookup)",
                            "6. ✅ Validate Blockchain (Full 7-Point Chain Verification)",
                            "7. 🌐 Manage Neighbor Peers (Add / Disconnect Peer)",
                            "8. 🔄 Synchronize with Network (Max Work Chain & Orphaned Tx Return)",
                            "9. 🛡️ Run 5 Security Attacks Demonstration (Step 8 Report)",
                            "10. 📜 View Mempool & Node Logs",
                            "11. 📊 Run PoW Mining Benchmark & Log-Scale Plot (16^D Experiment)",
                            "0. 🚪 Exit Node"
                        ]));

                AnsiConsole.WriteLine();

                if (choice.StartsWith("1.")) await CreateWalletCommandAsync(cancellationToken);
                else if (choice.StartsWith("2.")) await SendTransactionCommandAsync(cancellationToken);
                else if (choice.StartsWith("3.")) await ChangeMiningWalletCommandAsync(cancellationToken);
                else if (choice.StartsWith("4.")) await ShowBlockchainCommandAsync(cancellationToken);
                else if (choice.StartsWith("5.")) await ShowBalanceCommandAsync(cancellationToken);
                else if (choice.StartsWith("6.")) await ValidateBlockchainCommandAsync(cancellationToken);
                else if (choice.StartsWith("7.")) await ManagePeersCommandAsync(cancellationToken);
                else if (choice.StartsWith("8.")) await SynchronizeNetworkCommandAsync(cancellationToken);
                else if (choice.StartsWith("9.")) RenderAttacksDemo(pauseAfter: true);
                else if (choice.StartsWith("10.")) await ShowMempoolAndLogsAsync(cancellationToken);
                else if (choice.StartsWith("11.")) RunMiningBenchmark(pauseAfter: true);
                else if (choice.StartsWith("0."))
                {
                    if (AnsiConsole.Confirm("Are you sure you want to stop the node and exit?"))
                    {
                        AnsiConsole.MarkupLine("[bold red]Shutting down node... Goodbye![/]");
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[bold red]Error: {Markup.Escape(ex.Message)}[/]");
                Pause();
            }
        }
    }

    private async Task RenderHeaderAsync(CancellationToken cancellationToken)
    {
        var chain = await _syncService.GetLocalChainAsync(cancellationToken);
        var tip = chain[^1];
        var nextDiff = await _syncService.GetNextDifficultyAsync(cancellationToken);
        var work = StructuralBlockchainValidator.CalculateCumulativeWork(chain);
        var latestLog = _logBuffer.GetLatest();

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("[bold cyan]FULL-NODE BLOCKCHAIN & WALLET (secp256k1 / PoW)[/]").Centered());

        var minerSnippet = string.IsNullOrEmpty(_miningOptions.MinerAddress) ? "None" : _miningOptions.MinerAddress[..Math.Min(10, _miningOptions.MinerAddress.Length)] + "...";
        var statusLine = $"Chain Height: [bold white]{chain.Count}[/] | Tip: [grey]{Markup.Escape(tip.Header.Hash[..12])}...[/] | Diff: [bold yellow]{nextDiff}[/] | Mempool: [bold green]{_mempool.Count}[/] | Mining -> [cyan]{minerSnippet}[/]";
        table.AddRow(new Markup(statusLine));

        if (latestLog != null)
        {
            var levelColor = latestLog.Level switch
            {
                LogLevel.Error or LogLevel.Critical => "red",
                LogLevel.Warning => "yellow",
                LogLevel.Information => "green",
                _ => "grey"
            };
            var logSnippet = $"[grey]{latestLog.Timestamp:HH:mm:ss}[/] [{levelColor}][[{latestLog.Level}]][/] [dim]{Markup.Escape(latestLog.Message)}[/]";
            table.AddRow(new Markup(logSnippet));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Command 1: Create Wallet (outputs secp256k1 public/private keys and derived address).
    /// </summary>
    private async Task CreateWalletCommandAsync(CancellationToken cancellationToken)
    {
        var label = AnsiConsole.Ask<string>("Enter wallet label (e.g. 'Main', 'Miner'):", "Main");
        var wallet = await _walletService.CreateWalletAsync(label.Trim(), cancellationToken);

        var panel = new Panel(
            new Markup(
                $"[bold green]Wallet successfully created (ECDSA secp256k1)![/]\n\n" +
                $"[bold]Label:[/]             {Markup.Escape(wallet.Label)}\n" +
                $"[bold]Address:[/]           [cyan]{Markup.Escape(wallet.Address)}[/]\n" +
                $"[bold]Public Key (Hex):[/]  [grey]{Markup.Escape(wallet.PublicKey)}[/]\n" +
                $"[bold]Private Key (Hex):[/] [red]{Markup.Escape(wallet.PrivateKey)}[/]\n" +
                $"[bold]Initial Nonce:[/]     {wallet.Nonce}\n" +
                $"[bold]Saved to:[/]          {Markup.Escape(_walletService.WalletsFilePath)}"
            )
        )
        {
            Header = new PanelHeader("[bold green]Command 1: New Wallet Created[/]"),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);
        Pause();
    }

    /// <summary>
    /// Command 2: Send Transaction (validates 4 mempool checks, adds to mempool, broadcasts to peers).
    /// </summary>
    private async Task SendTransactionCommandAsync(CancellationToken cancellationToken)
    {
        var wallets = await _walletService.GetWalletsAsync(cancellationToken);
        if (wallets.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No local wallets available. Please create a wallet first (Command 1).[/]");
            Pause();
            return;
        }

        var senderChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select [bold]sender wallet[/]:")
                .PageSize(10)
                .AddChoices(wallets.Select(w => $"[[{Markup.Escape(w.Label)}]] {w.Address} | Balance: {w.Balance:F2} | Nonce: {w.Nonce}"))
        );

        var selectedIndex = wallets.FindIndex(w => senderChoice.Contains(w.Address));
        var senderWallet = wallets[selectedIndex];

        var recipient = AnsiConsole.Ask<string>("Enter [bold]recipient address[/] (0x...):");
        var amount = AnsiConsole.Ask<decimal>("Enter [bold]transfer amount[/]:");
        var fee = AnsiConsole.Ask<decimal>("Enter [bold]miner fee[/]:", 1.0m);

        var (success, tx, message) = await _walletService.CreateAndSendTransactionAsync(
            senderWallet, recipient, amount, fee, cancellationToken);

        if (success && tx != null)
        {
            var panel = new Panel(
                new Markup(
                    $"[bold green]Transaction passed all 4 Mempool checks and was broadcasted![/]\n\n" +
                    $"[bold]Tx ID (Hash):[/]  [cyan]{Markup.Escape(tx.Id)}[/]\n" +
                    $"[bold]Sender:[/]        {Markup.Escape(tx.Sender)}\n" +
                    $"[bold]Recipient:[/]     {Markup.Escape(tx.Recipient)}\n" +
                    $"[bold]Amount:[/]        [bold yellow]{tx.Amount:F2}[/]\n" +
                    $"[bold]Fee:[/]           [bold yellow]{tx.Fee:F2}[/]\n" +
                    $"[bold]Tx Nonce:[/]      {tx.Nonce}\n" +
                    $"[bold]Signature:[/]     [dim]{Markup.Escape(tx.Signature != null && tx.Signature.Length > 24 ? tx.Signature[..24] + "..." : tx.Signature ?? "N/A")}[/]\n" +
                    $"[bold]Status:[/]        Added to Mempool & broadcasted to peers"
                )
            )
            {
                Header = new PanelHeader("[bold green]Command 2: Transaction Accepted[/]"),
                Border = BoxBorder.Rounded
            };
            AnsiConsole.Write(panel);
        }
        else
        {
            var errPanel = new Panel(new Markup($"[bold red]{Markup.Escape(message)}[/]"))
            {
                Header = new PanelHeader("[bold red]Command 2: Transaction Rejected by Mempool[/]"),
                Border = BoxBorder.Rounded
            };
            AnsiConsole.Write(errPanel);
        }

        Pause();
    }

    /// <summary>
    /// Command 3: Configure / Change Active Mining Wallet.
    /// Reassigns the address that automatically receives Coinbase mining rewards in background.
    /// </summary>
    private async Task ChangeMiningWalletCommandAsync(CancellationToken cancellationToken)
    {
        var wallets = await _walletService.GetWalletsAsync(cancellationToken);
        string currentMiner = _miningOptions.MinerAddress;

        AnsiConsole.MarkupLine($"[bold yellow]Current active mining address:[/] [bold cyan]{Markup.Escape(currentMiner)}[/]\n");

        string newAddress;
        if (wallets.Count > 0)
        {
            var choices = wallets.Select(w => $"[[{Markup.Escape(w.Label)}]] {w.Address} (Bal: {w.Balance:F2})").ToList();
            choices.Add("Enter Custom Address...");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select wallet to receive all upcoming Coinbase block mining rewards:")
                    .AddChoices(choices));

            if (choice == "Enter Custom Address...")
            {
                newAddress = AnsiConsole.Ask<string>("Enter custom target address (0x...):").Trim();
            }
            else
            {
                var idx = wallets.FindIndex(w => choice.Contains(w.Address));
                newAddress = wallets[idx].Address;
            }
        }
        else
        {
            newAddress = AnsiConsole.Ask<string>("Enter custom miner address (0x...):", currentMiner).Trim();
        }

        if (newAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && newAddress.Length == 42)
        {
            _miningOptions.MinerAddress = newAddress;
            AnsiConsole.MarkupLine($"\n[bold green]Active mining wallet successfully updated to:[/] [cyan]{Markup.Escape(newAddress)}[/]");
            AnsiConsole.MarkupLine("[grey]Continuous background auto-mining will credit all subsequent block rewards directly to this address.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[bold red]Invalid Ethereum-style address format (must start with '0x' and contain 40 hex chars).[/]");
        }

        Pause();
    }

    /// <summary>
    /// Command 4: Show Blockchain (outputs all blocks and their transaction contents).
    /// </summary>
    private async Task ShowBlockchainCommandAsync(CancellationToken cancellationToken)
    {
        var chain = await _syncService.GetLocalChainAsync(cancellationToken);
        var totalWork = StructuralBlockchainValidator.CalculateCumulativeWork(chain);

        AnsiConsole.MarkupLine($"[bold cyan]=== BLOCKCHAIN CONTENTS ({chain.Count} blocks, Cumulative Work: {totalWork}) ===[/]\n");

        foreach (var block in chain)
        {
            var h = block.Header;
            var blockTable = new Table().Border(TableBorder.Rounded).Expand();
            blockTable.AddColumn($"[bold yellow]Block #{h.Index}[/]");
            blockTable.AddColumn("[bold white]Details[/]");

            blockTable.AddRow("Hash", $"[cyan]{Markup.Escape(h.Hash)}[/]");
            blockTable.AddRow("PreviousHash", $"[grey]{Markup.Escape(h.PreviousHash)}[/]");
            blockTable.AddRow("MerkleRoot", $"[grey]{Markup.Escape(h.MerkleRoot)}[/]");
            blockTable.AddRow("Timestamp (UTC)", $"{h.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            blockTable.AddRow("Difficulty / Nonce", $"Difficulty: [bold]{h.Difficulty}[/] | PoW Nonce: [bold]{h.Nonce}[/]");
            blockTable.AddRow("Transactions Count", block.Transactions.Count.ToString());

            for (int i = 0; i < block.Transactions.Count; i++)
            {
                var tx = block.Transactions[i];
                var isCoinbase = AccountLedger.IsSystemOrCoinbase(tx.Sender);
                var txLabel = isCoinbase ? $"Tx #{i} (COINBASE)" : $"Tx #{i} (Nonce {tx.Nonce})";
                var txDesc = isCoinbase
                    ? $"[green]+{tx.Amount:F2}[/] -> [cyan]{Markup.Escape(tx.Recipient)}[/] | ID: [dim]{Markup.Escape(tx.Id[..Math.Min(16, tx.Id.Length)])}...[/]"
                    : $"[yellow]{Markup.Escape(tx.Sender[..Math.Min(12, tx.Sender.Length)])}...[/] -> [cyan]{Markup.Escape(tx.Recipient[..Math.Min(12, tx.Recipient.Length)])}...[/] | Amt: [bold]{tx.Amount:F2}[/] | Fee: {tx.Fee:F2} | ID: [dim]{Markup.Escape(tx.Id[..Math.Min(14, tx.Id.Length)])}...[/]";

                blockTable.AddRow(new Text(txLabel), new Markup(txDesc));
            }

            AnsiConsole.Write(blockTable);
        }

        Pause();
    }

    /// <summary>
    /// Command 5: Show Address Balance (displays local wallets and allows querying any address across the chain).
    /// </summary>
    private async Task ShowBalanceCommandAsync(CancellationToken cancellationToken)
    {
        var wallets = await _walletService.GetWalletsAsync(cancellationToken);
        var chain = await _syncService.GetLocalChainAsync(cancellationToken);

        if (wallets.Count > 0)
        {
            var table = new Table().Border(TableBorder.Rounded).Title("[bold cyan]Local Wallets[/]");
            table.AddColumn("#");
            table.AddColumn("Label");
            table.AddColumn("Address");
            table.AddColumn(new TableColumn("Confirmed Balance").RightAligned());
            table.AddColumn(new TableColumn("Account Nonce").RightAligned());

            for (int i = 0; i < wallets.Count; i++)
            {
                var w = wallets[i];
                table.AddRow(
                    (i + 1).ToString(),
                    Markup.Escape(w.Label),
                    $"[cyan]{Markup.Escape(w.Address)}[/]",
                    $"[bold green]{w.Balance:F2}[/]",
                    w.Nonce.ToString()
                );
            }
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
        }

        var allAccounts = _accountLedger.GetAllAccounts();
        if (allAccounts.Count > 0)
        {
            var ledgerTable = new Table().Border(TableBorder.Simple).Title("[bold yellow]All Active Addresses on Chain[/]");
            ledgerTable.AddColumn("Address");
            ledgerTable.AddColumn(new TableColumn("Balance").RightAligned());
            ledgerTable.AddColumn(new TableColumn("Nonce").RightAligned());

            foreach (var acc in allAccounts)
            {
                ledgerTable.AddRow($"[cyan]{Markup.Escape(acc.Address)}[/]", $"[bold green]{acc.Balance:F2}[/]", acc.Nonce.ToString());
            }
            AnsiConsole.Write(ledgerTable);
            AnsiConsole.WriteLine();
        }

        var customAddress = AnsiConsole.Ask<string>("Enter an address to query across the chain (or press Enter to skip):", string.Empty);
        if (!string.IsNullOrWhiteSpace(customAddress))
        {
            var state = AccountLedger.CalculateAccountStateFromChain(chain, customAddress);
            var panel = new Panel(
                new Markup(
                    $"[bold]Address:[/]           [cyan]{Markup.Escape(state.Address)}[/]\n" +
                    $"[bold]Confirmed Balance:[/] [bold green]{state.Balance:F2}[/]\n" +
                    $"[bold]Next Nonce:[/]        {state.Nonce}"
                )
            )
            {
                Header = new PanelHeader("[bold cyan]Command 5: Address Balance Result[/]"),
                Border = BoxBorder.Rounded
            };
            AnsiConsole.Write(panel);
        }

        Pause();
    }

    /// <summary>
    /// Command 6: Validate Blockchain (runs full 7-point verification from Genesis and outputs verdict).
    /// </summary>
    private async Task ValidateBlockchainCommandAsync(CancellationToken cancellationToken)
    {
        var chain = await _syncService.GetLocalChainAsync(cancellationToken);
        var (isValid, errorMessage) = _validator.ValidateChain(chain);
        var work = StructuralBlockchainValidator.CalculateCumulativeWork(chain);

        if (isValid)
        {
            var panel = new Panel(
                new Markup(
                    $"[bold green]VERDICT: OK — Entire blockchain is 100% valid![/]\n\n" +
                    $"[bold]Verified Blocks:[/]   {chain.Count} (Block #0 .. Block #{chain[^1].Header.Index})\n" +
                    $"[bold]Cumulative Work:[/]   {work}\n" +
                    $"[bold]Checks Passed:[/]\n" +
                    $"  [green]✓[/] Header SHA-256 hashes & PoW difficulty prefixes\n" +
                    $"  [green]✓[/] PreviousHash cryptographic links\n" +
                    $"  [green]✓[/] Merkle tree roots for all blocks\n" +
                    $"  [green]✓[/] ECDSA (secp256k1) signatures for all transactions\n" +
                    $"  [green]✓[/] Sequential sender nonces without gaps or replays\n" +
                    $"  [green]✓[/] Historical account balances (no overspending)\n" +
                    $"  [green]✓[/] Coinbase miner rewards (base + transaction fees)"
                )
            )
            {
                Header = new PanelHeader("[bold green]Command 6: Blockchain Validation Passed[/]"),
                Border = BoxBorder.Rounded
            };
            AnsiConsole.Write(panel);
        }
        else
        {
            var panel = new Panel(
                new Markup(
                    $"[bold red]VERDICT: INVALID BLOCKCHAIN DETECTED![/]\n\n" +
                    $"[bold]Reason:[/] [red]{Markup.Escape(errorMessage ?? "Unknown validation failure")}[/]\n\n" +
                    $"[yellow]Tip: You can recover the chain now (sync from peers or rollback to last valid block).[/]"
                )
            )
            {
                Header = new PanelHeader("[bold red]Command 6: Blockchain Validation Failed[/]"),
                Border = BoxBorder.Heavy
            };
            AnsiConsole.Write(panel);

            if (AnsiConsole.Confirm("Would you like to recover the blockchain now (query peers or rollback to last valid block)?"))
            {
                SyncResult? recoveryResult = null;
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync("Recovering blockchain (querying peers or rolling back to valid block)...", async _ =>
                    {
                        recoveryResult = await _syncService.InitialSyncAsync(cancellationToken);
                    });

                if (recoveryResult != null && recoveryResult.Success)
                {
                    AnsiConsole.MarkupLine($"[bold green]✓ Blockchain recovered:[/] {Markup.Escape(recoveryResult.Message)}");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[bold red]✗ Recovery failed:[/] {Markup.Escape(recoveryResult?.ErrorMessage ?? "Unknown error")}");
                }
            }
        }

        Pause();
    }

    /// <summary>
    /// Command 7: Manage Neighbor Peers (add peer or remove/disconnect peer for fork demo).
    /// </summary>
    private async Task ManagePeersCommandAsync(CancellationToken cancellationToken)
    {
        var trustedPeers = await NodeManager.GetTrustedNodesAsync(cancellationToken: cancellationToken);
        var bannedPeers = await NodeManager.GetBannedNodesAsync(cancellationToken: cancellationToken);

        var peersTable = new Table().Border(TableBorder.Rounded).Title("[bold cyan]Configured Network Peers[/]");
        peersTable.AddColumn("Peer Address");
        peersTable.AddColumn("Status");
        peersTable.AddColumn("Details");

        foreach (var p in trustedPeers)
        {
            peersTable.AddRow($"{Markup.Escape(p.Host)}:{p.Port}", "[green]Active / Connected[/]", $"Last seen: {p.LastSeen:HH:mm:ss}");
        }

        foreach (var b in bannedPeers)
        {
            peersTable.AddRow($"{Markup.Escape(b.Host)}:{b.Port}", "[red]Banned[/]", Markup.Escape(b.Reason ?? "Invalid block/chain"));
        }

        if (trustedPeers.Count == 0 && bannedPeers.Count == 0)
        {
            peersTable.AddRow("[grey]None[/]", "[grey]-[/]", "Node is currently isolated (no active peers)");
        }

        AnsiConsole.Write(peersTable);
        AnsiConsole.WriteLine();

        var action = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select peer action:")
                .AddChoices([
                    "1. Add Neighbor Peer",
                    "2. Disconnect / Remove Neighbor Peer (Isolate for Fork Demo)",
                    "3. Unban Peer",
                    "0. Back to Main Menu"
                ]));

        if (action.StartsWith("1."))
        {
            var hostInput = AnsiConsole.Ask<string>("Enter peer host or URL (e.g. 'localhost:8082' or 'http://localhost:8082'):");
            if (TryParseHostPort(hostInput, out var host, out var port))
            {
                var added = await NodeManager.AddNodeAsync(new TrustedNode(host, port, true), cancellationToken: cancellationToken);
                if (added)
                    AnsiConsole.MarkupLine($"[bold green]Peer {Markup.Escape(host)}:{port} registered in {Markup.Escape(NodeManager.DefaultNodesFilePath)}.[/]");
                else
                    AnsiConsole.MarkupLine($"[yellow]Peer {Markup.Escape(host)}:{port} already exists or is banned.[/]");
            }
            else
            {
                var portNum = AnsiConsole.Ask<int>("Enter peer port (e.g. 8082):", 8082);
                var added = await NodeManager.AddNodeAsync(new TrustedNode(hostInput.Trim(), portNum, true), cancellationToken: cancellationToken);
                if (added)
                    AnsiConsole.MarkupLine($"[bold green]Peer {Markup.Escape(hostInput.Trim())}:{portNum} registered.[/]");
                else
                    AnsiConsole.MarkupLine($"[yellow]Peer already exists or is banned.[/]");
            }
            Pause();
        }
        else if (action.StartsWith("2."))
        {
            if (trustedPeers.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No active peers to remove.[/]");
                Pause();
                return;
            }

            var peerChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select peer to disconnect/remove:")
                    .AddChoices(trustedPeers.Select(p => $"{p.Host}:{p.Port}")));

            if (TryParseHostPort(peerChoice, out var host, out var port))
            {
                await NodeManager.RemoveNodeAsync(host, port, cancellationToken: cancellationToken);
                AnsiConsole.MarkupLine($"[bold yellow]Peer {Markup.Escape(host)}:{port} disconnected and removed.[/]");
            }
            Pause();
        }
        else if (action.StartsWith("3."))
        {
            if (bannedPeers.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No banned peers.[/]");
                Pause();
                return;
            }

            var banChoice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select peer to unban:")
                    .AddChoices(bannedPeers.Select(p => $"{p.Host}:{p.Port}")));

            if (TryParseHostPort(banChoice, out var host, out var port))
            {
                await NodeManager.UnbanNodeAsync(host, port, cancellationToken: cancellationToken);
                AnsiConsole.MarkupLine($"[bold green]Peer {Markup.Escape(host)}:{port} unbanned.[/]");
            }
            Pause();
        }
    }

    /// <summary>
    /// Command 8: Synchronize with Network (adopts valid chain with greatest cumulative work and returns orphaned txs to mempool).
    /// </summary>
    private async Task SynchronizeNetworkCommandAsync(CancellationToken cancellationToken)
    {
        var beforeChain = await _syncService.GetLocalChainAsync(cancellationToken);
        var beforeHeight = beforeChain.Count;
        var beforeTip = beforeChain[^1].Header.Hash;

        SyncResult? syncResult = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Validating local chain, querying neighbor peers and resolving forks...", async _ =>
            {
                syncResult = await _syncService.InitialSyncAsync(cancellationToken);
            });

        var afterChain = await _syncService.GetLocalChainAsync(cancellationToken);
        var afterWork = StructuralBlockchainValidator.CalculateCumulativeWork(afterChain);

        Panel panel;
        if (syncResult != null && !syncResult.Success)
        {
            panel = new Panel(
                new Markup(
                    $"[bold red]SYNC WARNING: LOCAL BLOCKCHAIN IS CORRUPTED![/]\n\n" +
                    $"[bold]Status:[/]           [red]Corrupted & Unable to Recover[/]\n" +
                    $"[bold]Error Reason:[/]     [bold yellow]{Markup.Escape(syncResult.ErrorMessage ?? syncResult.Message)}[/]\n" +
                    $"[bold]Current Height:[/]   {afterChain.Count}\n\n" +
                    $"[grey]Mining is halted on this invalid chain. Add trusted peers to recover or validate via Command 6.[/]"
                )
            )
            {
                Header = new PanelHeader("[bold red]Command 8: Local Chain Integrity Warning[/]"),
                Border = BoxBorder.Rounded
            };
        }
        else
        {
            panel = new Panel(
                new Markup(
                    $"[bold green]{Markup.Escape(syncResult?.Message ?? "Synchronization completed!")}[/]\n\n" +
                    $"[bold]Previous Height / Tip:[/] {beforeHeight} ({Markup.Escape(beforeTip[..Math.Min(14, beforeTip.Length)])}...)\n" +
                    $"[bold]Current Height / Tip:[/]  [bold cyan]{afterChain.Count}[/] ({Markup.Escape(afterChain[^1].Header.Hash[..Math.Min(14, afterChain[^1].Header.Hash.Length)])}...)\n" +
                    $"[bold]Cumulative Work:[/]       [bold yellow]{afterWork}[/]\n" +
                    $"[bold]Pending Mempool Txs:[/]   [bold green]{_mempool.Count}[/]"
                )
            )
            {
                Header = new PanelHeader("[bold green]Command 8: Network Synchronization Result[/]"),
                Border = BoxBorder.Rounded
            };
        }

        AnsiConsole.Write(panel);
        Pause();
    }

    /// <summary>
    /// Step 8: Executes all 5 mandatory attack scenarios and prints formatted rejection verdicts for report screenshots.
    /// </summary>
    public void RenderAttacksDemo(bool pauseAfter = true)
    {
        AnsiConsole.MarkupLine("[bold cyan]=== STEP 8: FIVE MANDATORY SECURITY ATTACKS DEMONSTRATION ===[/]\n");

        var results = _attackDemoService.RunAllAttacks();
        foreach (var res in results)
        {
            var statusBadge = res.DefenseSucceeded
                ? "[bold green]ATTACK BLOCKED ✓[/]"
                : "[bold red]DEFENSE FAILED ✗[/]";

            var panel = new Panel(
                new Markup(
                    $"[bold]Scenario:[/]          {Markup.Escape(res.ScenarioDescription)}\n" +
                    $"[bold]Attack Action:[/]     [yellow]{Markup.Escape(res.AttackAction)}[/]\n" +
                    $"[bold]Defense Status:[/]    {statusBadge}\n" +
                    $"[bold]Rejection Verdict:[/] [bold red]{Markup.Escape(res.RejectionVerdict)}[/]"
                )
            )
            {
                Header = new PanelHeader($"[bold cyan]{Markup.Escape(res.Title)}[/]"),
                Border = BoxBorder.Rounded
            };

            AnsiConsole.Write(panel);
            AnsiConsole.WriteLine();
        }

        if (pauseAfter)
        {
            Pause();
        }
    }

    /// <summary>
    /// Runs the Proof-of-Work mining benchmark across 4 difficulty levels (D = 1, 2, 3, 4), 10 runs each,
    /// prints statistics and ASCII log-scale visualization, and saves CSV + SVG chart.
    /// </summary>
    public void RunMiningBenchmark(bool pauseAfter = true)
    {
        AnsiConsole.MarkupLine("[bold cyan]=== PROOF-OF-WORK MINING BENCHMARK (4 DIFFICULTIES x 10 RUNS) ===[/]\n");

        IReadOnlyList<DifficultyBenchmarkStats> stats = [];
        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Running 10 mining trials for each difficulty level D = 1, 2, 3, 4...", ctx =>
            {
                stats = _benchmarkService.RunBenchmark([1, 2, 3, 4], runsPerDifficulty: 10, (diff, run, ms, iters) =>
                {
                    ctx.Status($"Difficulty D={diff} | Run {run}/10: {ms:F2} ms ({iters} nonces)...");
                });
            });

        var table = new Table().Border(TableBorder.Rounded).Title("[bold cyan]PoW Mining Time Statistics (10 Runs per Level)[/]");
        table.AddColumn("Difficulty (D)");
        table.AddColumn("Runs");
        table.AddColumn(new TableColumn("Mean Time (ms)").RightAligned());
        table.AddColumn(new TableColumn("StdDev (ms)").RightAligned());
        table.AddColumn(new TableColumn("Min / Max (ms)").RightAligned());
        table.AddColumn(new TableColumn("Mean Iterations").RightAligned());
        table.AddColumn(new TableColumn("Log10(Mean ms)").RightAligned());

        foreach (var s in stats)
        {
            var log10 = Math.Log10(Math.Max(0.001, s.MeanTimeMs));
            table.AddRow(
                $"[bold yellow]D = {s.Difficulty}[/] ({new string('0', s.Difficulty)}...)",
                s.RunsCount.ToString(),
                $"[bold green]{s.MeanTimeMs:F3}[/]",
                $"± {s.StdDevTimeMs:F3}",
                $"{s.MinTimeMs:F2} / {s.MaxTimeMs:F2}",
                $"{s.MeanIterations:F0}",
                $"[cyan]{log10:F3}[/]"
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Save CSV and logarithmic SVG chart inside the active data directory
        var dataDir = Path.GetDirectoryName(_syncService.BlocksFilePath) ?? "data";
        var csvPath = Path.GetFullPath(Path.Combine(dataDir, "mining_benchmark.csv"));
        var svgPath = Path.GetFullPath(Path.Combine(dataDir, "mining_benchmark_log_chart.svg"));
        MiningBenchmarkService.SaveCsv(stats, csvPath);
        MiningBenchmarkService.SaveLogScaleSvgChart(stats, svgPath);

        AnsiConsole.MarkupLine($"[bold green]✓ Benchmark CSV saved to:[/]       [cyan]{Markup.Escape(csvPath)}[/]");
        AnsiConsole.MarkupLine($"[bold green]✓ Logarithmic SVG chart saved to:[/] [cyan]{Markup.Escape(svgPath)}[/]");

        if (pauseAfter)
        {
            Pause();
        }
    }

    private async Task ShowMempoolAndLogsAsync(CancellationToken cancellationToken)
    {
        var pending = _mempool.GetAll();
        var mpTable = new Table().Border(TableBorder.Rounded).Title($"[bold cyan]Pending Mempool Transactions ({pending.Count})[/]");
        mpTable.AddColumn("Tx ID");
        mpTable.AddColumn("Sender");
        mpTable.AddColumn("Recipient");
        mpTable.AddColumn("Amount");
        mpTable.AddColumn("Fee");
        mpTable.AddColumn("Nonce");

        foreach (var tx in pending)
        {
            mpTable.AddRow(
                $"[cyan]{Markup.Escape(tx.Id[..Math.Min(14, tx.Id.Length)])}...[/]",
                Markup.Escape(tx.Sender[..Math.Min(14, tx.Sender.Length)]) + "...",
                Markup.Escape(tx.Recipient[..Math.Min(14, tx.Recipient.Length)]) + "...",
                $"[bold green]{tx.Amount:F2}[/]",
                $"{tx.Fee:F2}",
                tx.Nonce.ToString()
            );
        }

        if (pending.Count == 0)
        {
            mpTable.AddRow("[grey]Empty[/]", "-", "-", "-", "-", "-");
        }

        AnsiConsole.Write(mpTable);
        AnsiConsole.WriteLine();

        var logs = _logBuffer.GetRecent(25);
        AnsiConsole.MarkupLine("[bold cyan]=== RECENT NODE LOGS ===[/]");
        foreach (var log in logs)
        {
            PrintLogEntry(log);
        }

        AnsiConsole.WriteLine();
        var streamChoice = AnsiConsole.Confirm("Stream live logs? (Press any key to stop streaming)", false);
        if (!streamChoice)
        {
            return;
        }

        AnsiConsole.MarkupLine("[dim green]Streaming live logs (press any key to return to menu)...[/]");
        void OnMessageLogged(LogMessage newLog) => PrintLogEntry(newLog);

        _logBuffer.MessageLogged += OnMessageLogged;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                    break;
                }
                await Task.Delay(100, cancellationToken);
            }
        }
        finally
        {
            _logBuffer.MessageLogged -= OnMessageLogged;
        }
    }

    private static void PrintLogEntry(LogMessage log)
    {
        var levelMarkup = log.Level switch
        {
            LogLevel.Error or LogLevel.Critical => "[bold red]ERR[/]",
            LogLevel.Warning => "[bold yellow]WRN[/]",
            LogLevel.Information => "[bold green]INF[/]",
            LogLevel.Debug => "[bold blue]DBG[/]",
            _ => "[grey]TRC[/]"
        };

        var cat = log.Category.Length > 25 ? log.Category[^25..] : log.Category;
        AnsiConsole.MarkupLine($"[grey]{log.Timestamp:HH:mm:ss}[/] {levelMarkup} [cyan]{Markup.Escape(cat),-25}[/] {Markup.Escape(log.Message)}");
    }

    private static bool TryParseHostPort(string input, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Port > 0)
        {
            host = uri.Host;
            port = uri.Port;
            return true;
        }

        var parts = trimmed.Split(':');
        if (parts.Length == 2 && int.TryParse(parts[1], out var parsedPort) && parsedPort > 0)
        {
            host = parts[0].Trim();
            port = parsedPort;
            return true;
        }

        return false;
    }

    private static void Pause()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press Enter to continue...[/]");
        Console.ReadLine();
    }
}
