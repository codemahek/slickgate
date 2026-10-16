using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Slickgate.Core.Configuration;
using Slickgate.Core.Discovery;
using Slickgate.Gateway.Config;

namespace Slickgate.Gateway.Admin;

/// <summary>
/// Operational + management endpoints:
/// <list type="bullet">
///   <item><c>GET /-/health</c> — always open.</item>
///   <item><c>GET /-/routes</c>, <c>GET /-/config</c>, <c>POST /-/reload</c>.</item>
///   <item><c>GET /-/docker/containers</c> — running containers + suggested mapping.</item>
///   <item><c>GET|PUT|DELETE /-/mappings[/{id}]</c> — admin-managed overlay routes.</item>
/// </list>
/// Everything except health requires <c>X-Slickgate-Token</c> when configured.
/// </summary>
public static class AdminApi
{
    private const string TokenHeader = "X-Slickgate-Token";

    public static void MapAdminApi(this WebApplication app)
    {
        var runtime = app.Services.GetRequiredService<GatewayRuntime>();
        var options = app.Services.GetRequiredService<GatewayHostOptions>();
        var reloader = app.Services.GetRequiredService<ConfigReloader>();
        var mappings = app.Services.GetRequiredService<MappingsStore>();
        var startedAt = DateTimeOffset.UtcNow;

        app.MapGet("/-/health", () =>
            Results.Json(
                new HealthResponse(
                    "ok",
                    runtime.Revision,
                    runtime.Effective.Routes.Count,
                    (long)(DateTimeOffset.UtcNow - startedAt).TotalSeconds),
                AdminJsonContext.Default.HealthResponse));

        var secured = app.MapGroup("/-/")
            .AddEndpointFilter(async (context, next) =>
            {
                var token = runtime.Effective.Admin.Token;
                if (!string.IsNullOrEmpty(token))
                {
                    var provided = context.HttpContext.Request.Headers[TokenHeader].ToString();
                    if (!FixedTimeEquals(provided, token))
                    {
                        return Results.Json(
                            new ErrorResponse("Invalid or missing X-Slickgate-Token."),
                            AdminJsonContext.Default.ErrorResponse,
                            statusCode: StatusCodes.Status401Unauthorized);
                    }
                }

                return await next(context);
            });

        secured.MapGet("routes", () =>
        {
            var effective = runtime.Effective;
            var fileIds = new HashSet<string>(runtime.FileRoutes.Select(static r => r.Id), StringComparer.Ordinal);
            var mappingIds = new HashSet<string>(runtime.MappingRoutes.Select(static r => r.Id), StringComparer.Ordinal);

            var views = effective.Routes.Select(route => new RouteView(
                route.Id,
                fileIds.Contains(route.Id) ? "file"
                    : mappingIds.Contains(route.Id) ? "mappings"
                    : "docker",
                route.Hosts,
                route.PathPrefix,
                route.PathExact,
                route.Upstreams,
                route.Tls.ToString().ToLowerInvariant(),
                route.StripPrefix)).ToList();

            return Results.Json(
                new RoutesResponse(views.Count, views),
                AdminJsonContext.Default.RoutesResponse);
        });

        secured.MapGet("config", () =>
        {
            var effective = runtime.Effective;
            return Results.Json(
                new ConfigResponse(
                    options.ConfigPath,
                    runtime.Revision,
                    effective.Server,
                    effective.Admin,
                    effective.Acme,
                    effective.Docker,
                    effective.Telemetry),
                AdminJsonContext.Default.ConfigResponse);
        });

        secured.MapPost("reload", () =>
        {
            var ok = reloader.TryReload(out var error);
            return Results.Json(
                new ReloadResponse(ok, error),
                AdminJsonContext.Default.ReloadResponse,
                statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status500InternalServerError);
        });

        // ---- Docker containers (read-only view for the UI) ------------------

        secured.MapGet("docker/containers", async (CancellationToken ct) =>
        {
            var docker = runtime.Effective.Docker;
            var mappingIds = new HashSet<string>(runtime.MappingRoutes.Select(static r => r.Id), StringComparer.Ordinal);

            try
            {
                using var client = new DockerApiClient(docker.Endpoint);
                var containers = await client.ListRunningContainersAsync(ct);
                var views = new List<ContainerView>(containers.Count);

                foreach (var container in containers)
                {
                    var name = (container.Names.Length > 0 ? container.Names[0] : container.Id)
                        .TrimStart('/');
                    var labels = container.Labels ?? new Dictionary<string, string>();
                    var slug = DockerLabelParser.Slugify(name);
                    var ports = container.Ports
                        .Select(static p => new DockerPortView(p.PrivatePort, p.PublicPort, p.Type))
                        .ToList();

                    var suggestedPort = SuggestPort(labels, docker.LabelPrefix, container.Ports);
                    var suggested = new RouteMapping(
                        slug,
                        [$"{name}.localhost"],
                        "/",
                        null,
                        [$"http://{name}:{suggestedPort}"],
                        null,
                        "internal",
                        null,
                        null,
                        null,
                        null);

                    views.Add(new ContainerView(
                        container.Id.Length >= 12 ? container.Id[..12] : container.Id,
                        name,
                        container.Image,
                        container.State,
                        ports,
                        DockerLabelParser.IsEnabled(labels, docker.LabelPrefix),
                        mappingIds.Contains(slug) ? slug : null,
                        labels,
                        suggested));
                }

                return Results.Json(
                    new ContainersResponse(docker.Enabled, null, views.Count, views),
                    AdminJsonContext.Default.ContainersResponse);
            }
            catch (Exception ex)
            {
                return Results.Json(
                    new ContainersResponse(docker.Enabled, ex.Message, 0, []),
                    AdminJsonContext.Default.ContainersResponse);
            }
        });

        // ---- Admin-managed mappings CRUD ------------------------------------

        secured.MapGet("mappings", () =>
        {
            var list = mappings.LoadFromDisk().Select(MappingConverter.FromDefinition).ToList();
            return Results.Json(
                new MappingsResponse(list.Count, list),
                AdminJsonContext.Default.MappingsResponse);
        });

        secured.MapPut("mappings/{id}", async (string id, HttpContext context, CancellationToken ct) =>
        {
            RouteMapping? payload;
            try
            {
                payload = await context.Request.ReadFromJsonAsync(
                    AdminJsonContext.Default.RouteMapping, ct);
            }
            catch (JsonException ex)
            {
                return Results.Json(
                    new MappingResponse(false, $"Invalid JSON: {ex.Message}", null),
                    AdminJsonContext.Default.MappingResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (payload is null)
            {
                return Results.Json(
                    new MappingResponse(false, "Request body is required.", null),
                    AdminJsonContext.Default.MappingResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            RouteDefinition route;
            try
            {
                route = MappingConverter.ToDefinition(payload, id);
                YamlConfigLoader.ValidateRoutes([route]);
            }
            catch (SlickgateConfigException ex)
            {
                return Results.Json(
                    new MappingResponse(false, ex.Message, null),
                    AdminJsonContext.Default.MappingResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            mappings.Upsert(route);
            return Results.Json(
                new MappingResponse(true, null, MappingConverter.FromDefinition(route)),
                AdminJsonContext.Default.MappingResponse);
        });

        secured.MapDelete("mappings/{id}", (string id) =>
        {
            if (!mappings.Delete(id))
            {
                return Results.Json(
                    new MappingResponse(false, $"Mapping '{id}' not found.", null),
                    AdminJsonContext.Default.MappingResponse,
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Json(
                new MappingResponse(true, null, null),
                AdminJsonContext.Default.MappingResponse);
        });
    }

    private static int SuggestPort(
        IReadOnlyDictionary<string, string> labels,
        string labelPrefix,
        DockerPort[] ports)
    {
        var key = labelPrefix.TrimEnd('.') + ".port";
        foreach (var (k, v) in labels)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var port))
            {
                return port;
            }
        }

        foreach (var port in ports)
        {
            if (string.Equals(port.Type, "tcp", StringComparison.OrdinalIgnoreCase) && port.PrivatePort > 0)
            {
                return port.PrivatePort;
            }
        }

        return 80;
    }

    private static bool FixedTimeEquals(string provided, string expected)
    {
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
