using System.Diagnostics;
using System.Net.Http.Json;
using Blockchain.Shared.Common;
using Blockchain.Shared.Models;
using Miner.Common;

namespace Miner.Services;

/// <summary>
/// Background service that periodically polls trusted nodes
/// to retrieve their trusted peer lists (/node/peers) and evaluate their reachability.
/// </summary>
public class PeerDiscoveryService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PeerDiscoveryService> _logger;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(15);

    public PeerDiscoveryService(IHttpClientFactory httpClientFactory, ILogger<PeerDiscoveryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PeerDiscoveryService started.");

        // Short initial delay so other network nodes have time to spin up
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollPeersAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error occurred during background peer polling.");
            }

            await Task.Delay(_pollInterval, stoppingToken);
        }

        _logger.LogInformation("PeerDiscoveryService stopped.");
    }

    private async Task PollPeersAsync(CancellationToken cancellationToken)
    {
        var trustedNodes = await NodeManager.GetTrustedNodesAsync(cancellationToken: cancellationToken);
        if (trustedNodes.Count == 0)
        {
            return;
        }

        var client = _httpClientFactory.CreateClient("PeerClient");

        foreach (var node in trustedNodes)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var url = $"http://{node.Host}:{node.Port}/node/peers";
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(3)); // 3-second timeout for peer response

                var response = await client.GetAsync(url, cts.Token);
                stopwatch.Stop();

                if (response.IsSuccessStatusCode)
                {
                    var peers = await response.Content.ReadFromJsonAsync<List<TrustedNode>>(cancellationToken: cts.Token);

                    _logger.LogInformation(
                        "Successfully polled node {Host}:{Port} ({Elapsed}ms). Received trusted peers: {Count}.",
                        node.Host, node.Port, stopwatch.ElapsedMilliseconds, peers?.Count ?? 0);

                    // Record success and update response latency
                    await NodeManager.UpdateNodeMetricsAsync(
                        node.Host, node.Port, success: true, latencyMs: stopwatch.ElapsedMilliseconds, cancellationToken: cancellationToken);

                    // Further actions with received peers are intentionally not implemented per requirements
                }
                else
                {
                    _logger.LogWarning(
                        "Node {Host}:{Port} returned status code {StatusCode}.",
                        node.Host, node.Port, (int)response.StatusCode);

                    // Record failure without banning the node
                    await NodeManager.UpdateNodeMetricsAsync(
                        node.Host, node.Port, success: false, cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                _logger.LogWarning(
                    "Node {Host}:{Port} is unreachable: {Message}",
                    node.Host, node.Port, ex.Message);

                // Record failure (increments ConsecutiveFailures and lowers reputation)
                await NodeManager.UpdateNodeMetricsAsync(
                    node.Host, node.Port, success: false, cancellationToken: cancellationToken);
            }
        }
    }
}
