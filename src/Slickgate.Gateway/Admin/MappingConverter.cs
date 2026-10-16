using Slickgate.Core.Configuration;

namespace Slickgate.Gateway.Admin;

/// <summary>Converts between the admin API DTO and the core route model.</summary>
internal static class MappingConverter
{
    public static RouteDefinition ToDefinition(RouteMapping mapping, string? fallbackId = null)
    {
        var id = !string.IsNullOrWhiteSpace(mapping.Id) ? mapping.Id!.Trim() : (fallbackId ?? "").Trim();

        return new RouteDefinition
        {
            Id = id,
            Hosts = mapping.Hosts is { Count: > 0 } hosts
                ? hosts.Select(static h => h.Trim()).ToList()
                : [],
            PathPrefix = Normalize(mapping.PathPrefix),
            PathExact = Normalize(mapping.PathExact),
            Upstreams = mapping.Upstreams is { Count: > 0 }
                ? mapping.Upstreams.Select(static u => u.Trim()).ToList()
                : [],
            StripPrefix = Normalize(mapping.StripPrefix),
            Tls = ParseTls(mapping.Tls),
            RequestHeadersSet = mapping.RequestHeadersSet ?? new Dictionary<string, string>(),
            ResponseHeadersSet = mapping.ResponseHeadersSet ?? new Dictionary<string, string>(),
            HealthCheck = mapping.HealthCheck is null
                ? null
                : new HealthCheckDefinition
                {
                    Path = string.IsNullOrWhiteSpace(mapping.HealthCheck.Path)
                        ? "/health"
                        : mapping.HealthCheck.Path!,
                    Interval = ParseDuration(
                        mapping.HealthCheck.Interval, TimeSpan.FromSeconds(10)),
                    Timeout = ParseDuration(
                        mapping.HealthCheck.Timeout, TimeSpan.FromSeconds(2)),
                },
            Order = mapping.Order,
        };
    }

    public static RouteMapping FromDefinition(RouteDefinition route) =>
        new(
            route.Id,
            route.Hosts.Count > 0 ? route.Hosts.ToList() : null,
            route.PathPrefix,
            route.PathExact,
            route.Upstreams.ToList(),
            route.StripPrefix,
            route.Tls == TlsMode.Off ? "off" : route.Tls.ToString().ToLowerInvariant(),
            route.RequestHeadersSet.Count > 0 ? new Dictionary<string, string>(route.RequestHeadersSet) : null,
            route.ResponseHeadersSet.Count > 0 ? new Dictionary<string, string>(route.ResponseHeadersSet) : null,
            route.HealthCheck is { } health
                ? new HealthCheckMapping(health.Path, FormatDuration(health.Interval), FormatDuration(health.Timeout))
                : null,
            route.Order);

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TlsMode ParseTls(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "off" or "false" or "no" => TlsMode.Off,
        "internal" => TlsMode.Internal,
        "auto" => TlsMode.Auto,
        _ => throw new SlickgateConfigException(
            $"Invalid tls mode '{value}' (expected off, internal, or auto)."),
    };

    private static TimeSpan ParseDuration(string? value, TimeSpan fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : YamlConfigLoader.ParseDuration(value);

    private static string FormatDuration(TimeSpan value) =>
        value.TotalSeconds == Math.Floor(value.TotalSeconds)
            ? value.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "s"
            : value.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "s";
}
