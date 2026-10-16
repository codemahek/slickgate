namespace Slickgate.Admin.Models;

public sealed record HealthResponse(
    string Status,
    string Revision,
    int Routes,
    long UptimeSeconds);

public sealed record RouteSummary(
    string Id,
    string Source,
    List<string> Hosts,
    string? PathPrefix,
    string? PathExact,
    List<string> Upstreams,
    string Tls,
    string? StripPrefix);

public sealed record RoutesResponse(int Count, List<RouteSummary> Routes);

public sealed record ReloadResponse(bool Success, string? Error);

public sealed record ErrorResponse(string Error);

public sealed record ServerView(int HttpPort, int HttpsPort, bool RedirectHttpToHttps);

public sealed record AdminView(bool Enabled, string? Token, List<string> CorsOrigins);

public sealed record AcmeView(bool Enabled, string Email, string DirectoryUrl, string StorageDir);

public sealed record DockerView(bool Enabled, string Endpoint, string Network, string LabelPrefix, string PollInterval);

public sealed record TelemetryView(string? OtlpEndpoint);

public sealed record ConfigResponse(
    string ConfigPath,
    string Revision,
    ServerView Server,
    AdminView Admin,
    AcmeView Acme,
    DockerView Docker,
    TelemetryView Telemetry);

public sealed record DockerPortView(int PrivatePort, int? PublicPort, string Type);

public sealed record HealthCheckMapping(string? Path, string? Interval, string? Timeout);

public sealed record RouteMapping(
    string? Id,
    List<string>? Hosts,
    string? PathPrefix,
    string? PathExact,
    List<string>? Upstreams,
    string? StripPrefix,
    string? Tls,
    Dictionary<string, string>? RequestHeadersSet,
    Dictionary<string, string>? ResponseHeadersSet,
    HealthCheckMapping? HealthCheck,
    int? Order);

public sealed record ContainerView(
    string Id,
    string Name,
    string Image,
    string State,
    List<DockerPortView> Ports,
    bool Enabled,
    string? RouteId,
    Dictionary<string, string> Labels,
    RouteMapping Suggested);

public sealed record ContainersResponse(
    bool DockerEnabled,
    string? Error,
    int Count,
    List<ContainerView> Containers);

public sealed record MappingsResponse(int Count, List<RouteMapping> Mappings);

public sealed record MappingResponse(bool Success, string? Error, RouteMapping? Mapping);
