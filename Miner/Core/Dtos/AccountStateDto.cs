namespace Blockchain.Shared.Dtos;

/// <summary>
/// Data Transfer Object representing account state returned by a network node.
/// Includes current confirmed balance and the next expected transaction sequence number (Nonce).
/// </summary>
public class AccountStateDto
{
    public string Address { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public long Nonce { get; set; }

    public AccountStateDto()
    {
    }

    public AccountStateDto(string address, decimal balance, long nonce)
    {
        Address = address;
        Balance = balance;
        Nonce = nonce;
    }
}
