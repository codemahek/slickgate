using Slickgate.Core.Configuration;
using Xunit;

namespace Slickgate.Core.Tests;

public class RouteYamlWriterTests
{
    private static RouteDefinition Sample() => new()
    {
        Id = "users",
        Hosts = ["users.localhost", "api.localhost"],
        PathPrefix = "/api",
        Upstreams = ["http://users:8080", "http://users-b:8080"],
        StripPrefix = "/api",
        Tls = TlsMode.Internal,
        RequestHeadersSet = new Dictionary<string, string> { ["X-Gateway"] = "slickgate" },
        ResponseHeadersSet = new Dictionary<string, string> { ["X-Powered-By"] = "slickgate" },
        HealthCheck = new HealthCheckDefinition
        {
            Path = "/healthz",
            Interval = TimeSpan.FromSeconds(10),
            Timeout = TimeSpan.FromMilliseconds(1500),
        },
        Order = 5,
    };

    [Fact]
    public void Empty_WritesEmptySequence()
    {
        var yaml = RouteYamlWriter.Write([]);

        Assert.Contains("routes: []", yaml);
        Assert.Empty(YamlConfigLoader.LoadRouteList(yaml));
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = Sample();

        var yaml = RouteYamlWriter.Write([original]);
        var loaded = YamlConfigLoader.LoadRouteList(yaml);

        var route = Assert.Single(loaded);
        Assert.Equal(original.Id, route.Id);
        Assert.Equal(original.Hosts, route.Hosts);
        Assert.Equal(original.PathPrefix, route.PathPrefix);
        Assert.Equal(original.Upstreams, route.Upstreams);
        Assert.Equal(original.StripPrefix, route.StripPrefix);
        Assert.Equal(original.Tls, route.Tls);
        Assert.Equal(original.Order, route.Order);
        Assert.Equal(original.RequestHeadersSet["X-Gateway"], route.RequestHeadersSet["X-Gateway"]);
        Assert.Equal(original.ResponseHeadersSet["X-Powered-By"], route.ResponseHeadersSet["X-Powered-By"]);
        Assert.Equal(original.HealthCheck!.Path, route.HealthCheck!.Path);
        Assert.Equal(original.HealthCheck.Interval, route.HealthCheck.Interval);
        Assert.Equal(original.HealthCheck.Timeout, route.HealthCheck.Timeout);
    }

    [Fact]
    public void RoundTrip_QuotesUnsafeScalars()
    {
        var route = new RouteDefinition
        {
            Id = "odd id",
            Hosts = ["host with space"],
            Upstreams = ["http://127.0.0.1:8080"],
            PathPrefix = "/a b",
            RequestHeadersSet = new Dictionary<string, string> { ["X-Note"] = "a: b \"c\"" },
        };

        var yaml = RouteYamlWriter.Write([route]);
        var loaded = Assert.Single(YamlConfigLoader.LoadRouteList(yaml));

        Assert.Equal("odd id", loaded.Id);
        Assert.Equal(["host with space"], loaded.Hosts);
        Assert.Equal("/a b", loaded.PathPrefix);
        Assert.Equal("a: b \"c\"", loaded.RequestHeadersSet["X-Note"]);
    }

    [Fact]
    public void RoundTrip_OmittedOptionalFields()
    {
        var route = new RouteDefinition
        {
            Id = "plain",
            Hosts = ["plain.localhost"],
            PathPrefix = "/",
            Upstreams = ["http://plain:80"],
        };

        var yaml = RouteYamlWriter.Write([route]);
        var loaded = Assert.Single(YamlConfigLoader.LoadRouteList(yaml));

        Assert.Null(loaded.StripPrefix);
        Assert.Null(loaded.PathExact);
        Assert.Null(loaded.Order);
        Assert.Equal(TlsMode.Off, loaded.Tls);
        Assert.Null(loaded.HealthCheck);
    }

    [Fact]
    public void LoadRouteList_AcceptsRootSequence()
    {
        var routes = YamlConfigLoader.LoadRouteList("""
            - id: a
              hosts: [a.localhost]
              upstreams: [http://a:80]
            """);

        Assert.Single(routes);
        Assert.Equal("a", routes[0].Id);
    }

    [Fact]
    public void LoadRouteList_RejectsInvalidRoute()
    {
        Assert.Throws<SlickgateConfigException>(() => YamlConfigLoader.LoadRouteList("""
            routes:
              - id: broken
                hosts: [b.localhost]
            """));
    }

    [Fact]
    public void Write_MultipleRoutes_AreAllPresent()
    {
        var yaml = RouteYamlWriter.Write([
            new RouteDefinition { Id = "a", Hosts = ["a.localhost"], Upstreams = ["http://a:80"] },
            new RouteDefinition { Id = "b", Hosts = ["b.localhost"], Upstreams = ["http://b:80"] },
        ]);

        var loaded = YamlConfigLoader.LoadRouteList(yaml);

        Assert.Equal(["a", "b"], loaded.Select(r => r.Id));
    }
}
