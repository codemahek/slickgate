using Slickgate.Core.Configuration;
using Slickgate.Core.Discovery;

namespace Slickgate.Gateway.Discovery;

/// <summary>
/// Polls the Docker Engine API for running containers carrying <c>slickgate.*</c>
/// labels and publishes the resulting routes (hot-reloaded by the runtime).
/// </summary>
public sealed partial class DockerDiscoveryService(
    GatewayRuntime runtime,
    ILogger<DockerDiscoveryService> logger) : BackgroundService
{
    private static readonly TimeSpan DisabledPoll = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DockerApiClient? client = null;
        string? clientEndpoint = null;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var docker = runtime.Effective.Docker;

                if (!docker.Enabled)
                {
                    if (client is not null)
                    {
                        client.Dispose();
                        client = null;
                        clientEndpoint = null;
                        runtime.SetDockerRoutes([]);
                        LogDockerDisabled(logger);
                    }

                    await DelayAsync(DisabledPoll, stoppingToken);
                    continue;
                }

                if (client is null || clientEndpoint != docker.Endpoint)
                {
                    client?.Dispose();
                    try
                    {
                        client = new DockerApiClient(docker.Endpoint);
                        clientEndpoint = docker.Endpoint;
                        LogWatching(logger, docker.Endpoint, docker.Network);
                    }
                    catch (Exception ex)
                    {
                        LogConnectFailed(logger, docker.Endpoint, ex);
                        client = null;
                        clientEndpoint = null;
                        await DelayAsync(docker.PollInterval, stoppingToken);
                        continue;
                    }
                }

                try
                {
                    var routes = await DiscoverRoutesAsync(client, docker, stoppingToken);
                    runtime.SetDockerRoutes(routes);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    LogPollFailed(logger, ex);
                }

                await DelayAsync(docker.PollInterval, stoppingToken);
            }
        }
        finally
        {
            client?.Dispose();
        }
    }

    private const int MaxConcurrentInspects = 8;

    private async Task<IReadOnlyList<RouteDefinition>> DiscoverRoutesAsync(
        DockerApiClient client,
        DockerConfig config,
        CancellationToken ct)
    {
        var containers = await client.ListRunningContainersAsync(ct);

        var candidates = containers
            .Where(c => c.Labels is not null && DockerLabelParser.IsEnabled(c.Labels, config.LabelPrefix))
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        // Inspect in parallel (bounded) but keep results in container order:
        // Task.WhenAll preserves the task array's order, so route priority stays
        // deterministic regardless of which inspect finishes first.
        using var gate = new SemaphoreSlim(MaxConcurrentInspects);
        var inspections = new Task<ContainerInspect?>[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            inspections[i] = InspectThrottledAsync(client, candidates[i].Id, gate, ct);
        }

        var results = await Task.WhenAll(inspections);

        var routes = new List<RouteDefinition>(results.Length);
        for (var i = 0; i < results.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            var container = candidates[i];
            var inspect = results[i];
            if (inspect?.State is { Running: false })
            {
                continue;
            }

            var labels = inspect?.Config?.Labels ?? container.Labels!;
            var name = (inspect?.Name is { Length: > 0 } n ? n : container.Names.FirstOrDefault())
                ?.TrimStart('/') ?? container.Id[..12];

            var containerPort = GetPort(labels, config.LabelPrefix);
            var facts = new ContainerFacts
            {
                ContainerId = container.Id,
                ContainerName = name,
                ContainerIp = inspect?.GetIp(config.Network),
                HostPort = inspect?.GetPublishedHostPort(containerPort),
            };

            try
            {
                var route = DockerLabelParser.Parse(labels, facts, config.LabelPrefix);
                if (route is not null)
                {
                    routes.Add(route);
                }
            }
            catch (SlickgateConfigException ex)
            {
                LogContainerSkipped(logger, name, ex.Message);
            }
        }

        return routes;
    }

    private static async Task<ContainerInspect?> InspectThrottledAsync(
        DockerApiClient client,
        string containerId,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            return await client.InspectAsync(containerId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private static int GetPort(IReadOnlyDictionary<string, string> labels, string labelPrefix)
    {
        var key = labelPrefix.TrimEnd('.') + ".port";
        foreach (var (k, v) in labels)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var port))
            {
                return port;
            }
        }

        return 80;
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        Task.Delay(delay == TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delay, ct);

    [LoggerMessage(Level = LogLevel.Information, Message = "Docker discovery disabled.")]
    private static partial void LogDockerDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Docker discovery watching {Endpoint} (network {Network}).")]
    private static partial void LogWatching(ILogger logger, string endpoint, string network);

    [LoggerMessage(Level = LogLevel.Error, Message = "Cannot connect to Docker endpoint {Endpoint}.")]
    private static partial void LogConnectFailed(ILogger logger, string endpoint, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Docker discovery poll failed; keeping previous routes.")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping container {Name}: {Error}")]
    private static partial void LogContainerSkipped(ILogger logger, string name, string error);
}
