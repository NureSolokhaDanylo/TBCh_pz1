namespace Miner.Common;

/// <summary>
/// Configuration options for the mining service.
/// Reads environment variables if available or falls back to defaults.
/// </summary>
public class MiningOptions
{
    /// <summary>
    /// Wallet address receiving block mining rewards (Coinbase recipient).
    /// </summary>
    public string MinerAddress { get; set; } = Environment.GetEnvironmentVariable("MINER_ADDRESS") 
        ?? "0x0000000000000000000000000000000000000001";

    /// <summary>
    /// Reward amount credited to the miner per mined block.
    /// </summary>
    public decimal BlockReward { get; set; } = decimal.TryParse(Environment.GetEnvironmentVariable("MINER_REWARD"), out var r) ? r : 50.0m;

    /// <summary>
    /// Current Proof-of-Work difficulty (leading hex zeroes).
    /// </summary>
    public int Difficulty { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("MINER_DIFFICULTY"), out var d) ? d : 4;

    /// <summary>
    /// Whether continuous background mining is enabled.
    /// </summary>
    public bool Enabled { get; set; } = !string.Equals(Environment.GetEnvironmentVariable("MINER_ENABLED"), "false", StringComparison.OrdinalIgnoreCase);
}
