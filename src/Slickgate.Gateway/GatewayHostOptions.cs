namespace Slickgate.Gateway;

/// <summary>Host-level options that are not hot-reloadable (paths, ports).</summary>
public sealed class GatewayHostOptions
{
    public string ConfigPath { get; set; } = "";

    /// <summary>Overlay file for admin-managed mappings (derived from ConfigPath by default).</summary>
    public string MappingsPath { get; set; } = "";

    public int HttpPort { get; set; } = 8080;

    public int HttpsPort { get; set; } = 8443;
}
