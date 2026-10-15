namespace Blockchain.Shared.Models;

/// <summary>
/// Represents the live state of an account: confirmed balance and Nonce counter.
/// </summary>
public class AccountState
{
    public string Address { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public long Nonce { get; set; }

    public AccountState()
    {
    }

    public AccountState(string address, decimal balance, long nonce)
    {
        Address = address;
        Balance = balance;
        Nonce = nonce;
    }
}
