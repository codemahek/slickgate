using Slickgate.Core.Configuration;
using Xunit;

namespace Slickgate.Core.Tests;

public class YamlConfigLoaderTests
{
    private const string Sample = """
        server:
          http_port: 8080
          https_port: 8443
          redirect_http_to_https: false

        admin:
          enabled: true
          token: changeme

        acme:
          enabled: true
          email: admin@example.com
          directory_url: https://acme-staging-v02.api.letsencrypt.org/directory
          storage_dir: ./certs

        docker:
          enabled: true
          endpoint: unix:///var/run/docker.sock
          network: slickgate
          label_prefix: slickgate
          poll_interval: 10s

        telemetry:
          otlp_endpoint: http://localhost:4317

        routes:
          - id: api
            host: api.example.com
            path_prefix: /api
            upstreams:
              - http://127.0.0.1:5099
            strip_prefix: /api
            tls: off
            request_headers:
              set:
                X-Gateway: slickgate
            response_headers:
              set:
                X-Powered-By: slickgate
            health_check:
              path: /
              interval: 10s
              timeout: 2s

          - id: catch-all
            hosts: [localhost, 127.0.0.1]
            path_prefix: /
            upstreams:
              - http://127.0.0.1:5099
            tls: internal
        """;

    [Fact]
    public void Load_ParsesAllSections()
    {
        var config = YamlConfigLoader.Load(Sample);

        Assert.Equal(8080, config.Server.HttpPort);
        Assert.Equal(8443, config.Server.HttpsPort);
        Assert.False(config.Server.RedirectHttpToHttps);

        Assert.True(config.Admin.Enabled);
        Assert.Equal("changeme", config.Admin.Token);

        Assert.True(config.Acme.Enabled);
        Assert.Equal("admin@example.com", config.Acme.Email);
        Assert.Equal("./certs", config.Acme.StorageDir);

        Assert.True(config.Docker.Enabled);
        Assert.Equal("unix:///var/run/docker.sock", config.Docker.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(10), config.Docker.PollInterval);

        Assert.Equal("http://localhost:4317", config.Telemetry.OtlpEndpoint);

        Assert.Equal(2, config.Routes.Count);
    }

    [Fact]
    public void Load_MapsRouteDetails()
    {
        var config = YamlConfigLoader.Load(Sample);
        var api = config.Routes[0];

        Assert.Equal("api", api.Id);
        Assert.Equal(["api.example.com"], api.Hosts);
        Assert.Equal("/api", api.PathPrefix);
        Assert.Equal(["http://127.0.0.1:5099"], api.Upstreams);
        Assert.Equal("/api", api.StripPrefix);
        Assert.Equal(TlsMode.Off, api.Tls);
        Assert.Equal("slickgate", api.RequestHeadersSet["X-Gateway"]);
        Assert.Equal("slickgate", api.ResponseHeadersSet["X-Powered-By"]);
        Assert.Equal("/", api.HealthCheck!.Path);
        Assert.Equal(TimeSpan.FromSeconds(10), api.HealthCheck.Interval);
        Assert.Equal(TimeSpan.FromSeconds(2), api.HealthCheck.Timeout);
    }

    [Fact]
    public void Load_MergesHostAndHosts()
    {
        var config = YamlConfigLoader.Load("""
            routes:
              - id: r
                host: a.example.com
                hosts: [b.example.com]
                upstreams: [http://127.0.0.1:1]
                tls: off
            """);

        Assert.Equal(["a.example.com", "b.example.com"], config.Routes[0].Hosts);
    }

    [Theory]
    [InlineData("10s", 10)]
    [InlineData("2m", 120)]
    [InlineData("1h", 3600)]
    [InlineData("1h30m", 5400)]
    [InlineData("1500ms", 1.5)]
    [InlineData("15", 15)]
    public void ParseDuration_HandlesUnits(string input, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), YamlConfigLoader.ParseDuration(input));
    }

    [Theory]
    [InlineData("10x")]
    [InlineData("abc")]
    [InlineData("")]
    public void ParseDuration_RejectsGarbage(string input)
    {
        Assert.Throws<SlickgateConfigException>(() => YamlConfigLoader.ParseDuration(input));
    }

    [Fact]
    public void Load_RejectsDuplicateRouteIds()
    {
        var yaml = """
            routes:
              - id: dup
                path_prefix: /
                upstreams: [http://127.0.0.1:1]
              - id: dup
                path_prefix: /other
                upstreams: [http://127.0.0.1:2]
            """;

        var ex = Assert.Throws<SlickgateConfigException>(() => YamlConfigLoader.Load(yaml));
        Assert.Contains("Duplicate route id", ex.Message);
    }

    [Fact]
    public void Load_RejectsRouteWithoutUpstreams()
    {
        var yaml = """
            routes:
              - id: broken
                host: x.example.com
            """;

        var ex = Assert.Throws<SlickgateConfigException>(() => YamlConfigLoader.Load(yaml));
        Assert.Contains("at least one upstream", ex.Message);
    }

    [Fact]
    public void Load_RejectsRouteWithoutMatcher()
    {
        var yaml = """
            routes:
              - id: broken
                upstreams: [http://127.0.0.1:1]
            """;

        var ex = Assert.Throws<SlickgateConfigException>(() => YamlConfigLoader.Load(yaml));
        Assert.Contains("at least one of host", ex.Message);
    }

    [Fact]
    public void Load_RejectsInvalidUpstream()
    {
        var yaml = """
            routes:
              - id: broken
                host: x.example.com
                upstreams: [ftp://nope]
            """;

        var ex = Assert.Throws<SlickgateConfigException>(() => YamlConfigLoader.Load(yaml));
        Assert.Contains("invalid upstream", ex.Message);
    }

    [Fact]
    public void Load_EmptyDocument_UsesDefaults()
    {
        var config = YamlConfigLoader.Load("");

        Assert.Empty(config.Routes);
        Assert.Equal(80, config.Server.HttpPort);
        Assert.Equal(443, config.Server.HttpsPort);
    }

    [Fact]
    public void Load_RejectsAcmeWithoutEmail()
    {
        var ex = Assert.Throws<SlickgateConfigException>(
            () => YamlConfigLoader.Load("acme:\n  enabled: true\n"));

        Assert.Contains("acme.email", ex.Message);
    }

    [Fact]
    public void Load_ParsesTlsFileStaticRootAndBasicAuth()
    {
        var config = YamlConfigLoader.Load("""
            tls:
              cert_file: /etc/slickgate/certs/origin.pem
              key_file: /etc/slickgate/certs/origin.key

            routes:
              - id: home
                hosts: [rishidaftary.com, www.rishidaftary.com]
                static_root: /etc/slickgate/www
                tls: file
              - id: nas
                host: my-nas.rishidaftary.com
                upstreams: [http://192.168.68.50:81]
                tls: file
                basic_auth:
                  - user: rishi
                    password_hash: $2y$05$6uUIkaWu6eUy
            """);

        Assert.Equal("/etc/slickgate/certs/origin.pem", config.Tls.CertFile);
        Assert.Equal("/etc/slickgate/certs/origin.key", config.Tls.KeyFile);

        var home = config.Routes[0];
        Assert.Equal(TlsMode.File, home.Tls);
        Assert.Equal("/etc/slickgate/www", home.StaticRoot);
        Assert.Empty(home.Upstreams);

        var nas = config.Routes[1];
        Assert.Equal(TlsMode.File, nas.Tls);
        var user = Assert.Single(nas.BasicAuth);
        Assert.Equal("rishi", user.User);
        Assert.Equal("$2y$05$6uUIkaWu6eUy", user.PasswordHash);
    }

    [Fact]
    public void Load_RejectsUnknownTlsMode()
    {
        var ex = Assert.Throws<SlickgateConfigException>(() => YamlConfigLoader.Load("""
            routes:
              - id: a
                host: a.example.com
                upstreams: [http://a:80]
                tls: bogus
            """));

        Assert.Contains("Invalid tls mode", ex.Message);
    }

    [Fact]
    public void Load_ParsesMigratedCaddyConfig()
    {
        var config = YamlConfigLoader.Load("""
            server:
              http_port: 80
              https_port: 443
              redirect_http_to_https: true

            tls:
              cert_file: /etc/slickgate/certs/origin.pem
              key_file: /etc/slickgate/certs/origin.key

            routes:
              - id: jellyfin
                host: jellyfin.rishidaftary.com
                upstreams: [http://192.168.68.50:30013]
                tls: file
              - id: my-nas
                host: my-nas.rishidaftary.com
                upstreams: [http://192.168.68.50:81]
                tls: file
                basic_auth:
                  - user: rishi
                    password_hash: $2y$05$6uUIkaWu6eUyEzxrK/YsAe3epTq/anYwtVkWZykTQxk0Qf48ATk5e
              - id: home
                hosts: [rishidaftary.com, www.rishidaftary.com, home.rishidaftary.com]
                static_root: /data/www
                tls: file
              - id: album
                host: album.rishidaftary.com
                upstreams: [http://192.168.68.50:30041]
                tls: file
              - id: hk-jain-parivar
                host: hk-jain-parivar.rishidaftary.com
                upstreams: [http://192.168.68.50:5691]
                tls: file
            """);

        Assert.Equal(5, config.Routes.Count);
        Assert.Equal("/etc/slickgate/certs/origin.pem", config.Tls.CertFile);

        var home = config.Routes.Single(r => r.Id == "home");
        Assert.Equal(3, home.Hosts.Count);
        Assert.Equal("/data/www", home.StaticRoot);
        Assert.Empty(home.Upstreams);

        var nas = config.Routes.Single(r => r.Id == "my-nas");
        Assert.Equal("rishi", Assert.Single(nas.BasicAuth).User);
        Assert.All(config.Routes, r => Assert.Equal(TlsMode.File, r.Tls));
    }
}
