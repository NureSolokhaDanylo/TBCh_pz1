namespace Blockchain.Shared.Models;

/// <summary>
/// Represents a network peer.
/// Lightweight model focusing on connection details without complex scoring.
/// </summary>
public class TrustedNode
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }

    /// <summary>
    /// Indicates whether the node is trusted by the local node.
    /// </summary>
    public bool IsTrusted { get; set; } = true;

    /// <summary>
    /// Timestamp of the last successful interaction.
    /// </summary>
    public DateTime? LastSeen { get; set; }

    /// <summary>
    /// Network latency in milliseconds (optional).
    /// </summary>
    public long? LatencyMs { get; set; }

    public TrustedNode()
    {
    }

    public TrustedNode(string host, int port, bool isTrusted = true)
    {
        Host = host;
        Port = port;
        IsTrusted = isTrusted;
    }
}
