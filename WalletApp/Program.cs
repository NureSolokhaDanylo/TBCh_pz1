using Blockchain.Shared.Common;
using Blockchain.Shared.Models;
using WalletApp.Models;
using WalletApp.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.InputEncoding = System.Text.Encoding.UTF8;

var walletService = new WalletService();
var nodeClient = new NodeClient();

if (!Console.IsOutputRedirected && !Console.IsInputRedirected)
{
    try { Console.Clear(); } catch { }
}
PrintHeader();

while (true)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("\n=================== MAIN MENU ===================");
    Console.ResetColor();
    Console.WriteLine("1. List my wallets");
    Console.WriteLine("2. Create new wallet");
    Console.WriteLine("3. Create and sign transaction");
    Console.WriteLine("4. Sync balance and Nonce with node (manual)");
    Console.WriteLine("5. Broadcast last transaction to network node");
    Console.WriteLine("6. View and export wallet keys");
    Console.WriteLine("7. List configured miner nodes (data/wallet_nodes.json)");
    Console.WriteLine("8. Add new miner node");
    Console.WriteLine("0. Exit");
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("\nSelect menu option > ");
    Console.ResetColor();

    var choice = Console.ReadLine()?.Trim();
    Console.WriteLine();

    switch (choice)
    {
        case "1":
            await ShowWalletsAsync();
            break;
        case "2":
            await CreateWalletAsync();
            break;
        case "3":
            await CreateTransactionAsync();
            break;
        case "4":
            await SyncWithNodeAsync();
            break;
        case "5":
            await BroadcastTransactionAsync();
            break;
        case "6":
            await ShowWalletDetailsAsync();
            break;
        case "7":
            await ShowNodesAsync();
            break;
        case "8":
            await AddNodeAsync();
            break;
        case "0":
            Console.WriteLine("Exiting wallet application. Goodbye!");
            return;
        default:
            PrintWarning("Invalid option. Please try again.");
            break;
    }
}

void PrintHeader()
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════╗
║             BLOCKCHAIN WALLET CLI (Ethereum Model)           ║
║       Manage Wallets, Nonce Tracking & Signed Transactions   ║
╚══════════════════════════════════════════════════════════════╝");
    Console.ResetColor();
}

async Task ShowWalletsAsync()
{
    var wallets = await walletService.GetWalletsAsync();
    if (wallets.Count == 0)
    {
        PrintWarning("You do not have any wallets yet. Create one using option 2.");
        return;
    }

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("┌─────┬──────────────────────┬────────────────────────────────────────────┬─────────────┬───────┬─────────────────────┐");
    Console.WriteLine("│  #  │ Label                │ Wallet Address                             │ Balance     │ Nonce │ Last Synced         │");
    Console.WriteLine("├─────┼──────────────────────┼────────────────────────────────────────────┼─────────────┼───────┼─────────────────────┤");
    Console.ResetColor();

    for (int i = 0; i < wallets.Count; i++)
    {
        var w = wallets[i];
        var syncStr = w.LastSyncedAt.HasValue ? w.LastSyncedAt.Value.ToLocalTime().ToString("dd.MM HH:mm:ss") : "Not synced";
        Console.WriteLine($"│ {i + 1,3} │ {Truncate(w.Label, 20),-20} │ {w.Address,-42} │ {w.Balance,11:F2} │ {w.Nonce,5} │ {syncStr,-19} │");
    }

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("└─────┴──────────────────────┴────────────────────────────────────────────┴─────────────┴───────┴─────────────────────┘");
    Console.ResetColor();
    Console.WriteLine($"Total wallets: {wallets.Count}. Balances reflect latest node sync responses.");
}

async Task CreateWalletAsync()
{
    Console.Write("Enter wallet label (e.g. 'Main'): ");
    var label = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(label))
    {
        label = $"Wallet #{DateTime.UtcNow.Ticks % 1000}";
    }

    var wallet = await walletService.CreateWalletAsync(label);
    PrintSuccess("Wallet successfully created!");
    Console.WriteLine($"Label:   {wallet.Label}");
    Console.WriteLine($"Address: {wallet.Address}");
    Console.WriteLine($"Nonce:   {wallet.Nonce}");
    Console.WriteLine($"Balance: {wallet.Balance}");
    Console.WriteLine($"File:    {walletService.WalletsFilePath}");
}

async Task CreateTransactionAsync()
{
    var wallets = await walletService.GetWalletsAsync();
    if (wallets.Count == 0)
    {
        PrintError("Please create at least one wallet first (option 2).");
        return;
    }

    await ShowWalletsAsync();
    Console.Write("\nSelect sender wallet number > ");
    if (!int.TryParse(Console.ReadLine(), out int idx) || idx < 1 || idx > wallets.Count)
    {
        PrintError("Invalid wallet number.");
        return;
    }

    var sender = wallets[idx - 1];

    Console.Write("Enter recipient address (0x...): ");
    var recipient = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(recipient))
    {
        PrintError("Recipient address cannot be empty.");
        return;
    }

    Console.Write("Enter transfer amount: ");
    if (!decimal.TryParse(Console.ReadLine()?.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out decimal amount) || amount <= 0)
    {
        PrintError("Invalid transfer amount.");
        return;
    }

    Console.WriteLine($"\nCurrent sender wallet Nonce: {sender.Nonce}");
    var (tx, updatedWallet) = await walletService.CreateAndSignTransactionAsync(sender, recipient, amount);

    PrintSuccess("\nTransaction successfully created and signed with private key!");
    Console.WriteLine("-----------------------------------------------------------------");
    Console.WriteLine($"ID (Hash):   {tx.Id}");
    Console.WriteLine($"Sender:      {tx.Sender}");
    Console.WriteLine($"Recipient:   {tx.Recipient}");
    Console.WriteLine($"Amount:      {tx.Amount}");
    Console.WriteLine($"Tx Nonce:    {tx.Nonce}");
    Console.WriteLine($"Time (UTC):  {tx.Timestamp:O}");
    Console.WriteLine($"Signature:   {tx.Signature}");
    Console.WriteLine("-----------------------------------------------------------------");
    Console.WriteLine($"Sender wallet Nonce incremented to: {updatedWallet.Nonce} (Ethereum Replay Protection)");
    Console.WriteLine($"Transaction saved to: {walletService.OutboxTransactionsFilePath}");
}

async Task SyncWithNodeAsync()
{
    var wallets = await walletService.GetWalletsAsync();
    if (wallets.Count == 0)
    {
        PrintError("No wallets available to synchronize.");
        return;
    }

    await ShowWalletsAsync();
    Console.Write("\nSelect wallet number to sync > ");
    if (!int.TryParse(Console.ReadLine(), out int idx) || idx < 1 || idx > wallets.Count)
    {
        PrintError("Invalid wallet number.");
        return;
    }

    var targetWallet = wallets[idx - 1];

    var nodes = await nodeClient.GetNodeUrlsAsync();
    if (nodes.Count == 0)
    {
        PrintError("No miner nodes configured.");
        return;
    }

    Console.WriteLine("\nAvailable miner nodes (from data/wallet_nodes.json):");
    for (int i = 0; i < nodes.Count; i++)
    {
        Console.WriteLine($"  [{i + 1}] {nodes[i]}");
    }

    Console.Write("Select node to query (or press Enter for first) > ");
    var nodeInput = Console.ReadLine()?.Trim();
    string selectedNode = nodes[0];
    if (int.TryParse(nodeInput, out int nIdx) && nIdx >= 1 && nIdx <= nodes.Count)
    {
        selectedNode = nodes[nIdx - 1];
    }

    Console.WriteLine($"\nSending GET request: {selectedNode}/accounts/{targetWallet.Address} ...");
    var (success, state, message) = await nodeClient.FetchAccountStateAsync(selectedNode, targetWallet.Address);

    if (success && state is not null)
    {
        await walletService.UpdateSyncStateAsync(targetWallet.Address, state.Balance, state.Nonce);
        PrintSuccess($"Synchronization successful!\nUpdated Balance: {state.Balance}\nUpdated Nonce: {state.Nonce}");
    }
    else
    {
        PrintWarning($"Node response: {message}");
        Console.Write("Would you like to manually input confirmed balance for testing? (y/n) > ");
        if (Console.ReadLine()?.Trim().ToLowerInvariant() == "y")
        {
            Console.Write("Enter balance: ");
            if (decimal.TryParse(Console.ReadLine()?.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out decimal manualBalance))
            {
                await walletService.UpdateSyncStateAsync(targetWallet.Address, manualBalance);
                PrintSuccess($"Local wallet balance set to: {manualBalance}");
            }
        }
    }
}

async Task BroadcastTransactionAsync()
{
    var txList = await JsonLinesHelper.ReadAllAsync<Transaction>(walletService.OutboxTransactionsFilePath);
    if (txList.Count == 0)
    {
        PrintWarning("No transactions in the outbox. Please create a transaction first (option 3).");
        return;
    }

    var lastTx = txList.Last();
    Console.WriteLine("Latest created transaction:");
    Console.WriteLine($"  ID:        {lastTx.Id}");
    Console.WriteLine($"  Sender:    {lastTx.Sender}");
    Console.WriteLine($"  Recipient: {lastTx.Recipient}");
    Console.WriteLine($"  Amount:    {lastTx.Amount}");
    Console.WriteLine($"  Tx Nonce:  {lastTx.Nonce}");

    var nodes = await nodeClient.GetNodeUrlsAsync();
    Console.WriteLine("\nSelect miner node to broadcast to (from data/wallet_nodes.json):");
    for (int i = 0; i < nodes.Count; i++)
    {
        Console.WriteLine($"  [{i + 1}] {nodes[i]}");
    }

    Console.Write("Node number > ");
    if (!int.TryParse(Console.ReadLine(), out int nIdx) || nIdx < 1 || nIdx > nodes.Count)
    {
        PrintError("Invalid node number.");
        return;
    }

    var targetNode = nodes[nIdx - 1];
    Console.WriteLine($"\nBroadcasting POST request: {targetNode}/transactions ...");
    var (success, message) = await nodeClient.BroadcastTransactionAsync(targetNode, lastTx);

    if (success)
    {
        PrintSuccess(message);
    }
    else
    {
        PrintWarning(message);
    }
}

async Task ShowWalletDetailsAsync()
{
    var wallets = await walletService.GetWalletsAsync();
    if (wallets.Count == 0)
    {
        PrintError("No wallets found.");
        return;
    }

    await ShowWalletsAsync();
    Console.Write("\nSelect wallet number to view keys > ");
    if (!int.TryParse(Console.ReadLine(), out int idx) || idx < 1 || idx > wallets.Count)
    {
        PrintError("Invalid wallet number.");
        return;
    }

    var w = wallets[idx - 1];
    Console.WriteLine("\n================= WALLET DETAILS =================");
    Console.WriteLine($"Label:         {w.Label}");
    Console.WriteLine($"Address:       {w.Address}");
    Console.WriteLine($"Current Nonce: {w.Nonce}");
    Console.WriteLine($"Balance:       {w.Balance}");
    Console.WriteLine($"Created At:    {w.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    Console.ForegroundColor = ConsoleColor.DarkYellow;
    Console.WriteLine($"Public Key (Hex):\n{w.PublicKey}");
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Private Key (Hex - SECRET):\n{w.PrivateKey}");
    Console.ResetColor();
    Console.WriteLine("==================================================");
}

async Task ShowNodesAsync()
{
    var nodes = await nodeClient.GetNodeUrlsAsync();
    Console.WriteLine($"Configured miner nodes from [{nodeClient.NodesFilePath}]:");
    for (int i = 0; i < nodes.Count; i++)
    {
        Console.WriteLine($"  [{i + 1}] {nodes[i]}");
    }
}

async Task AddNodeAsync()
{
    Console.Write("Enter URL of the new miner node (e.g. http://localhost:8084): ");
    var url = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
    {
        PrintError("Invalid URL format.");
        return;
    }

    var added = await nodeClient.AddNodeUrlAsync(url);
    if (added)
    {
        PrintSuccess($"Node {url} successfully added to {nodeClient.NodesFilePath}.");
    }
    else
    {
        PrintWarning("This node is already present in the list.");
    }
}

void PrintSuccess(string message)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"[OK] {message}");
    Console.ResetColor();
}

void PrintWarning(string message)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"[!] {message}");
    Console.ResetColor();
}

void PrintError(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[ERROR] {message}");
    Console.ResetColor();
}

string Truncate(string value, int maxLen)
{
    if (string.IsNullOrEmpty(value)) return string.Empty;
    return value.Length <= maxLen ? value : value[..(maxLen - 3)] + "...";
}
