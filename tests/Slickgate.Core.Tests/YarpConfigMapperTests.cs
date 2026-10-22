using Slickgate.Core.Configuration;
using Slickgate.Core.Proxy;
using Xunit;

namespace Slickgate.Core.Tests;

public class YarpConfigMapperTests
{
    private static RouteDefinition Route(
        string id = "r",
        IReadOnlyList<string>? hosts = null,
        string? pathPrefix = null,
        string? pathExact = null,
        IReadOnlyList<string>? upstreams = null,
        string? stripPrefix = null,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        IReadOnlyDictionary<string, string>? responseHeaders = null,
        HealthCheckDefinition? health = null,
        int? order = null,
        TimeSpan? timeout = null) => new()
        {
            Id = id,
            Hosts = hosts ?? [],
            PathPrefix = pathPrefix,
            PathExact = pathExact,
            Upstreams = upstreams ?? ["http://127.0.0.1:5099"],
            StripPrefix = stripPrefix,
            RequestHeadersSet = requestHeaders ?? new Dictionary<string, string>(),
            ResponseHeadersSet = responseHeaders ?? new Dictionary<string, string>(),
            HealthCheck = health,
            Order = order,
            Timeout = timeout,
        };

    private static (IReadOnlyList<Yarp.ReverseProxy.Configuration.RouteConfig> Routes,
        IReadOnlyList<Yarp.ReverseProxy.Configuration.ClusterConfig> Clusters)
        Map(params RouteDefinition[] routes) =>
        YarpConfigMapper.Map(new GatewayConfig { Routes = routes });

    private static (IReadOnlyList<Yarp.ReverseProxy.Configuration.RouteConfig> Routes,
        IReadOnlyList<Yarp.ReverseProxy.Configuration.ClusterConfig> Clusters)
        Map(TimeSpan? serverTimeout, params RouteDefinition[] routes) =>
        YarpConfigMapper.Map(new GatewayConfig
        {
            Server = new ServerConfig { RequestTimeout = serverTimeout },
            Routes = routes,
        });

    [Fact]
    public void Map_PrefixPath_BecomesCatchAllTemplate()
    {
        var (routes, _) = Map(Route(pathPrefix: "/api"));

        Assert.Equal("/api/{**catch-all}", routes[0].Match.Path);
    }

    [Fact]
    public void Map_RootPrefix_MatchesEverything()
    {
        var (routes, _) = Map(Route(pathPrefix: "/"));

        Assert.Equal("/{**catch-all}", routes[0].Match.Path);
    }

    [Fact]
    public void Map_PrefixWithoutLeadingSlash_IsNormalized()
    {
        var (routes, _) = Map(Route(pathPrefix: "api/v1"));

        Assert.Equal("/api/v1/{**catch-all}", routes[0].Match.Path);
    }

    [Fact]
    public void Map_ExactPath_WinsOverPrefix()
    {
        var (routes, _) = Map(Route(pathExact: "/ping", pathPrefix: "/"));

        Assert.Equal("/ping", routes[0].Match.Path);
    }

    [Fact]
    public void Map_HostsArePreserved_OrNullWhenEmpty()
    {
        var (withHosts, _) = Map(Route(hosts: ["a.example.com", "b.example.com"]));
        var (withoutHosts, _) = Map(Route());

        Assert.Equal(["a.example.com", "b.example.com"], withHosts[0].Match.Hosts);
        Assert.Null(withoutHosts[0].Match.Hosts);
    }

    [Fact]
    public void Map_StripPrefix_AddsPathRemovePrefixTransform()
    {
        var (routes, _) = Map(Route(pathPrefix: "/api", stripPrefix: "/api"));

        var transform = Assert.Single(routes[0].Transforms!);
        Assert.Equal("/api", transform["PathRemovePrefix"]);
    }

    [Fact]
    public void Map_Headers_BecomeSetTransforms()
    {
        var (routes, _) = Map(Route(
            requestHeaders: new Dictionary<string, string> { ["X-Gateway"] = "slickgate" },
            responseHeaders: new Dictionary<string, string> { ["X-Powered-By"] = "slickgate" }));

        Assert.Contains(routes[0].Transforms!, t =>
            t.TryGetValue("RequestHeader", out var name) && name == "X-Gateway" &&
            t.TryGetValue("Set", out var value) && value == "slickgate");

        Assert.Contains(routes[0].Transforms!, t =>
            t.TryGetValue("ResponseHeader", out var name) && name == "X-Powered-By" &&
            t.TryGetValue("Set", out var value) && value == "slickgate");
    }

    [Fact]
    public void Map_ClusterId_MatchesRouteId_AndListsDestinations()
    {
        var (routes, clusters) = Map(Route(id: "api", upstreams: ["http://127.0.0.1:1", "http://127.0.0.1:2"]));

        Assert.Equal("api", routes[0].ClusterId);
        var cluster = Assert.Single(clusters);
        Assert.Equal("api", cluster.ClusterId);
        Assert.Equal("http://127.0.0.1:1", cluster.Destinations!["0"].Address);
        Assert.Equal("http://127.0.0.1:2", cluster.Destinations!["1"].Address);
    }

    [Fact]
    public void Map_HealthCheck_ConfiguredAsActive()
    {
        var (_, clusters) = Map(Route(
            health: new HealthCheckDefinition
            {
                Path = "/healthz",
                Interval = TimeSpan.FromSeconds(5),
                Timeout = TimeSpan.FromSeconds(1),
            }));

        var active = clusters[0].HealthCheck!.Active!;
        Assert.True(active.Enabled);
        Assert.Equal("/healthz", active.Path);
        Assert.Equal(TimeSpan.FromSeconds(5), active.Interval);
        Assert.Equal(TimeSpan.FromSeconds(1), active.Timeout);
    }

    [Fact]
    public void Map_NoTimeout_DisablesYarpActivityTimeout()
    {
        var (_, clusters) = Map(Route());

        Assert.Equal<TimeSpan?>(Timeout.InfiniteTimeSpan, clusters[0].HttpRequest!.ActivityTimeout);
    }

    [Fact]
    public void Map_ServerRequestTimeout_AppliesToClusters()
    {
        var (_, clusters) = Map(TimeSpan.FromMinutes(2), Route());

        Assert.Equal<TimeSpan?>(TimeSpan.FromMinutes(2), clusters[0].HttpRequest!.ActivityTimeout);
    }

    [Fact]
    public void Map_RouteTimeout_OverridesServerDefault()
    {
        var (_, clusters) = Map(TimeSpan.FromMinutes(2), Route(timeout: TimeSpan.FromSeconds(30)));

        Assert.Equal<TimeSpan?>(TimeSpan.FromSeconds(30), clusters[0].HttpRequest!.ActivityTimeout);
    }

    [Fact]
    public void Map_Order_FollowsDeclarationOrderByDefault()
    {
        var (routes, _) = Map(Route(id: "first"), Route(id: "second"));

        Assert.Equal(0, routes[0].Order);
        Assert.Equal(10, routes[1].Order);
    }

    [Fact]
    public void Map_ExplicitOrder_IsHonored()
    {
        var (routes, _) = Map(Route(id: "explicit", order: 42));

        Assert.Equal(42, routes[0].Order);
    }
}
