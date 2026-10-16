using Slickgate.Core.Configuration;
using Slickgate.Core.Discovery;

namespace Slickgate.Gateway.Discovery;

/// <summary>
/// Polls the Docker Engine API for running containers carrying <c>slickgate.*</c>
/// labels and publishes the resulting routes (hot-reloaded by the runtime).
/// </summary>
public sealed class DockerDiscoveryService(
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
                        logger.LogInformation("Docker discovery disabled.");
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
                        logger.LogInformation("Docker discovery watching {Endpoint} (network {Network}).",
                            docker.Endpoint, docker.Network);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Cannot connect to Docker endpoint {Endpoint}.", docker.Endpoint);
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
                    logger.LogWarning(ex, "Docker discovery poll failed; keeping previous routes.");
                }

                await DelayAsync(docker.PollInterval, stoppingToken);
            }
        }
        finally
        {
            client?.Dispose();
        }
    }

    private async Task<IReadOnlyList<RouteDefinition>> DiscoverRoutesAsync(
        DockerApiClient client,
        DockerConfig config,
        CancellationToken ct)
    {
        var containers = await client.ListRunningContainersAsync(ct);
        var prefix = config.LabelPrefix.TrimEnd('.') + "." + "enable";

        var routes = new List<RouteDefinition>();
        foreach (var container in containers)
        {
            ct.ThrowIfCancellationRequested();

            if (container.Labels is null || !HasLabel(container.Labels, prefix))
            {
                continue;
            }

            var inspect = await client.InspectAsync(container.Id, ct);
            if (inspect?.State is { Running: false })
            {
                continue;
            }

            var labels = inspect?.Config?.Labels ?? container.Labels;
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
                logger.LogWarning("Skipping container {Name}: {Error}", name, ex.Message);
            }
        }

        return routes;
    }

    private static bool HasLabel(IReadOnlyDictionary<string, string> labels, string key)
    {
        foreach (var (k, v) in labels)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) &&
                v.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1")
            {
                return true;
            }
        }

        return false;
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
}
