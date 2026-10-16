using System.Security.Cryptography;
using System.Text;
using Slickgate.Gateway.Config;

namespace Slickgate.Gateway.Admin;

/// <summary>
/// Operational endpoints:
/// <c>GET /-/health</c> (open), <c>GET /-/routes</c>, <c>GET /-/config</c>,
/// <c>POST /-/reload</c> (last three require <c>X-Slickgate-Token</c> when configured).
/// </summary>
public static class AdminApi
{
    private const string TokenHeader = "X-Slickgate-Token";

    public static void MapAdminApi(this WebApplication app)
    {
        var runtime = app.Services.GetRequiredService<GatewayRuntime>();
        var options = app.Services.GetRequiredService<GatewayHostOptions>();
        var reloader = app.Services.GetRequiredService<ConfigReloader>();
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
                        return Results.Unauthorized();
                    }
                }

                return await next(context);
            });

        secured.MapGet("routes", () =>
        {
            var effective = runtime.Effective;
            var fileIds = new HashSet<string>(
                runtime.FileRoutes.Select(static r => r.Id),
                StringComparer.Ordinal);

            var views = effective.Routes.Select(route => new RouteView(
                route.Id,
                fileIds.Contains(route.Id) ? "file" : "docker",
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
    }

    private static bool FixedTimeEquals(string provided, string expected)
    {
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
