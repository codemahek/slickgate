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
    TelemetryConfig Telemetry);

public sealed record RawConfigResponse(string Path, string Yaml);

public sealed record RawConfigRequest(string Yaml);

public sealed record ConfigActionResponse(bool Success, string? Error);

public sealed record ErrorResponse(string Error);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(RoutesResponse))]
[JsonSerializable(typeof(ReloadResponse))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(RawConfigResponse))]
[JsonSerializable(typeof(RawConfigRequest))]
[JsonSerializable(typeof(ConfigActionResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class AdminJsonContext : JsonSerializerContext;
