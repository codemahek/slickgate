using Slickgate.Core.Configuration;

namespace Slickgate.Core.Proxy;

/// <summary>
/// Selects the effective route for an incoming request using the same precedence
/// as YARP (lower <see cref="RouteDefinition.Order"/> first, otherwise declaration
/// order × 10). Host and path matching mirror YARP's defaults: host comparison is
/// case-insensitive, <c>*.example.com</c> matches sub-domains, and path prefixes
/// are segment-aware. Matching is allocation-free (spans only).
/// </summary>
public static class RouteMatcher
{
    public static RouteDefinition? Match(IReadOnlyList<RouteDefinition> routes, string? host, string? path)
    {
        var hostSpan = StripPort(host.AsSpan());
        var pathSpan = path.AsSpan();

        RouteDefinition? best = null;
        var bestOrder = int.MaxValue;

        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            if (!MatchesRoute(route, hostSpan, pathSpan))
                continue;

            var order = route.Order ?? i * 10;
            if (order >= bestOrder)
                continue;

            bestOrder = order;
            best = route;
        }

        return best;
    }

    public static bool MatchesHost(RouteDefinition route, string? host)
        => MatchesHostCore(route, StripPort(host.AsSpan()));

    public static bool MatchesPath(RouteDefinition route, string? path)
        => MatchesPathCore(route, path.AsSpan());

    private static bool MatchesRoute(RouteDefinition route, ReadOnlySpan<char> host, ReadOnlySpan<char> path)
        => MatchesHostCore(route, host) && MatchesPathCore(route, path);

    private static bool MatchesHostCore(RouteDefinition route, ReadOnlySpan<char> host)
    {
        if (route.Hosts.Count == 0)
            return true;

        for (var i = 0; i < route.Hosts.Count; i++)
        {
            if (HostMatches(route.Hosts[i], host))
                return true;
        }

        return false;
    }

    private static bool HostMatches(string pattern, ReadOnlySpan<char> host)
    {
        if (host.IsEmpty)
            return false;

        if (pattern.Length == 1 && pattern[0] == '*')
            return true;

        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
            return host.Equals(pattern.AsSpan(), StringComparison.OrdinalIgnoreCase);

        var suffix = pattern.AsSpan(1);
        return host.Length > suffix.Length
            && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPathCore(RouteDefinition route, ReadOnlySpan<char> path)
    {
        if (route.PathExact is not null && !NormalizedEquals(route.PathExact.AsSpan(), path))
            return false;

        if (route.PathPrefix is null)
            return true;

        var prefix = route.PathPrefix.AsSpan();
        return IsRoot(prefix) || NormalizedStartsWith(path, prefix);
    }

    private static bool NormalizedEquals(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
        => Body(a).Equals(Body(b), StringComparison.OrdinalIgnoreCase);

    private static bool NormalizedStartsWith(ReadOnlySpan<char> path, ReadOnlySpan<char> prefix)
    {
        var body = Body(path);
        var pre = Body(prefix);

        return body.Equals(pre, StringComparison.OrdinalIgnoreCase)
            || (body.Length > pre.Length
                && body[pre.Length] == '/'
                && body[..pre.Length].Equals(pre, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsRoot(ReadOnlySpan<char> prefix) => Body(prefix).IsEmpty;

    private static ReadOnlySpan<char> Body(ReadOnlySpan<char> value)
    {
        var start = 0;
        while (start < value.Length && value[start] == '/')
            start++;

        var end = value.Length;
        while (end > start && value[end - 1] == '/')
            end--;

        return value[start..end];
    }

    private static ReadOnlySpan<char> StripPort(ReadOnlySpan<char> host)
    {
        var colon = host.IndexOf(':');
        return colon >= 0 ? host[..colon] : host;
    }
}
