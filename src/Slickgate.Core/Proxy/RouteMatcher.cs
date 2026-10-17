using Slickgate.Core.Configuration;

namespace Slickgate.Core.Proxy;

/// <summary>
/// Selects the effective route for an incoming request using the same precedence
/// as YARP (lower <see cref="RouteDefinition.Order"/> first, otherwise declaration
/// order × 10). Host and path matching mirror YARP's defaults: host comparison is
/// case-insensitive, <c>*.example.com</c> matches sub-domains, and path prefixes
/// are segment-aware.
/// </summary>
public static class RouteMatcher
{
    public static RouteDefinition? Match(IReadOnlyList<RouteDefinition> routes, string? host, string path)
    {
        RouteDefinition? best = null;
        var bestOrder = int.MaxValue;

        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            if (!Matches(route, host, path))
                continue;

            var order = route.Order ?? i * 10;
            if (order >= bestOrder) 
                continue;

            bestOrder = order;
            best = route;
        }

        return best;
    }

    public static bool Matches(RouteDefinition route, string? host, string path)
    {
        var normalizedPath = string.IsNullOrEmpty(path) ? "/" : path;

        return MatchesHost(route, host)
            && MatchesPath(route, normalizedPath);
    }

    public static bool MatchesHost(RouteDefinition route, string? host)
    {
        if (route.Hosts.Count == 0)
        {
            return true;
        }

        foreach (var pattern in route.Hosts)
        {
            if (HostMatches(pattern, host))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HostMatches(string pattern, string? host)
    {
        // Strip a port from the incoming host (Host header may be "name:port").
        if (host is not null)
        {
            var colon = host.IndexOf(':');
            if (colon >= 0)
            {
                host = host[..colon];
            }
        }

        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (pattern == "*")
        {
            return true;
        }

        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
            return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);

        var suffix = pattern[1..]; // ".example.com"
        return host.Length > suffix.Length
               && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);

    }

    public static bool MatchesPath(RouteDefinition route, string path)
    {
        if (route.PathExact is not null)
        {
            var exact = NormalizePrefix(route.PathExact);
            if (!string.Equals(exact, NormalizePrefix(path), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (route.PathPrefix is null) 
            return true;

        var prefix = NormalizePrefix(route.PathPrefix);
        if (prefix == "/") 
            return true;

        var normalizedPath = NormalizePrefix(path);
        return string.Equals(normalizedPath, prefix, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePrefix(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "/";

        if (!value.StartsWith('/')) 
            value = "/" + value;

        return value.Length > 1 ? value.TrimEnd('/') : value;
    }
}
