using Microsoft.Extensions.Logging;

namespace Miner.Logging;

/// <summary>
/// Custom logger provider that writes log entries into InMemoryLogBuffer and appends them to a log file.
/// </summary>
public class BufferedLoggerProvider : ILoggerProvider
{
    private readonly InMemoryLogBuffer _buffer;
    private readonly string _logFilePath;
    private readonly object _fileLock = new();

    public BufferedLoggerProvider(InMemoryLogBuffer buffer, string logFilePath = "data/miner.log")
    {
        _buffer = buffer;
        _logFilePath = logFilePath;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new BufferedLogger(categoryName, _buffer, _logFilePath, _fileLock);
    }

    public void Dispose()
    {
    }

    private class BufferedLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly InMemoryLogBuffer _buffer;
        private readonly string _logFilePath;
        private readonly object _fileLock;

        public BufferedLogger(string categoryName, InMemoryLogBuffer buffer, string logFilePath, object fileLock)
        {
            _categoryName = categoryName;
            _buffer = buffer;
            _logFilePath = logFilePath;
            _fileLock = fileLock;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message) && exception == null) return;

            var logEntry = new LogMessage(
                DateTime.UtcNow,
                logLevel,
                _categoryName,
                message,
                exception
            );

            _buffer.Add(logEntry);

            // Write to persistent log file
            try
            {
                var dir = Path.GetDirectoryName(_logFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var line = $"[{logEntry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel.ToString().ToUpperInvariant(),-3}] [{_categoryName}] {message}{(exception != null ? $" | {exception}" : string.Empty)}";
                lock (_fileLock)
                {
                    File.AppendAllLines(_logFilePath, [line]);
                }
            }
            catch
            {
                // Silently ignore file access issues
            }
        }
    }
}
