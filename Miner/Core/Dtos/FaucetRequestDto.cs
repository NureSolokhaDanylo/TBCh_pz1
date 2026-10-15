namespace Blockchain.Shared.Dtos;

/// <summary>
/// Data Transfer Object for requesting test funds from the faucet.
/// </summary>
public record FaucetRequestDto(string? Address, decimal? Amount);
