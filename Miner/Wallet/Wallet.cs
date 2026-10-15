namespace Miner.Wallet;

/// <summary>
/// User local wallet model.
/// The account model follows Ethereum: the address is derived from the public key,
/// and the Nonce counter tracks the count of outgoing transactions to prevent replay attacks.
/// </summary>
public class Wallet
{
    public string Address { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Account transaction counter (Nonce). Starts at 0, incremented upon each sent transaction.
    /// </summary>
    public long Nonce { get; set; }

    /// <summary>
    /// Wallet balance confirmed from local blockchain and ledger.
    /// </summary>
    public decimal Balance { get; set; }

    /// <summary>
    /// User-friendly label for the wallet (e.g. "Main", "Savings").
    /// </summary>
    public string Label { get; set; } = "Wallet";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Wallet()
    {
    }

    public Wallet(string address, string publicKey, string privateKey, string label = "Wallet")
    {
        Address = address;
        PublicKey = publicKey;
        PrivateKey = privateKey;
        Label = label;
        Nonce = 0;
        Balance = 0;
        CreatedAt = DateTime.UtcNow;
    }
}
