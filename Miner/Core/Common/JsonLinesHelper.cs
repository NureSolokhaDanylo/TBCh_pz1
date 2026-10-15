using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Blockchain.Shared.Common;

/// <summary>
/// Helper class for working with files in JSON Lines (JSONL) format.
/// Each line in the file represents a standalone valid JSON object.
/// </summary>
public static class JsonLinesHelper
{
    public static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false, // In JSONL, each object must strictly occupy a single line
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>
    /// Reads all lines of a file and deserializes them into a list of objects.
    /// Returns an empty list if the file does not exist.
    /// </summary>
    public static async Task<List<T>> ReadAllAsync<T>(
        string filePath,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return new List<T>();
        }

        var opt = options ?? DefaultOptions;
        var result = new List<T>();

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var item = JsonSerializer.Deserialize<T>(line, opt);
            if (item is not null)
            {
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// Reads objects line-by-line as an asynchronous stream (IAsyncEnumerable).
    /// Suitable for large files to avoid loading the entire dataset into memory.
    /// </summary>
    public static async IAsyncEnumerable<T> ReadLinesAsync<T>(
        string filePath,
        JsonSerializerOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            yield break;

        var opt = options ?? DefaultOptions;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var item = JsonSerializer.Deserialize<T>(line, opt);
            if (item is not null)
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Reads the last non-empty line of the file and deserializes it.
    /// Convenient for fetching the latest block or block header.
    /// </summary>
    public static async Task<T?> ReadLastAsync<T>(
        string filePath,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return default;

        var opt = options ?? DefaultOptions;
        string? lastLine = null;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lastLine = line;
            }
        }

        if (lastLine is null)
            return default;

        return JsonSerializer.Deserialize<T>(lastLine, opt);
    }

    /// <summary>
    /// Appends a single object as a new line to the end of the file.
    /// Automatically creates the parent directory if it does not exist.
    /// </summary>
    public static async Task AppendAsync<T>(
        string filePath,
        T item,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDirectoryExists(filePath);

        var opt = options ?? DefaultOptions;
        var json = SerializeToSingleLine(item, opt);

        await using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
    }

    /// <summary>
    /// Appends a collection of objects to the end of the file (one object per line).
    /// </summary>
    public static async Task AppendManyAsync<T>(
        string filePath,
        IEnumerable<T> items,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDirectoryExists(filePath);

        var opt = options ?? DefaultOptions;

        await using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);

        foreach (var item in items)
        {
            var json = SerializeToSingleLine(item, opt);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
    }

    /// <summary>
    /// Overwrites the file with new data line-by-line.
    /// Used when replacing chains during consensus resolution.
    /// </summary>
    public static async Task OverwriteAllAsync<T>(
        string filePath,
        IEnumerable<T> items,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        EnsureDirectoryExists(filePath);

        var opt = options ?? DefaultOptions;

        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);

        foreach (var item in items)
        {
            var json = SerializeToSingleLine(item, opt);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
        }
    }

    /// <summary>
    /// Counts the number of non-empty lines in the file without deserializing.
    /// Useful for quickly determining chain height.
    /// </summary>
    public static async Task<int> CountLinesAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
            return 0;

        int count = 0;
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                count++;
            }
        }

        return count;
    }

    private static string SerializeToSingleLine<T>(T item, JsonSerializerOptions options)
    {
        var json = JsonSerializer.Serialize(item, options);
        // Guard against inadvertent newline characters inside serialized JSON
        return json.Replace("\r", "").Replace("\n", "");
    }

    private static void EnsureDirectoryExists(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
