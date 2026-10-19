using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Slickgate.Core.Configuration;
using Slickgate.Core.Proxy;
using Slickgate.Gateway;
using Slickgate.Gateway.Admin;
using Slickgate.Gateway.Certificates;
using Slickgate.Gateway.Config;
using Slickgate.Gateway.Discovery;
using Slickgate.Gateway.Http;
using Yarp.ReverseProxy.Configuration;

GatewayHostOptions hostOptions;
GatewayConfig initialConfig;
try
{
    hostOptions = ResolveHostOptions(args);
    initialConfig = YamlConfigLoader.LoadFile(hostOptions.ConfigPath);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"slickgate: {ex.Message}");
    return 1;
}

hostOptions.HttpPort = initialConfig.Server.HttpPort;
hostOptions.HttpsPort = initialConfig.Server.HttpsPort;

var proxyProvider = new DynamicProxyConfigProvider();
var runtime = new GatewayRuntime(proxyProvider);
var certificates = new CertificateManager(initialConfig.Acme.StorageDir);
runtime.Changed += certificates.UpdateRoutes;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
    console.UseUtcTimestamp = true;
});

if (builder.Configuration["Logging:LogLevel:Default"] is null)
{
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}

builder.Services.AddSingleton(hostOptions);
builder.Services.AddSingleton(runtime);
builder.Services.AddSingleton<IProxyConfigProvider>(proxyProvider);
builder.Services.AddSingleton(certificates);
builder.Services.AddSingleton<AcmeChallengeStore>();
builder.Services.AddSingleton<MappingsStore>();
builder.Services.AddSingleton<ConfigReloader>();
builder.Services.AddHostedService<ConfigFileWatcherService>();
builder.Services.AddHostedService<AcmeProvisioner>();
builder.Services.AddHostedService<DockerDiscoveryService>();
builder.Services.AddReverseProxy();

var otlpEndpoint = initialConfig.Telemetry.OtlpEndpoint;
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("slickgate"))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint)))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddOtlpExporter(exporter => exporter.Endpoint = new Uri(otlpEndpoint)));
}

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Match Caddy: no request body size limit (Kestrel defaults to ~28.6 MiB).
    kestrel.Limits.MaxRequestBodySize = null;

    if (hostOptions.HttpPort > 0)
    {
        kestrel.ListenAnyIP(hostOptions.HttpPort);
    }

    if (hostOptions.HttpsPort > 0)
    {
        kestrel.ListenAnyIP(hostOptions.HttpsPort, listen =>
        {
            listen.UseHttps(https =>
            {
                https.ServerCertificateSelector = (_, sniHost) => certificates.Select(sniHost);
            });
        });
    }
});

// Publish the initial routes before the host starts so the first request is routed.
runtime.SetFileConfig(initialConfig);
certificates.UpdateRoutes(initialConfig);

var app = builder.Build();

// Apply the admin-managed mappings overlay (if any) before serving.
app.Services.GetRequiredService<MappingsStore>().TryLoad(out _);

app.UseMiddleware<AcmeChallengeMiddleware>();
app.UseMiddleware<AdminCorsMiddleware>();
app.MapAdminApi();
app.UseMiddleware<HttpsRedirectMiddleware>();
app.UseMiddleware<RouteMiddleware>();
app.MapReverseProxy();

app.Logger.LogInformation(
    "Slickgate listening on {Http} (http) / {Https} (https) with {Routes} route(s) from {Config}.",
    hostOptions.HttpPort,
    hostOptions.HttpsPort,
    initialConfig.Routes.Count,
    hostOptions.ConfigPath);

app.Run();

return 0;

static GatewayHostOptions ResolveHostOptions(string[] args)
{
    string? path = null;
    string? mappings = null;

    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--config" && i + 1 < args.Length)
        {
            path = args[i + 1];
        }
        else if (args[i].StartsWith("--config=", StringComparison.Ordinal))
        {
            path = args[i]["--config=".Length..];
        }
        else if (args[i] == "--mappings" && i + 1 < args.Length)
        {
            mappings = args[i + 1];
        }
        else if (args[i].StartsWith("--mappings=", StringComparison.Ordinal))
        {
            mappings = args[i]["--mappings=".Length..];
        }
    }

    path ??= Environment.GetEnvironmentVariable("SLICKGATE_CONFIG");
    mappings ??= Environment.GetEnvironmentVariable("SLICKGATE_MAPPINGS");

    if (string.IsNullOrWhiteSpace(path))
    {
        string?[] candidates =
        [
            "gateway.yaml",
            Path.Combine("config", "gateway.yaml"),
            Path.Combine(AppContext.BaseDirectory, "gateway.yaml"),
            Path.Combine("/etc/slickgate", "gateway.yaml"),
        ];

        path = candidates.FirstOrDefault(candidate =>
            !string.IsNullOrEmpty(candidate) && File.Exists(candidate));
    }

    if (string.IsNullOrWhiteSpace(path))
    {
        throw new InvalidOperationException(
            "No config file found. Pass --config <path>, set SLICKGATE_CONFIG, " +
            "or place gateway.yaml in the working directory.");
    }

    mappings ??= Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
        "gateway.mappings.yaml");

    return new GatewayHostOptions { ConfigPath = path, MappingsPath = mappings };
}
