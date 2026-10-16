using Slickgate.Core.Configuration;
using Slickgate.Core.Proxy;

namespace Slickgate.Gateway;

/// <summary>
/// Holds the live gateway state: file-based routes, admin-managed mapping routes,
/// and routes discovered from Docker labels. Every change produces a new merged
/// snapshot that is pushed to YARP (hot reload) and announced to subscribers
/// (certificates, ACME).
/// </summary>
public sealed class GatewayRuntime(DynamicProxyConfigProvider provider)
{
    private readonly object _gate = new();
    private GatewayConfig _fileConfig = new();
    private IReadOnlyList<RouteDefinition> _mappingRoutes = [];
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

    public IReadOnlyList<RouteDefinition> MappingRoutes
    {
        get
        {
            lock (_gate)
            {
                return _mappingRoutes;
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

    public void SetMappingRoutes(IReadOnlyList<RouteDefinition> routes)
    {
        GatewayConfig effective;
        lock (_gate)
        {
            _mappingRoutes = routes;
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
        var merged = Merge(_fileConfig.Routes, _mappingRoutes, _dockerRoutes);
        var effective = _fileConfig with { Routes = merged };

        var (routes, clusters) = YarpConfigMapper.Map(effective);
        Provider.Update(routes, clusters);

        _effective = effective;
        Revision = Guid.NewGuid().ToString("N");
        LoadedAt = DateTimeOffset.UtcNow;
        return effective;
    }

    /// <summary>
    /// Concatenates route sources in priority order, renaming any route whose id
    /// collides with an earlier one (first occurrence keeps the id).
    /// </summary>
    private static List<RouteDefinition> Merge(params IReadOnlyList<RouteDefinition>[] sources)
    {
        var merged = new List<RouteDefinition>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            foreach (var route in source)
            {
                var id = route.Id;
                var suffix = 2;
                while (!ids.Add(id))
                {
                    id = $"{route.Id}-{suffix++}";
                }

                merged.Add(id == route.Id ? route : route with { Id = id });
            }
        }

        return merged;
    }
}
