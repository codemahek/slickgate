namespace Slickgate.Gateway;

/// <summary>Host-level options that are not hot-reloadable (paths, ports).</summary>
public sealed class GatewayHostOptions
{
    public string ConfigPath { get; set; } = "";

    public int HttpPort { get; set; } = 8080;

    public int HttpsPort { get; set; } = 8443;
}
