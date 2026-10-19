using Slickgate.Core.Configuration;
using Slickgate.Core.Proxy;

namespace Slickgate.Gateway;

/// <summary>
/// Holds the live gateway state: the routes loaded from the single config file.
/// Applying a new configuration swaps YARP's snapshot atomically (no locks on the
/// request path) and announces the change to subscribers (certificates, ACME).
/// </summary>
public sealed class GatewayRuntime(DynamicProxyConfigProvider provider)
{
    /// <summary>Raised after a new configuration has been applied.</summary>
    public event Action<GatewayConfig>? Changed;

    public DynamicProxyConfigProvider Provider { get; } = provider;

    public string Revision { get; private set; } = "";

    public DateTimeOffset LoadedAt { get; private set; } = DateTimeOffset.UtcNow;

    public GatewayConfig Effective { get; private set; } = new();

    public void SetFileConfig(GatewayConfig config)
    {
        var (routes, clusters) = YarpConfigMapper.Map(config);
        Provider.Update(routes, clusters);

        Effective = config;
        Revision = Guid.NewGuid().ToString("N");
        LoadedAt = DateTimeOffset.UtcNow;

        Changed?.Invoke(config);
    }
}
