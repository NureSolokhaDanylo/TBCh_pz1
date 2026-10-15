using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Miner.Logging;

public record LogMessage(
    DateTime Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    Exception? Exception
);

/// <summary>
/// Thread-safe in-memory circular buffer storing recent application log entries.
/// Allows interactive console UIs to render logs cleanly without interfering with prompts.
/// </summary>
public class InMemoryLogBuffer
{
    private readonly ConcurrentQueue<LogMessage> _queue = new();
    private readonly int _maxCapacity;

    public event Action<LogMessage>? MessageLogged;

    public InMemoryLogBuffer(int maxCapacity = 500)
    {
        _maxCapacity = maxCapacity;
    }

    public void Add(LogMessage logMessage)
    {
        _queue.Enqueue(logMessage);
        while (_queue.Count > _maxCapacity && _queue.TryDequeue(out _))
        {
        }

        try
        {
            MessageLogged?.Invoke(logMessage);
        }
        catch
        {
            // Suppress subscriber errors
        }
    }

    public IReadOnlyList<LogMessage> GetRecent(int count = 50)
    {
        var all = _queue.ToArray();
        return all.Length <= count ? all : all[^count..];
    }

    public LogMessage? GetLatest()
    {
        var all = _queue.ToArray();
        return all.Length > 0 ? all[^1] : null;
    }
}
