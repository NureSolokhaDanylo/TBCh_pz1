namespace Blockchain.Shared.Models;

public class TrustedNode
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }

    /// <summary>
    /// Indicates whether the node is trusted.
    /// </summary>
    public bool IsTrusted { get; set; } = true;

    /// <summary>
    /// Node reputation score (0 to 100).
    /// Used to rank and select the best nodes to interact with.
    /// </summary>
    public int Reputation { get; set; } = 100;

    /// <summary>
    /// Total number of successful responses from the node.
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Total number of failed communication attempts or timeouts.
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// Number of consecutive connection failures.
    /// Helps track temporarily unresponsive nodes without placing them in the ban list.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Timestamp of the last successful response.
    /// </summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// Timestamp of the last health check / poll attempt.
    /// </summary>
    public DateTime? LastChecked { get; set; }

    /// <summary>
    /// Network latency in milliseconds.
    /// </summary>
    public long? LatencyMs { get; set; }

    /// <summary>
    /// Node reliability ratio (from 0.0 to 1.0).
    /// </summary>
    public double SuccessRate => (SuccessCount + FailureCount) == 0
        ? 1.0
        : Math.Round((double)SuccessCount / (SuccessCount + FailureCount), 2);

    /// <summary>
    /// Indicates if the node has been responding reliably in recent requests.
    /// </summary>
    public bool IsResponsive => ConsecutiveFailures == 0 && LastSeen != null;

    public TrustedNode()
    {
    }

    public TrustedNode(string host, int port, bool isTrusted = true, int reputation = 100)
    {
        Host = host;
        Port = port;
        IsTrusted = isTrusted;
        Reputation = reputation;
    }

    /// <summary>
    /// Records a successful response from the node and updates latency metrics.
    /// </summary>
    public void RecordSuccess(long latencyMs)
    {
        SuccessCount++;
        ConsecutiveFailures = 0;
        LastSeen = DateTime.UtcNow;
        LastChecked = DateTime.UtcNow;
        LatencyMs = latencyMs;
        Reputation = Math.Min(100, Reputation + 1);
    }

    /// <summary>
    /// Records a communication failure with the node.
    /// Decreases reputation and increments failure counters without banning the node.
    /// </summary>
    public void RecordFailure()
    {
        FailureCount++;
        ConsecutiveFailures++;
        LastChecked = DateTime.UtcNow;
        LatencyMs = null;
        Reputation = Math.Max(0, Reputation - 5);
    }
}
