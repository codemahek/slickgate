using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slickgate.Core.Discovery;

public sealed class ContainerListItem
{
    [JsonPropertyName("Id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("Names")]
    public string[] Names { get; set; } = [];

    [JsonPropertyName("Image")]
    public string Image { get; set; } = "";

    [JsonPropertyName("Labels")]
    public Dictionary<string, string>? Labels { get; set; }

    [JsonPropertyName("State")]
    public string State { get; set; } = "";

    [JsonPropertyName("Status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("Ports")]
    public DockerPort[] Ports { get; set; } = [];
}

public sealed class DockerPort
{
    [JsonPropertyName("IP")]
    public string? Ip { get; set; }

    [JsonPropertyName("PrivatePort")]
    public int PrivatePort { get; set; }

    [JsonPropertyName("PublicPort")]
    public int? PublicPort { get; set; }

    [JsonPropertyName("Type")]
    public string Type { get; set; } = "";
}

public sealed class ContainerInspect
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("Config")]
    public ContainerConfigInfo? Config { get; set; }

    [JsonPropertyName("NetworkSettings")]
    public NetworkSettingsInfo? NetworkSettings { get; set; }

    [JsonPropertyName("State")]
    public ContainerStateInfo? State { get; set; }

    /// <summary>Container IP on the given network (falls back to any network).</summary>
    public string? GetIp(string? preferredNetwork)
    {
        var networks = NetworkSettings?.Networks;
        if (networks is null || networks.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(preferredNetwork) &&
            networks.TryGetValue(preferredNetwork, out var preferred) &&
            !string.IsNullOrEmpty(preferred.IpAddress))
        {
            return preferred.IpAddress;
        }

        return networks.Values
            .Select(static n => n.IpAddress)
            .FirstOrDefault(static ip => !string.IsNullOrEmpty(ip));
    }

    /// <summary>First host-published port for a container port (e.g. "80/tcp").</summary>
    public int? GetPublishedHostPort(int containerPort)
    {
        var ports = NetworkSettings?.Ports;
        if (ports is null)
        {
            return null;
        }

        if (!ports.TryGetValue($"{containerPort}/tcp", out var bindings) || bindings is null)
        {
            return null;
        }

        return bindings
            .Select(static b => int.TryParse(b.HostPort, out var hp) ? hp : (int?)null)
            .FirstOrDefault(static hp => hp is not null);
    }
}

public sealed class ContainerConfigInfo
{
    [JsonPropertyName("Labels")]
    public Dictionary<string, string>? Labels { get; set; }
}

public sealed class NetworkSettingsInfo
{
    [JsonPropertyName("Networks")]
    public Dictionary<string, NetworkAttachmentInfo>? Networks { get; set; }

    [JsonPropertyName("Ports")]
    public Dictionary<string, PortBinding[]>? Ports { get; set; }
}

public sealed class NetworkAttachmentInfo
{
    [JsonPropertyName("IPAddress")]
    public string? IpAddress { get; set; }

    [JsonPropertyName("Gateway")]
    public string? Gateway { get; set; }
}

public sealed class PortBinding
{
    [JsonPropertyName("HostIp")]
    public string? HostIp { get; set; }

    [JsonPropertyName("HostPort")]
    public string? HostPort { get; set; }
}

public sealed class ContainerStateInfo
{
    [JsonPropertyName("Running")]
    public bool Running { get; set; }

    [JsonPropertyName("Status")]
    public string Status { get; set; } = "";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ContainerListItem[]))]
[JsonSerializable(typeof(ContainerInspect))]
internal sealed partial class DockerJsonContext : JsonSerializerContext;
