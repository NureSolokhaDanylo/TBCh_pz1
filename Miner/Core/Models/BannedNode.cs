namespace Blockchain.Shared.Models;

public class BannedNode
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string? Reason { get; set; }
    public DateTime BannedAt { get; set; }

    public BannedNode()
    {
    }

    public BannedNode(string host, int port, string? reason = null)
    {
        Host = host;
        Port = port;
        Reason = reason;
        BannedAt = DateTime.UtcNow;
    }
}
