namespace Slickgate.Core.Configuration;

public enum TlsMode
{
    Off,
    Internal,
    Auto,
}

public sealed record GatewayConfig
{
    public ServerConfig Server { get; init; } = new();
    public AdminConfig Admin { get; init; } = new();
    public AcmeConfig Acme { get; init; } = new();
    public DockerConfig Docker { get; init; } = new();
    public TelemetryConfig Telemetry { get; init; } = new();
    public IReadOnlyList<RouteDefinition> Routes { get; init; } = [];
}

public sealed record ServerConfig
{
    public int HttpPort { get; init; } = 80;
    public int HttpsPort { get; init; } = 443;
    public bool RedirectHttpToHttps { get; init; } = true;
}

public sealed record AdminConfig
{
    public bool Enabled { get; init; } = true;
    public string? Token { get; init; }

    /// <summary>
    /// Origins allowed to call the admin API from a browser (the UI is deployed
    /// independently, so it is normally a different origin). Use "*" to allow any.
    /// </summary>
    public IReadOnlyList<string> CorsOrigins { get; init; } = [];
}

public sealed record AcmeConfig
{
    public bool Enabled { get; init; }
    public string Email { get; init; } = "";
    public string DirectoryUrl { get; init; } = "https://acme-v02.api.letsencrypt.org/directory";
    public string StorageDir { get; init; } = "./certs";
}

public sealed record DockerConfig
{
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = "unix:///var/run/docker.sock";
    public string Network { get; init; } = "slickgate";
    public string LabelPrefix { get; init; } = "slickgate";
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed record TelemetryConfig
{
    public string? OtlpEndpoint { get; init; }
}

public sealed record HealthCheckDefinition
{
    public string Path { get; init; } = "/health";
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
}

public sealed record RouteDefinition
{
    public string Id { get; init; } = "";
    public IReadOnlyList<string> Hosts { get; init; } = [];
    public string? PathPrefix { get; init; }
    public string? PathExact { get; init; }
    public IReadOnlyList<string> Upstreams { get; init; } = [];
    public string? StripPrefix { get; init; }
    public TlsMode Tls { get; init; } = TlsMode.Off;
    public IReadOnlyDictionary<string, string> RequestHeadersSet { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> RequestHeadersAppend { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> ResponseHeadersSet { get; init; } =
        new Dictionary<string, string>();
    public HealthCheckDefinition? HealthCheck { get; init; }
    public int? Order { get; init; }
}
