using System.Net.Http.Json;
using System.Text.Json;
using Blockchain.Shared.Common;
using Blockchain.Shared.Dtos;
using Blockchain.Shared.Models;

namespace WalletApp.Services;

/// <summary>
/// Client for interacting with blockchain network miner nodes.
/// Node endpoints are loaded dynamically from a JSON Lines configuration file.
/// </summary>
public class NodeClient
{
    public string NodesFilePath { get; set; } = "data/wallet_nodes.json";
    private readonly HttpClient _httpClient;

    public class NodeConfig
    {
        public string Url { get; set; } = string.Empty;

        public NodeConfig() { }
        public NodeConfig(string url) => Url = url;
    }

    public NodeClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    }

    /// <summary>
    /// Loads miner node URLs from data/wallet_nodes.json.
    /// If the file does not exist, initializes it with default local ports (8081, 8082, 8083).
    /// </summary>
    public async Task<List<string>> GetNodeUrlsAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(NodesFilePath))
        {
            var defaultNodes = new List<NodeConfig>
            {
                new("http://localhost:8081"),
                new("http://localhost:8082"),
                new("http://localhost:8083")
            };
            await JsonLinesHelper.OverwriteAllAsync(NodesFilePath, defaultNodes, cancellationToken: cancellationToken);
            return defaultNodes.Select(n => n.Url).ToList();
        }

        var nodes = await JsonLinesHelper.ReadAllAsync<NodeConfig>(NodesFilePath, cancellationToken: cancellationToken);
        return nodes.Select(n => n.Url.TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Adds a new node URL to the configuration file.
    /// </summary>
    public async Task<bool> AddNodeUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        var cleanUrl = url.Trim().TrimEnd('/');
        var existing = await GetNodeUrlsAsync(cancellationToken);
        if (existing.Contains(cleanUrl, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        await JsonLinesHelper.AppendAsync(NodesFilePath, new NodeConfig(cleanUrl), cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>
    /// Requests current account state (confirmed balance and Nonce) from a node: GET /accounts/{address}.
    /// </summary>
    public async Task<(bool Success, AccountStateDto? State, string Message)> FetchAccountStateAsync(
        string nodeUrl,
        string address,
        CancellationToken cancellationToken = default)
    {
        var cleanUrl = nodeUrl.TrimEnd('/');
        var requestUrl = $"{cleanUrl}/accounts/{address}";

        try
        {
            var response = await _httpClient.GetAsync(requestUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var state = await response.Content.ReadFromJsonAsync<AccountStateDto>(cancellationToken: cancellationToken);
                return (true, state, "Successfully retrieved account state from node.");
            }

            if ((int)response.StatusCode == 501)
            {
                return (false, null, $"Node {cleanUrl} returned 501 Not Implemented (endpoint /accounts/{{address}} is not implemented yet on this miner).");
            }

            return (false, null, $"Node {cleanUrl} returned status code {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }
        catch (Exception ex)
        {
            return (false, null, $"Failed to connect to node {cleanUrl}: {ex.Message}");
        }
    }

    /// <summary>
    /// Broadcasts a signed transaction to a network node: POST /transactions.
    /// </summary>
    public async Task<(bool Success, string Message)> BroadcastTransactionAsync(
        string nodeUrl,
        Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        var cleanUrl = nodeUrl.TrimEnd('/');
        var requestUrl = $"{cleanUrl}/transactions";

        try
        {
            var response = await _httpClient.PostAsJsonAsync(requestUrl, transaction, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return (true, $"Transaction successfully accepted by node {cleanUrl}.");
            }

            if ((int)response.StatusCode == 501)
            {
                return (false, $"Node {cleanUrl} returned 501 Not Implemented (endpoint POST /transactions is not implemented yet on this miner).");
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return (false, $"Node {cleanUrl} returned error {(int)response.StatusCode}: {errorBody}");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to broadcast to node {cleanUrl}: {ex.Message}");
        }
    }
}
