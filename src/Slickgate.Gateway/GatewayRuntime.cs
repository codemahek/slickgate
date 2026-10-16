using Slickgate.Core.Configuration;
using Slickgate.Core.Proxy;

namespace Slickgate.Gateway;

/// <summary>
/// Holds the live gateway state: file-based routes plus routes discovered from
/// Docker labels. Every change produces a new merged snapshot that is pushed to
/// YARP (hot reload) and announced to subscribers (certificates, ACME).
/// </summary>
public sealed class GatewayRuntime(DynamicProxyConfigProvider provider)
{
    private readonly object _gate = new();
    private GatewayConfig _fileConfig = new();
    private IReadOnlyList<RouteDefinition> _dockerRoutes = [];
    private GatewayConfig _effective = new();

    /// <summary>Raised after the effective configuration changes (outside the lock).</summary>
    public event Action<GatewayConfig>? Changed;

    public DynamicProxyConfigProvider Provider { get; } = provider;

    public string Revision { get; private set; } = "";

    public DateTimeOffset LoadedAt { get; private set; } = DateTimeOffset.UtcNow;

    public GatewayConfig Effective
    {
        get
        {
            lock (_gate)
            {
                return _effective;
            }
        }
    }

    public IReadOnlyList<RouteDefinition> FileRoutes
    {
        get
        {
            lock (_gate)
            {
                return _fileConfig.Routes;
            }
        }
    }

    public IReadOnlyList<RouteDefinition> DockerRoutes
    {
        get
        {
            lock (_gate)
            {
                return _dockerRoutes;
            }
        }
    }

    public void SetFileConfig(GatewayConfig config)
    {
        GatewayConfig effective;
        lock (_gate)
        {
            _fileConfig = config;
            effective = Recompute();
        }

        Changed?.Invoke(effective);
    }

    public void SetDockerRoutes(IReadOnlyList<RouteDefinition> routes)
    {
        GatewayConfig effective;
        lock (_gate)
        {
            _dockerRoutes = routes;
            effective = Recompute();
        }

        Changed?.Invoke(effective);
    }

    private GatewayConfig Recompute()
    {
        var merged = Merge(_fileConfig.Routes, _dockerRoutes);
        var effective = _fileConfig with { Routes = merged };

        var (routes, clusters) = YarpConfigMapper.Map(effective);
        Provider.Update(routes, clusters);

        _effective = effective;
        Revision = Guid.NewGuid().ToString("N");
        LoadedAt = DateTimeOffset.UtcNow;
        return effective;
    }

    /// <summary>Appends Docker routes, renaming any whose id collides with a file route.</summary>
    private static List<RouteDefinition> Merge(
        IReadOnlyList<RouteDefinition> fileRoutes,
        IReadOnlyList<RouteDefinition> dockerRoutes)
    {
        var merged = new List<RouteDefinition>(fileRoutes.Count + dockerRoutes.Count);
        merged.AddRange(fileRoutes);

        if (dockerRoutes.Count == 0)
        {
            return merged;
        }

        var ids = new HashSet<string>(fileRoutes.Select(static r => r.Id), StringComparer.Ordinal);
        foreach (var route in dockerRoutes)
        {
            var id = route.Id;
            var suffix = 2;
            while (!ids.Add(id))
            {
                id = $"{route.Id}-{suffix++}";
            }

            merged.Add(id == route.Id ? route : route with { Id = id });
        }

        return merged;
    }
}
