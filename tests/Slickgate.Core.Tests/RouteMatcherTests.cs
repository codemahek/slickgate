using Slickgate.Core.Configuration;
using Slickgate.Core.Proxy;
using Xunit;

namespace Slickgate.Core.Tests;

public class RouteMatcherTests
{
    private static RouteDefinition Route(
        string id,
        string[] hosts,
        string? prefix = null,
        string? exact = null,
        int? order = null) => new()
    {
        Id = id,
        Hosts = hosts,
        PathPrefix = prefix,
        PathExact = exact,
        Upstreams = ["http://upstream:80"],
        Order = order,
    };

    [Fact]
    public void MatchesHost_IgnoringCaseAndPort()
    {
        var route = Route("a", ["App.Example.com"], prefix: "/");

        Assert.Same(route, RouteMatcher.Match([route], "app.example.com", "/"));
        Assert.Same(route, RouteMatcher.Match([route], "APP.example.com:8443", "/"));
    }

    [Fact]
    public void WildcardHost_MatchesSubdomainsButNotApex()
    {
        var route = Route("a", ["*.example.com"], prefix: "/");

        Assert.NotNull(RouteMatcher.Match([route], "foo.example.com", "/"));
        Assert.NotNull(RouteMatcher.Match([route], "a.b.example.com", "/"));
        Assert.Null(RouteMatcher.Match([route], "example.com", "/"));
    }

    [Fact]
    public void PathPrefix_IsSegmentAware()
    {
        var route = Route("a", [], prefix: "/api");

        Assert.NotNull(RouteMatcher.Match([route], "any.example.com", "/api"));
        Assert.NotNull(RouteMatcher.Match([route], "any.example.com", "/api/users"));
        Assert.Null(RouteMatcher.Match([route], "any.example.com", "/apix"));
    }

    [Fact]
    public void RootPrefix_MatchesEverything()
    {
        var route = Route("a", ["example.com"], prefix: "/");

        Assert.NotNull(RouteMatcher.Match([route], "example.com", "/"));
        Assert.NotNull(RouteMatcher.Match([route], "example.com", "/anything/here"));
    }

    [Fact]
    public void PathExact_RequiresExactMatch()
    {
        var route = Route("a", ["example.com"], exact: "/health");

        Assert.NotNull(RouteMatcher.Match([route], "example.com", "/health"));
        Assert.Null(RouteMatcher.Match([route], "example.com", "/health/deep"));
    }

    [Fact]
    public void LowerOrderWins()
    {
        var first = Route("first", ["example.com"], prefix: "/", order: 10);
        var preferred = Route("preferred", ["example.com"], prefix: "/", order: 5);

        var match = RouteMatcher.Match([first, preferred], "example.com", "/");

        Assert.Same(preferred, match);
    }

    [Fact]
    public void NoMatch_ReturnsNull()
    {
        var route = Route("a", ["a.example.com"], prefix: "/");

        Assert.Null(RouteMatcher.Match([route], "b.example.com", "/"));
    }
}
