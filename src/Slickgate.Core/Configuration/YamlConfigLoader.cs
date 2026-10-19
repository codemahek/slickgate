using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Slickgate.Core.Configuration;

public static class YamlConfigLoader
{
    public static GatewayConfig LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new SlickgateConfigException($"Config file not found: {path}");
        }

        try
        {
            return Load(File.ReadAllText(path));
        }
        catch (SlickgateConfigException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SlickgateConfigException($"Failed to read config file '{path}': {ex.Message}");
        }
    }

    public static GatewayConfig Load(string yaml)
    {
        var root = ParseDocument(yaml);
        if (root is null)
        {
            return new GatewayConfig();
        }

        if (root is not YamlMappingNode map)
        {
            throw new SlickgateConfigException("Config root must be a YAML mapping.");
        }

        var config = new GatewayConfig
        {
            Server = ParseServer(Map(map, "server")),
            Admin = ParseAdmin(Map(map, "admin")),
            Acme = ParseAcme(Map(map, "acme")),
            Telemetry = ParseTelemetry(Map(map, "telemetry")),
            Tls = ParseTlsSection(Map(map, "tls")),
            Routes = ParseRoutes(Seq(map, "routes")),
        };

        if (config.Server.HttpPort == 0 && config.Server.HttpsPort == 0)
        {
            throw new SlickgateConfigException("At least one of server.http_port / server.https_port must be non-zero.");
        }

        ValidateRoutes(config.Routes);

        if (config.Acme.Enabled && string.IsNullOrWhiteSpace(config.Acme.Email))
        {
            throw new SlickgateConfigException("acme.email is required when acme.enabled is true.");
        }

        return config;
    }

    private static YamlNode? ParseDocument(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new SlickgateConfigException($"Invalid YAML: {ex.Message}");
        }

        return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
    }

    /// <summary>Validates a set of routes (unique ids, upstreams, matchers).</summary>
    public static void ValidateRoutes(IReadOnlyList<RouteDefinition> routes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.Id))
            {
                throw new SlickgateConfigException("Every route must have a non-empty 'id'.");
            }

            if (!seen.Add(route.Id))
            {
                throw new SlickgateConfigException($"Duplicate route id '{route.Id}'.");
            }

            if (route.Upstreams.Count == 0 && route.StaticRoot is null)
            {
                throw new SlickgateConfigException(
                    $"Route '{route.Id}' must declare at least one upstream or a static_root.");
            }

            foreach (var upstream in route.Upstreams)
            {
                if (!Uri.TryCreate(upstream, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    throw new SlickgateConfigException(
                        $"Route '{route.Id}' has invalid upstream '{upstream}' (expected http:// or https:// URL).");
                }
            }

            if (route.Hosts.Count == 0 && route.PathPrefix is null && route.PathExact is null)
            {
                throw new SlickgateConfigException(
                    $"Route '{route.Id}' must match on at least one of host, path_prefix, or path_exact.");
            }
        }
    }

    // ---- Section parsers -------------------------------------------------

    private static ServerConfig ParseServer(YamlMappingNode? map) => map is null
        ? new ServerConfig()
        : new ServerConfig
        {
            HttpPort = Int(map, "http_port", 80),
            HttpsPort = Int(map, "https_port", 443),
            RedirectHttpToHttps = Bool(map, "redirect_http_to_https", true),
        };

    private static AdminConfig ParseAdmin(YamlMappingNode? map) => map is null
        ? new AdminConfig()
        : new AdminConfig
        {
            Enabled = Bool(map, "enabled", true),
            Token = String(map, "token"),
            CorsOrigins = StringList(map, "cors_origins"),
        };

    private static AcmeConfig ParseAcme(YamlMappingNode? map) => map is null
        ? new AcmeConfig()
        : new AcmeConfig
        {
            Enabled = Bool(map, "enabled", false),
            Email = String(map, "email") ?? "",
            DirectoryUrl = String(map, "directory_url") ??
                           "https://acme-v02.api.letsencrypt.org/directory",
            StorageDir = String(map, "storage_dir") ?? "./certs",
        };

    private static TlsConfig ParseTlsSection(YamlMappingNode? map) => map is null
        ? new TlsConfig()
        : new TlsConfig
        {
            CertFile = String(map, "cert_file"),
            KeyFile = String(map, "key_file"),
        };

    private static TelemetryConfig ParseTelemetry(YamlMappingNode? map) => map is null
        ? new TelemetryConfig()
        : new TelemetryConfig { OtlpEndpoint = String(map, "otlp_endpoint") };

    private static List<RouteDefinition> ParseRoutes(YamlSequenceNode? seq)
    {
        if (seq is null)
        {
            return [];
        }

        var routes = new List<RouteDefinition>();
        foreach (var node in seq.Children)
        {
            if (node is not YamlMappingNode map)
            {
                throw new SlickgateConfigException("Each entry under 'routes' must be a mapping.");
            }

            routes.Add(ParseRoute(map));
        }

        return routes;
    }

    private static RouteDefinition ParseRoute(YamlMappingNode map)
    {
        var host = String(map, "host");
        var hosts = StringList(map, "hosts");
        if (host is not null)
        {
            hosts = [host, .. hosts];
        }

        var requestHeaders = Map(map, "request_headers");
        var responseHeaders = Map(map, "response_headers");

        return new RouteDefinition
        {
            Id = String(map, "id") ?? "",
            Hosts = hosts,
            PathPrefix = String(map, "path_prefix"),
            PathExact = String(map, "path_exact"),
            Upstreams = StringList(map, "upstreams"),
            StripPrefix = String(map, "strip_prefix"),
            Tls = ParseTlsMode(String(map, "tls")),
            CertFile = String(map, "cert_file"),
            KeyFile = String(map, "key_file"),
            StaticRoot = String(map, "static_root"),
            BasicAuth = ParseBasicAuth(Seq(map, "basic_auth")),
            RequestHeadersSet = HeaderMap(requestHeaders, "set"),
            RequestHeadersAppend = HeaderMap(requestHeaders, "append"),
            ResponseHeadersSet = HeaderMap(responseHeaders, "set"),
            HealthCheck = ParseHealthCheck(Map(map, "health_check")),
            Order = map.Children.TryGetValue(new YamlScalarNode("order"), out var orderNode)
                ? int.Parse(orderNode.ToString())
                : null,
        };
    }

    private static IReadOnlyList<BasicAuthUser> ParseBasicAuth(YamlSequenceNode? seq)
    {
        if (seq is null)
        {
            return [];
        }

        var users = new List<BasicAuthUser>();
        foreach (var node in seq.Children)
        {
            if (node is not YamlMappingNode userMap)
            {
                throw new SlickgateConfigException(
                    "Each 'basic_auth' entry must be a mapping with 'user' and 'password_hash'.");
            }

            var user = String(userMap, "user");
            var hash = String(userMap, "password_hash");
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(hash))
            {
                throw new SlickgateConfigException(
                    "Each 'basic_auth' entry requires a non-empty 'user' and 'password_hash'.");
            }

            users.Add(new BasicAuthUser { User = user!, PasswordHash = hash! });
        }

        return users;
    }

    private static TlsMode ParseTlsMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "off" => TlsMode.Off,
        "internal" => TlsMode.Internal,
        "auto" => TlsMode.Auto,
        "file" => TlsMode.File,
        _ => throw new SlickgateConfigException(
            $"Invalid tls mode '{value}' (expected off, internal, auto, or file)."),
    };

    private static HealthCheckDefinition? ParseHealthCheck(YamlMappingNode? map)
    {
        if (map is null)
        {
            return null;
        }

        return new HealthCheckDefinition
        {
            Path = String(map, "path") ?? "/health",
            Interval = Duration(map, "interval", TimeSpan.FromSeconds(10)),
            Timeout = Duration(map, "timeout", TimeSpan.FromSeconds(2)),
        };
    }

    // ---- YAML node helpers ----------------------------------------------

    private static YamlNode Key(string name) => new YamlScalarNode(name);

    /// <summary>
    /// True for an omitted value ("key:"), "~", or a plain "null" scalar — but not
    /// for an explicitly quoted empty string ("key: \"\"").
    /// </summary>
    private static bool IsNullScalar(YamlScalarNode scalar) =>
        scalar.Value is null ||
        (scalar.Style == ScalarStyle.Plain &&
         scalar.Value.Trim() is "" or "~" or "null" or "Null" or "NULL");

    private static bool IsMissing(YamlNode node) =>
        node is YamlScalarNode scalar && IsNullScalar(scalar);

    private static YamlMappingNode? Map(YamlMappingNode parent, string name)
    {
        if (!parent.Children.TryGetValue(Key(name), out var node))
        {
            return null;
        }

        return node switch
        {
            YamlMappingNode map => map,
            YamlScalarNode scalar when IsNullScalar(scalar) => null,
            _ => throw new SlickgateConfigException($"'{name}' must be a mapping."),
        };
    }

    private static YamlSequenceNode? Seq(YamlMappingNode parent, string name)
    {
        if (!parent.Children.TryGetValue(Key(name), out var node))
        {
            return null;
        }

        return node switch
        {
            YamlSequenceNode seq => seq,
            YamlScalarNode scalar when IsNullScalar(scalar) => null,
            _ => throw new SlickgateConfigException($"'{name}' must be a list."),
        };
    }

    private static string? String(YamlMappingNode parent, string name)
    {
        if (!parent.Children.TryGetValue(Key(name), out var node) || IsMissing(node))
        {
            return null;
        }

        if (node is not YamlScalarNode scalar)
        {
            throw new SlickgateConfigException($"'{name}' must be a string.");
        }

        return scalar.Value;
    }

    private static bool Bool(YamlMappingNode parent, string name, bool fallback)
    {
        var value = String(parent, name);
        if (value is null)
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new SlickgateConfigException($"'{name}' must be a boolean, got '{value}'."),
        };
    }

    private static int Int(YamlMappingNode parent, string name, int fallback)
    {
        var value = String(parent, name);
        if (value is null)
        {
            return fallback;
        }

        if (!int.TryParse(value, out var result))
        {
            throw new SlickgateConfigException($"'{name}' must be an integer, got '{value}'.");
        }

        return result;
    }

    private static IReadOnlyList<string> StringList(YamlMappingNode parent, string name)
    {
        if (!parent.Children.TryGetValue(Key(name), out var node) || IsMissing(node))
        {
            return [];
        }

        return node switch
        {
            YamlSequenceNode seq => seq.Children
                .Select(static child => child.ToString())
                .ToList(),
            YamlScalarNode scalar when !string.IsNullOrWhiteSpace(scalar.Value) => [scalar.Value],
            _ => throw new SlickgateConfigException($"'{name}' must be a list of strings."),
        };
    }

    private static IReadOnlyDictionary<string, string> HeaderMap(YamlMappingNode? parent, string name)
    {
        if (parent is null)
        {
            return new Dictionary<string, string>();
        }

        var map = Map(parent, name);
        if (map is null)
        {
            return new Dictionary<string, string>();
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in map.Children)
        {
            var headerName = key.ToString();
            if (string.IsNullOrWhiteSpace(headerName))
            {
                throw new SlickgateConfigException($"Empty header name under '{name}'.");
            }

            result[headerName] = value.ToString();
        }

        return result;
    }

    /// <summary>
    /// Parses durations such as "10s", "2m", "1h30m", "500ms", or a bare number of seconds.
    /// </summary>
    public static TimeSpan ParseDuration(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            throw new SlickgateConfigException("Duration must not be empty.");
        }

        if (long.TryParse(value, out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        var totalSeconds = 0.0;
        var index = 0;

        while (index < value.Length)
        {
            var start = index;
            if (value[index] is '+' or '-')
            {
                index++;
            }

            while (index < value.Length && (char.IsDigit(value[index]) || value[index] == '.'))
            {
                index++;
            }

            if (index == start || index == value.Length ||
                !double.TryParse(value[start..index], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var magnitude))
            {
                throw new SlickgateConfigException($"Invalid duration '{value}'.");
            }

            var unitStart = index;
            if (string.Compare(value, index, "ms", 0, 2, StringComparison.OrdinalIgnoreCase) == 0)
            {
                totalSeconds += magnitude / 1000.0;
                index += 2;
            }
            else
            {
                index++;
                totalSeconds += char.ToLowerInvariant(value[unitStart]) switch
                {
                    'd' => magnitude * 86400,
                    'h' => magnitude * 3600,
                    'm' => magnitude * 60,
                    's' => magnitude,
                    _ => throw new SlickgateConfigException(
                        $"Invalid duration unit '{value[unitStart]}' in '{value}' (use d, h, m, s, or ms)."),
                };
            }
        }

        return TimeSpan.FromSeconds(totalSeconds);
    }

    private static TimeSpan Duration(YamlMappingNode parent, string name, TimeSpan fallback)
    {
        var value = String(parent, name);
        if (value is null)
        {
            return fallback;
        }

        try
        {
            return ParseDuration(value);
        }
        catch (SlickgateConfigException ex)
        {
            throw new SlickgateConfigException($"'{name}': {ex.Message}");
        }
    }
}
