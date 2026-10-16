using Slickgate.Core.Configuration;

namespace Slickgate.Core.Discovery;

/// <summary>
/// Facts about a running container used to build a route.
/// </summary>
public sealed record ContainerFacts
{
    public string ContainerId { get; init; } = "";
    public string ContainerName { get; init; } = "";

    /// <summary>Container IP on the gateway's Docker network (null for host networking).</summary>
    public string? ContainerIp { get; init; }

    /// <summary>Host-published port, if any (used as fallback when no container IP).</summary>
    public int? HostPort { get; init; }
}

/// <summary>
/// Parses Caddy-style Docker labels into a Slickgate route definition.
/// Recognized labels (with the configured prefix, default "slickgate"):
/// <c>enable</c>, <c>id</c>, <c>host(s)</c>, <c>port</c>, <c>path</c>,
/// <c>tls</c>, <c>strip_prefix</c>, <c>health_path</c>, <c>order</c>.
/// </summary>
public static class DockerLabelParser
{
    public static RouteDefinition? Parse(
        IReadOnlyDictionary<string, string> labels,
        ContainerFacts facts,
        string labelPrefix = "slickgate")
    {
        var prefix = labelPrefix.TrimEnd('.') + ".";

        if (!TryGet(labels, prefix + "enable", out var enabled) ||
            !IsTruthy(enabled))
        {
            return null;
        }

        if (!TryGet(labels, prefix + "host", out var host) &&
            !TryGet(labels, prefix + "hosts", out host))
        {
            host = null;
        }

        var hosts = SplitList(host);
        var pathPrefix = TryGet(labels, prefix + "path", out var path) && !string.IsNullOrWhiteSpace(path)
            ? NormalizePath(path)
            : "/";

        if (hosts.Count == 0)
        {
            // Fall back to container name as a hostname so the container is
            // reachable as e.g. "whoami.localhost" without extra labels.
            hosts = [facts.ContainerName.TrimStart('/')];
        }

        var port = TryGet(labels, prefix + "port", out var portValue) && int.TryParse(portValue, out var p)
            ? p
            : 80;

        var upstream = BuildUpstreamUrl(facts, port);
        if (upstream is null)
        {
            return null;
        }

        var id = TryGet(labels, prefix + "id", out var explicitId) && !string.IsNullOrWhiteSpace(explicitId)
            ? explicitId
            : Slug(facts.ContainerName.TrimStart('/'));

        var stripPrefix = ParseStripPrefix(labels, prefix, pathPrefix);
        var tls = ParseTls(labels, prefix);

        return new RouteDefinition
        {
            Id = id,
            Hosts = hosts,
            PathPrefix = pathPrefix,
            Upstreams = [upstream],
            StripPrefix = stripPrefix,
            Tls = tls,
            HealthCheck = TryGet(labels, prefix + "health_path", out var healthPath) &&
                          !string.IsNullOrWhiteSpace(healthPath)
                ? new HealthCheckDefinition { Path = NormalizePath(healthPath) }
                : null,
            Order = TryGet(labels, prefix + "order", out var orderValue) && int.TryParse(orderValue, out var order)
                ? order
                : null,
        };
    }

    internal static string? BuildUpstreamUrl(ContainerFacts facts, int port)
    {
        if (!string.IsNullOrEmpty(facts.ContainerIp))
        {
            return $"http://{facts.ContainerIp}:{port}";
        }

        if (facts.HostPort is { } hostPort)
        {
            return $"http://127.0.0.1:{hostPort}";
        }

        return null;
    }

    private static string? ParseStripPrefix(
        IReadOnlyDictionary<string, string> labels,
        string prefix,
        string pathPrefix)
    {
        if (!TryGet(labels, prefix + "strip_prefix", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" => pathPrefix == "/" ? null : pathPrefix,
            "false" or "no" or "off" => null,
            _ => NormalizePath(value),
        };
    }

    private static TlsMode ParseTls(IReadOnlyDictionary<string, string> labels, string prefix)
    {
        if (!TryGet(labels, prefix + "tls", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return TlsMode.Off;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => TlsMode.Auto,
            "internal" => TlsMode.Internal,
            "off" or "false" or "no" => TlsMode.Off,
            _ => throw new SlickgateConfigException(
                $"Invalid {prefix}tls label value '{value}' (expected off, internal, or auto)."),
        };
    }

    private static bool IsTruthy(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1";

    private static bool TryGet(IReadOnlyDictionary<string, string> labels, string key, out string value)
    {
        if (labels.TryGetValue(key, out var found) && found is not null)
        {
            value = found;
            return true;
        }

        // Docker labels are case-sensitive; retry with lowercase key.
        foreach (var (k, v) in labels)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }

        value = "";
        return false;
    }

    private static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

    private static string NormalizePath(string path) =>
        !path.StartsWith('/') ? "/" + path : path;

    private static string Slug(string name)
    {
        var chars = name.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')
            .ToArray();
        var slug = new string(chars);
        return string.IsNullOrWhiteSpace(slug) ? "route" : slug.Trim('-');
    }
}
