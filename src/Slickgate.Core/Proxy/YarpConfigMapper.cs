using Yarp.ReverseProxy.Configuration;

namespace Slickgate.Core.Proxy;

using Slickgate.Core.Configuration;

/// <summary>
/// Translates Slickgate's <see cref="GatewayConfig"/> route definitions into YARP
/// route/cluster snapshots consumed by <see cref="DynamicProxyConfigProvider"/>.
/// </summary>
public static class YarpConfigMapper
{
    public static (IReadOnlyList<RouteConfig> Routes, IReadOnlyList<ClusterConfig> Clusters) Map(
        GatewayConfig config)
    {
        var routes = new List<RouteConfig>(config.Routes.Count);
        var clusters = new List<ClusterConfig>(config.Routes.Count);

        for (var i = 0; i < config.Routes.Count; i++)
        {
            var route = config.Routes[i];

            // Static routes are served by the Slickgate route middleware, not YARP.
            // Keep the original index so ordering relative to proxied routes is stable.
            if (route.StaticRoot is not null)
            {
                continue;
            }

            routes.Add(MapRoute(route, i));
            clusters.Add(MapCluster(route));
        }

        return (routes, clusters);
    }

    private static RouteConfig MapRoute(RouteDefinition route, int index)
    {
        var transforms = new List<Dictionary<string, string>>();

        if (!string.IsNullOrEmpty(route.StripPrefix))
        {
            transforms.Add(new Dictionary<string, string>
            {
                ["PathRemovePrefix"] = NormalizePrefix(route.StripPrefix),
            });
        }

        foreach (var (name, value) in route.RequestHeadersSet)
        {
            transforms.Add(new Dictionary<string, string>
            {
                ["RequestHeader"] = name,
                ["Set"] = value,
            });
        }

        foreach (var (name, value) in route.RequestHeadersAppend)
        {
            transforms.Add(new Dictionary<string, string>
            {
                ["RequestHeader"] = name,
                ["Append"] = value,
            });
        }

        foreach (var (name, value) in route.ResponseHeadersSet)
        {
            transforms.Add(new Dictionary<string, string>
            {
                ["ResponseHeader"] = name,
                ["Set"] = value,
            });
        }

        return new RouteConfig
        {
            RouteId = route.Id,
            ClusterId = route.Id,
            Order = route.Order ?? index * 10,
            Match = new RouteMatch
            {
                Hosts = route.Hosts.Count > 0 ? route.Hosts : null,
                Path = BuildPathTemplate(route),
            },
            Transforms = transforms,
        };
    }

    /// <summary>
    /// Builds an ASP.NET route template: exact paths match literally, prefixes become
    /// "{**catch-all}" patterns ("/" matches everything).
    /// </summary>
    private static string? BuildPathTemplate(RouteDefinition route)
    {
        if (route.PathExact is not null)
        {
            return route.PathExact.StartsWith('/') ? route.PathExact : "/" + route.PathExact;
        }

        if (route.PathPrefix is null)
        {
            return null;
        }

        var prefix = NormalizePrefix(route.PathPrefix);
        return prefix == "/" ? "/{**catch-all}" : $"{prefix}/{{**catch-all}}";
    }

    private static string NormalizePrefix(string prefix)
    {
        if (!prefix.StartsWith('/'))
        {
            prefix = "/" + prefix;
        }

        return prefix.Length > 1 ? prefix.TrimEnd('/') : prefix;
    }

    private static ClusterConfig MapCluster(RouteDefinition route)
    {
        var destinations = new Dictionary<string, DestinationConfig>(StringComparer.Ordinal);
        for (var i = 0; i < route.Upstreams.Count; i++)
        {
            destinations[i.ToString()] = new DestinationConfig { Address = route.Upstreams[i] };
        }

        return new ClusterConfig
        {
            ClusterId = route.Id,
            LoadBalancingPolicy = "RoundRobin",
            HealthCheck = route.HealthCheck is { } health
                ? new HealthCheckConfig
                {
                    Active = new ActiveHealthCheckConfig
                    {
                        Enabled = true,
                        Interval = health.Interval,
                        Timeout = health.Timeout,
                        Path = health.Path,
                        Policy = "ConsecutiveFailures",
                    },
                }
                : null,
            Destinations = destinations,
        };
    }
}
