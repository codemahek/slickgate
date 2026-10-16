using Slickgate.Core.Configuration;
using Slickgate.Core.Discovery;
using Xunit;

namespace Slickgate.Core.Tests;

public class DockerLabelParserTests
{
    private static ContainerFacts Facts(
        string name = "whoami",
        string? ip = "172.20.0.5",
        int? hostPort = null) => new()
        {
            ContainerId = "abc123",
            ContainerName = name,
            ContainerIp = ip,
            HostPort = hostPort,
        };

    private static Dictionary<string, string> Labels(params (string Key, string Value)[] pairs)
    {
        var labels = new Dictionary<string, string>();
        foreach (var (key, value) in pairs)
        {
            labels[key] = value;
        }

        return labels;
    }

    [Fact]
    public void Parse_WithoutEnableLabel_ReturnsNull()
    {
        var route = DockerLabelParser.Parse(
            Labels(("slickgate.host", "whoami.local")),
            Facts());

        Assert.Null(route);
    }

    [Fact]
    public void Parse_Disabled_ReturnsNull()
    {
        var route = DockerLabelParser.Parse(
            Labels(("slickgate.enable", "false"), ("slickgate.host", "whoami.local")),
            Facts());

        Assert.Null(route);
    }

    [Fact]
    public void Parse_MinimalLabels_UsesContainerNameAsHost()
    {
        var route = DockerLabelParser.Parse(
            Labels(("slickgate.enable", "true")),
            Facts(name: "whoami"));

        Assert.NotNull(route);
        Assert.Equal("whoami", route.Id);
        Assert.Equal(["whoami"], route.Hosts);
        Assert.Equal("/", route.PathPrefix);
        Assert.Equal(["http://172.20.0.5:80"], route.Upstreams);
        Assert.Equal(TlsMode.Off, route.Tls);
        Assert.Null(route.StripPrefix);
    }

    [Fact]
    public void Parse_MultipleHosts_AreSplit()
    {
        var route = DockerLabelParser.Parse(
            Labels(("slickgate.enable", "true"), ("slickgate.hosts", "a.local, b.local")),
            Facts());

        Assert.Equal(["a.local", "b.local"], route!.Hosts);
    }

    [Fact]
    public void Parse_PortAndExplicitId_AreHonored()
    {
        var route = DockerLabelParser.Parse(
            Labels(("slickgate.enable", "1"), ("slickgate.port", "3000"), ("slickgate.id", "custom")),
            Facts());

        Assert.Equal("custom", route!.Id);
        Assert.Equal(["http://172.20.0.5:3000"], route.Upstreams);
    }

    [Fact]
    public void Parse_PathPrefix_IsNormalized()
    {
        var route = DockerLabelParser.Parse(
            Labels(("slickgate.enable", "true"), ("slickgate.path", "api")),
            Facts());

        Assert.Equal("/api", route!.PathPrefix);
    }

    [Fact]
    public void Parse_TlsModes_AreParsed()
    {
        Assert.Equal(TlsMode.Auto, DockerLabelParser.Parse(
            Labels(("slickgate.enable", "true"), ("slickgate.tls", "auto")), Facts())!.Tls);

        Assert.Equal(TlsMode.Internal, DockerLabelParser.Parse(
            Labels(("slickgate.enable", "true"), ("slickgate.tls", "internal")), Facts())!.Tls);
    }

    [Fact]
    public void Parse_InvalidTls_Throws()
    {
        Assert.Throws<SlickgateConfigException>(() => DockerLabelParser.Parse(
            Labels(("slickgate.enable", "true"), ("slickgate.tls", "wat")), Facts()));
    }

    [Fact]
    public void Parse_StripPrefixTrue_UsesPathPrefix()
    {
        var route = DockerLabelParser.Parse(
            Labels(
                ("slickgate.enable", "true"),
                ("slickgate.path", "/api"),
                ("slickgate.strip_prefix", "true")),
            Facts());

        Assert.Equal("/api", route!.StripPrefix);
    }

    [Fact]
    public void Parse_StripPrefixPath_IsNormalized()
    {
        var route = DockerLabelParser.Parse(
            Labels(("slickgate.enable", "true"), ("slickgate.strip_prefix", "api")),
            Facts());

        Assert.Equal("/api", route!.StripPrefix);
    }

    [Fact]
    public void Parse_StripPrefixFalse_IsNull()
    {
        var route = DockerLabelParser.Parse(
            Labels(("slickgate.enable", "true"), ("slickgate.strip_prefix", "false")),
            Facts());

        Assert.Null(route!.StripPrefix);
    }

    [Fact]
    public void Parse_NoContainerIp_FallsBackToPublishedHostPort()
    {
        var route = DockerLabelParser.Parse(
            Labels(("slickgate.enable", "true"), ("slickgate.port", "80")),
            Facts(ip: null, hostPort: 32768));

        Assert.Equal(["http://127.0.0.1:32768"], route!.Upstreams);
    }

    [Fact]
    public void Parse_NoAddressAvailable_ReturnsNull()
    {
        var route = DockerLabelParser.Parse(
            Labels(("slickgate.enable", "true")),
            Facts(ip: null, hostPort: null));

        Assert.Null(route);
    }

    [Fact]
    public void Parse_HealthAndOrderLabels_AreHonored()
    {
        var route = DockerLabelParser.Parse(
            Labels(
                ("slickgate.enable", "true"),
                ("slickgate.health_path", "healthz"),
                ("slickgate.order", "5")),
            Facts());

        Assert.Equal("/healthz", route!.HealthCheck!.Path);
        Assert.Equal(5, route.Order);
    }

    [Fact]
    public void Parse_CustomPrefix_IsSupported()
    {
        var route = DockerLabelParser.Parse(
            Labels(("edge.enable", "true"), ("edge.host", "x.local")),
            Facts(),
            labelPrefix: "edge");

        Assert.Equal(["x.local"], route!.Hosts);
    }
}
