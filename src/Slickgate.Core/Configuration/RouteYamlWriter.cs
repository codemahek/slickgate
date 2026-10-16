using System.Globalization;
using System.Text;

namespace Slickgate.Core.Configuration;

/// <summary>
/// Serializes routes to a standalone overlay document. Written by hand (no
/// reflection-based serializer) so it stays Native-AOT friendly and produces a
/// stable, diff-friendly file for the admin-managed mappings.
/// </summary>
public static class RouteYamlWriter
{
    private const string Header =
        "# Managed by the Slickgate admin UI. Manual edits are overwritten on save.";

    public static string Write(IReadOnlyList<RouteDefinition> routes)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append('\n');

        if (routes.Count == 0)
        {
            sb.Append("routes: []\n");
            return sb.ToString();
        }

        sb.Append("routes:\n");
        foreach (var route in routes)
        {
            WriteRoute(sb, route);
        }

        return sb.ToString();
    }

    private static void WriteRoute(StringBuilder sb, RouteDefinition route)
    {
        sb.Append("  - id: ").Append(Scalar(route.Id)).Append('\n');

        if (route.Hosts.Count > 0)
        {
            sb.Append("    hosts:\n");
            foreach (var host in route.Hosts)
            {
                sb.Append("      - ").Append(Scalar(host)).Append('\n');
            }
        }

        if (route.PathPrefix is not null)
        {
            sb.Append("    path_prefix: ").Append(Scalar(route.PathPrefix)).Append('\n');
        }

        if (route.PathExact is not null)
        {
            sb.Append("    path_exact: ").Append(Scalar(route.PathExact)).Append('\n');
        }

        sb.Append("    upstreams:\n");
        foreach (var upstream in route.Upstreams)
        {
            sb.Append("      - ").Append(Scalar(upstream)).Append('\n');
        }

        if (route.StripPrefix is not null)
        {
            sb.Append("    strip_prefix: ").Append(Scalar(route.StripPrefix)).Append('\n');
        }

        if (route.Tls != TlsMode.Off)
        {
            sb.Append("    tls: ").Append(route.Tls.ToString().ToLowerInvariant()).Append('\n');
        }

        WriteHeaderMap(sb, "request_headers", route.RequestHeadersSet);
        WriteHeaderMap(sb, "response_headers", route.ResponseHeadersSet);

        if (route.HealthCheck is { } health)
        {
            sb.Append("    health_check:\n");
            sb.Append("      path: ").Append(Scalar(health.Path)).Append('\n');
            sb.Append("      interval: ").Append(FormatDuration(health.Interval)).Append('\n');
            sb.Append("      timeout: ").Append(FormatDuration(health.Timeout)).Append('\n');
        }

        if (route.Order is { } order)
        {
            sb.Append("    order: ").Append(order.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    private static void WriteHeaderMap(
        StringBuilder sb,
        string name,
        IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count == 0)
        {
            return;
        }

        sb.Append("    ").Append(name).Append(":\n");
        sb.Append("      set:\n");
        foreach (var (key, value) in headers)
        {
            sb.Append("        ").Append(Scalar(key)).Append(": ").Append(Scalar(value)).Append('\n');
        }
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalSeconds == Math.Floor(value.TotalSeconds)
            ? $"{value.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s"
            : $"{value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s";

    /// <summary>Quotes a scalar when it contains characters that are unsafe in plain YAML.</summary>
    private static string Scalar(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        foreach (var ch in value)
        {
            if (!IsPlainSafe(ch))
            {
                return Quote(value);
            }
        }

        return value;
    }

    private static bool IsPlainSafe(char ch) =>
        char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '/' or ':' or '+' or '@';

    private static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
