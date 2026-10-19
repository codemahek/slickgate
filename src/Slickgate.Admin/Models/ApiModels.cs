namespace Slickgate.Admin.Models;

public sealed record HealthResponse(
    string Status,
    string Revision,
    int Routes,
    long UptimeSeconds);

public sealed record RouteSummary(
    string Id,
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

public sealed record TelemetryView(string? OtlpEndpoint);

public sealed record ConfigResponse(
    string ConfigPath,
    string Revision,
    ServerView Server,
    AdminView Admin,
    AcmeView Acme,
    TelemetryView Telemetry);

public sealed record RawConfigResponse(string Path, string Yaml);

public sealed record RawConfigRequest(string Yaml);

public sealed record ConfigActionResponse(bool Success, string? Error);
