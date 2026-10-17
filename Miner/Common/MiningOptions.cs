namespace Miner.Common;

/// <summary>
/// Configuration options for the mining service and difficulty retargeting.
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
    /// Base reward amount credited to the miner per mined block (excluding transaction fees).
    /// </summary>
    public decimal BlockReward { get; set; } = decimal.TryParse(Environment.GetEnvironmentVariable("MINER_REWARD"), out var r) ? r : 50.0m;

    /// <summary>
    /// Initial Proof-of-Work difficulty (leading hex zeroes).
    /// </summary>
    public int Difficulty { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("MINER_DIFFICULTY"), out var d) ? d : 4;

    /// <summary>
    /// Number of blocks (K) between automatic difficulty adjustments.
    /// </summary>
    public int AdjustmentIntervalBlocks { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("MINER_ADJUSTMENT_INTERVAL"), out var k) ? k : 10;

    /// <summary>
    /// Target time in seconds to mine a single block.
    /// </summary>
    public int TargetBlockTimeSeconds { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("MINER_TARGET_BLOCK_TIME"), out var t) ? t : 10;

    /// <summary>
    /// Whether continuous background auto-mining is enabled (defaults to true unless explicitly disabled via MINER_ENABLED=false).
    /// </summary>
    public bool Enabled { get; set; } = !string.Equals(Environment.GetEnvironmentVariable("MINER_ENABLED"), "false", StringComparison.OrdinalIgnoreCase);
}
