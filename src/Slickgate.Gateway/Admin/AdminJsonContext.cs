using System.Text.Json;
using System.Text.Json.Serialization;
using Slickgate.Core.Configuration;

namespace Slickgate.Gateway.Admin;

public sealed record HealthResponse(
    string Status,
    string Revision,
    int Routes,
    long UptimeSeconds);

public sealed record RouteView(
    string Id,
    string Source,
    IReadOnlyList<string> Hosts,
    string? PathPrefix,
    string? PathExact,
    IReadOnlyList<string> Upstreams,
    string Tls,
    string? StripPrefix);

public sealed record RoutesResponse(int Count, IReadOnlyList<RouteView> Routes);

public sealed record ReloadResponse(bool Success, string? Error);

public sealed record ConfigResponse(
    string ConfigPath,
    string Revision,
    ServerConfig Server,
    AdminConfig Admin,
    AcmeConfig Acme,
    DockerConfig Docker,
    TelemetryConfig Telemetry);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(RoutesResponse))]
[JsonSerializable(typeof(ReloadResponse))]
[JsonSerializable(typeof(ConfigResponse))]
internal sealed partial class AdminJsonContext : JsonSerializerContext;
