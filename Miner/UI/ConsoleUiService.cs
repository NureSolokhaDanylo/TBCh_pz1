using Blockchain.Shared.Common;
using Blockchain.Shared.Models;
using Miner.Common;
using Miner.Logging;
using Miner.Services;
using Miner.Wallet;
using Spectre.Console;

namespace Miner.UI;

/// <summary>
/// Interactive terminal user interface powered by Spectre.Console.
/// Manages wallets, transactions, mining, node status, and log inspection cleanly.
/// </summary>
public class ConsoleUiService
{
    private readonly WalletService _walletService;
    private readonly ChainSynchronizationService _syncService;
    private readonly Mempool _mempool;
    private readonly MiningController _miningController;
    private readonly InMemoryLogBuffer _logBuffer;
    private readonly ILogger<ConsoleUiService> _logger;

    public ConsoleUiService(
        WalletService walletService,
        ChainSynchronizationService syncService,
        Mempool mempool,
        MiningController miningController,
        InMemoryLogBuffer logBuffer,
        ILogger<ConsoleUiService> logger)
    {
        _walletService = walletService;
        _syncService = syncService;
        _mempool = mempool;
        _miningController = miningController;
        _logBuffer = logBuffer;
        _logger = logger;
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
                        .Title("[yellow]Select an option:[/]")
                        .PageSize(10)
                        .AddChoices([
                            "1. 💼 Wallets & Balances",
                            "2. ➕ Create New Wallet",
                            "3. 💸 Send Transaction",
                            "4. 🔑 View Wallet Keys",
                            "5. ⛏️ Mine Next Block",
                            "6. 🌐 Node & Blockchain Status",
                            "7. 📜 View Node Logs",
                            "8. 💧 Credit Test Coins (Faucet)",
                            "0. 🚪 Exit Node"
                        ]));

                AnsiConsole.WriteLine();

                switch (choice)
                {
                    case "1. 💼 Wallets & Balances":
                        await ShowWalletsAsync(cancellationToken);
                        break;
                    case "2. ➕ Create New Wallet":
                        await CreateWalletAsync(cancellationToken);
                        break;
                    case "3. 💸 Send Transaction":
                        await SendTransactionAsync(cancellationToken);
                        break;
                    case "4. 🔑 View Wallet Keys":
                        await ViewWalletKeysAsync(cancellationToken);
                        break;
                    case "5. ⛏️ Mine Next Block":
                        await MineBlockAsync(cancellationToken);
                        break;
                    case "6. 🌐 Node & Blockchain Status":
                        await ShowNodeStatusAsync(cancellationToken);
                        break;
                    case "7. 📜 View Node Logs":
                        await ShowLogsAsync(cancellationToken);
                        break;
                    case "8. 💧 Credit Test Coins (Faucet)":
                        await CreditFaucetAsync(cancellationToken);
                        break;
                    case "0. 🚪 Exit Node":
                        if (AnsiConsole.Confirm("Are you sure you want to stop the node and exit?"))
                        {
                            AnsiConsole.MarkupLine("[bold red]Shutting down node... Goodbye![/]");
                            return;
                        }
                        break;
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
        var latestLog = _logBuffer.GetLatest();

        var table = new Table().Border(TableBorder.Rounded).Expand();
        table.AddColumn(new TableColumn("[bold cyan]BLOCKCHAIN FULL-NODE & WALLET[/]").Centered());

        var miningStateStr = _miningController.State switch
        {
            MiningState.Mining => "[bold green]MINING[/]",
            MiningState.Syncing => "[bold yellow]SYNCING[/]",
            MiningState.Paused => "[bold yellow]PAUSED[/]",
            _ => "[bold grey]IDLE[/]"
        };

        var statusLine = $"Chain Height: [bold white]{chain.Count}[/] | Tip: [grey]{Markup.Escape(tip.Header.Hash[..12])}...[/] | Mempool: [bold white]{_mempool.Count}[/] | State: {miningStateStr}";
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

    private async Task ShowWalletsAsync(CancellationToken cancellationToken)
    {
        var wallets = await _walletService.GetWalletsAsync(cancellationToken);
        if (wallets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No local wallets found. Create one using option 2.[/]");
            Pause();
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("#");
        table.AddColumn("Label");
        table.AddColumn("Address");
        table.AddColumn(new TableColumn("Verified Balance").RightAligned());
        table.AddColumn(new TableColumn("Nonce").RightAligned());
        table.AddColumn("Created At (UTC)");

        for (int i = 0; i < wallets.Count; i++)
        {
            var w = wallets[i];
            table.AddRow(
                (i + 1).ToString(),
                Markup.Escape(w.Label),
                $"[cyan]{Markup.Escape(w.Address)}[/]",
                $"[bold green]{w.Balance:F2}[/]",
                w.Nonce.ToString(),
                w.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")
            );
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]Total wallets: {wallets.Count}. Balances and nonces are verified deterministically by the local full node.[/]");
        Pause();
    }

    private async Task CreateWalletAsync(CancellationToken cancellationToken)
    {
        var label = AnsiConsole.Ask<string>("Enter wallet label (e.g. 'Main', 'Miner'):", "Main");
        var wallet = await _walletService.CreateWalletAsync(label.Trim(), cancellationToken);

        var panel = new Panel(
            new Markup(
                $"[bold green]Wallet successfully created![/]\n\n" +
                $"[bold]Label:[/]       {Markup.Escape(wallet.Label)}\n" +
                $"[bold]Address:[/]     [cyan]{Markup.Escape(wallet.Address)}[/]\n" +
                $"[bold]Nonce:[/]       {wallet.Nonce}\n" +
                $"[bold]Balance:[/]     {wallet.Balance}\n" +
                $"[bold]Storage:[/]     {Markup.Escape(_walletService.WalletsFilePath)}"
            )
        )
        {
            Header = new PanelHeader("[bold green]New Wallet Details[/]"),
            Border = BoxBorder.Rounded
        };

        AnsiConsole.Write(panel);
        Pause();
    }

    private async Task SendTransactionAsync(CancellationToken cancellationToken)
    {
        var wallets = await _walletService.GetWalletsAsync(cancellationToken);
        if (wallets.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No local wallets available. Please create a wallet first.[/]");
            Pause();
            return;
        }

        var senderChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select [bold]sender wallet[/]:")
                .PageSize(10)
                .AddChoices(wallets.Select(w => $"[{w.Label}] {w.Address} | Bal: {w.Balance:F2} | Nonce: {w.Nonce}"))
        );

        var selectedIndex = wallets.FindIndex(w => senderChoice.Contains(w.Address));
        var senderWallet = wallets[selectedIndex];

        var recipient = AnsiConsole.Ask<string>("Enter [bold]recipient address[/] (0x...):");
        var amount = AnsiConsole.Ask<decimal>("Enter [bold]transfer amount[/]:");

        var (success, tx, message) = await _walletService.CreateAndSendTransactionAsync(senderWallet, recipient, amount, cancellationToken);

        if (success && tx != null)
        {
            var panel = new Panel(
                new Markup(
                    $"[bold green]Transaction successfully created, signed, and broadcasted![/]\n\n" +
                    $"[bold]Tx ID (Hash):[/]  [cyan]{Markup.Escape(tx.Id)}[/]\n" +
                    $"[bold]Sender:[/]        {Markup.Escape(tx.Sender)}\n" +
                    $"[bold]Recipient:[/]     {Markup.Escape(tx.Recipient)}\n" +
                    $"[bold]Amount:[/]        [bold yellow]{tx.Amount:F2}[/]\n" +
                    $"[bold]Tx Nonce:[/]      {tx.Nonce}\n" +
                    $"[bold]Signature:[/]     [dim]{Markup.Escape(tx.Signature != null && tx.Signature.Length > 20 ? tx.Signature[..20] + "..." : tx.Signature ?? "N/A")}[/]\n" +
                    $"[bold]Status:[/]        Validated in local Mempool & broadcasted to peers"
                )
            )
            {
                Header = new PanelHeader("[bold green]Transaction Sent[/]"),
                Border = BoxBorder.Rounded
            };
            AnsiConsole.Write(panel);
        }
        else
        {
            AnsiConsole.MarkupLine($"[bold red]Transaction Failed:[/] {Markup.Escape(message)}");
        }

        Pause();
    }

    private async Task ViewWalletKeysAsync(CancellationToken cancellationToken)
    {
        var wallets = await _walletService.GetWalletsAsync(cancellationToken);
        if (wallets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No local wallets found.[/]");
            Pause();
            return;
        }

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select wallet to inspect keys:")
                .PageSize(10)
                .AddChoices(wallets.Select(w => $"[{w.Label}] {w.Address}"))
        );

        var selectedIndex = wallets.FindIndex(w => choice.Contains(w.Address));
        var w = wallets[selectedIndex];

        var panel = new Panel(
            new Markup(
                $"[bold yellow]WARNING: Never share your private key with anyone![/]\n\n" +
                $"[bold]Label:[/]       {Markup.Escape(w.Label)}\n" +
                $"[bold]Address:[/]     [cyan]{Markup.Escape(w.Address)}[/]\n" +
                $"[bold]Nonce:[/]       {w.Nonce}\n" +
                $"[bold]Balance:[/]     {w.Balance:F2}\n\n" +
                $"[bold]Public Key (Hex):[/]\n[grey]{Markup.Escape(w.PublicKey)}[/]\n\n" +
                $"[bold red]Private Key (Hex - SECRET):[/]\n[red]{Markup.Escape(w.PrivateKey)}[/]"
            )
        )
        {
            Header = new PanelHeader($"[bold red]Wallet Security - {Markup.Escape(w.Label)}[/]"),
            Border = BoxBorder.Heavy
        };

        AnsiConsole.Write(panel);
        Pause();
    }

    private async Task MineBlockAsync(CancellationToken cancellationToken)
    {
        var wallets = await _walletService.GetWalletsAsync(cancellationToken);
        string minerAddress;

        if (wallets.Count > 0)
        {
            var choices = wallets.Select(w => $"[{w.Label}] {w.Address}").ToList();
            choices.Add("Custom Address...");

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select [bold]Coinbase reward recipient address[/]:")
                    .AddChoices(choices)
            );

            if (choice == "Custom Address...")
            {
                minerAddress = AnsiConsole.Ask<string>("Enter custom miner address (0x...):");
            }
            else
            {
                var idx = wallets.FindIndex(w => choice.Contains(w.Address));
                minerAddress = wallets[idx].Address;
            }
        }
        else
        {
            minerAddress = AnsiConsole.Ask<string>("Enter miner address to receive Coinbase reward (0x...):");
        }

        Block? minedBlock = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Mining candidate block (Proof-of-Work)...", async ctx =>
            {
                minedBlock = await _syncService.MineNextBlockAsync(
                    minerAddress: minerAddress,
                    blockReward: 50.0m,
                    difficulty: 4,
                    cancellationToken: cancellationToken
                );
            });

        if (minedBlock != null)
        {
            var panel = new Panel(
                new Markup(
                    $"[bold green]Block #{minedBlock.Header.Index} successfully mined and broadcasted![/]\n\n" +
                    $"[bold]Block Hash:[/]      [cyan]{Markup.Escape(minedBlock.Header.Hash)}[/]\n" +
                    $"[bold]Previous Hash:[/]   [grey]{Markup.Escape(minedBlock.Header.PreviousHash)}[/]\n" +
                    $"[bold]Merkle Root:[/]     [grey]{Markup.Escape(minedBlock.Header.MerkleRoot)}[/]\n" +
                    $"[bold]PoW Nonce:[/]       {minedBlock.Header.Nonce}\n" +
                    $"[bold]Difficulty:[/]      {minedBlock.Header.Difficulty}\n" +
                    $"[bold]Transactions:[/]    {minedBlock.Transactions.Count} (1 Coinbase + {minedBlock.Transactions.Count - 1} mempool)\n" +
                    $"[bold]Coinbase Reward:[/] [bold yellow]50.00[/] -> [cyan]{Markup.Escape(minerAddress)}[/]"
                )
            )
            {
                Header = new PanelHeader($"[bold green]Mined Block #{minedBlock.Header.Index}[/]"),
                Border = BoxBorder.Rounded
            };
            AnsiConsole.Write(panel);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]Mining stopped without producing a block.[/]");
        }

        Pause();
    }

    private async Task ShowNodeStatusAsync(CancellationToken cancellationToken)
    {
        var chain = await _syncService.GetLocalChainAsync(cancellationToken);
        var tip = chain[^1];
        var pendingTxs = _mempool.GetAll();
        var trustedPeers = await NodeManager.GetTrustedNodesAsync(cancellationToken: cancellationToken);
        var bannedPeers = await NodeManager.GetBannedNodesAsync(cancellationToken: cancellationToken);

        var chainTable = new Table().Border(TableBorder.Rounded);
        chainTable.AddColumn("Property");
        chainTable.AddColumn("Value");
        chainTable.AddRow("Chain Height", chain.Count.ToString());
        chainTable.AddRow("Tip Block Index", tip.Header.Index.ToString());
        chainTable.AddRow("Tip Hash", $"[cyan]{Markup.Escape(tip.Header.Hash)}[/]");
        chainTable.AddRow("Tip Merkle Root", Markup.Escape(tip.Header.MerkleRoot));
        chainTable.AddRow("Mining Difficulty", tip.Header.Difficulty.ToString());
        chainTable.AddRow("Mining Controller State", _miningController.State.ToString());
        chainTable.AddRow("Pending Transactions (Mempool)", pendingTxs.Count.ToString());

        AnsiConsole.Write(new Panel(chainTable) { Header = new PanelHeader("[bold cyan]Local Blockchain Status[/]") });

        var peersTable = new Table().Border(TableBorder.Rounded);
        peersTable.AddColumn("Peer");
        peersTable.AddColumn("Status");
        peersTable.AddColumn("Details");

        foreach (var p in trustedPeers)
        {
            peersTable.AddRow($"{Markup.Escape(p.Host)}:{p.Port}", "[green]Active / Trusted[/]", $"Seen: {p.LastSeen:HH:mm:ss}");
        }

        foreach (var b in bannedPeers)
        {
            peersTable.AddRow($"{Markup.Escape(b.Host)}:{b.Port}", "[red]Banned[/]", Markup.Escape(b.Reason ?? "No reason specified"));
        }

        if (trustedPeers.Count == 0 && bannedPeers.Count == 0)
        {
            peersTable.AddRow("[grey]None[/]", "[grey]-[/]", "No peers configured in data/nodes.json");
        }

        AnsiConsole.Write(new Panel(peersTable) { Header = new PanelHeader("[bold cyan]Network Peers[/]") });
        Pause();
    }

    private async Task ShowLogsAsync(CancellationToken cancellationToken)
    {
        var logs = _logBuffer.GetRecent(30);

        AnsiConsole.MarkupLine("[bold cyan]=== RECENT NODE LOGS ===[/]");
        foreach (var log in logs)
        {
            PrintLogEntry(log);
        }

        AnsiConsole.WriteLine();
        var streamChoice = AnsiConsole.Confirm("Would you like to stream live logs? (Press any key to stop streaming)", false);
        if (!streamChoice)
        {
            return;
        }

        AnsiConsole.MarkupLine("[dim green]Streaming live logs (press any key to return to menu)...[/]");
        void OnMessageLogged(LogMessage newLog)
        {
            PrintLogEntry(newLog);
        }

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

    private async Task CreditFaucetAsync(CancellationToken cancellationToken)
    {
        var wallets = await _walletService.GetWalletsAsync(cancellationToken);
        if (wallets.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No local wallets available. Please create a wallet first.[/]");
            Pause();
            return;
        }

        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select wallet to credit test coins:")
                .AddChoices(wallets.Select(w => $"[{w.Label}] {w.Address} | Bal: {w.Balance:F2}"))
        );

        var selectedIndex = wallets.FindIndex(w => choice.Contains(w.Address));
        var targetWallet = wallets[selectedIndex];

        var amount = AnsiConsole.Ask<decimal>("Enter amount of test coins to credit:", 100m);
        var updated = _walletService.CreditFaucet(targetWallet.Address, amount);

        AnsiConsole.MarkupLine($"[bold green]Success![/] Credited [bold yellow]{amount:F2}[/] to [cyan]{Markup.Escape(targetWallet.Address)}[/].");
        AnsiConsole.MarkupLine($"New Confirmed Balance: [bold green]{updated.Balance:F2}[/]");
        Pause();
    }

    private static void Pause()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press Enter to continue...[/]");
        Console.ReadLine();
    }
}
