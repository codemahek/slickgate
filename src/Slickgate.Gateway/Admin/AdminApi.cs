using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Slickgate.Core.Configuration;
using Slickgate.Gateway.Config;

namespace Slickgate.Gateway.Admin;

/// <summary>
/// Operational + management endpoints:
/// <list type="bullet">
///   <item><c>GET /-/health</c> — always open.</item>
///   <item><c>GET /-/routes</c>, <c>GET /-/config</c>.</item>
///   <item><c>GET /-/config/raw</c>, <c>POST /-/config/validate</c>, <c>PUT /-/config/raw</c>.</item>
///   <item><c>POST /-/reload</c> — apply the saved config file (explicit, no file watcher).</item>
/// </list>
/// Everything except health requires <c>X-Slickgate-Token</c> when configured.
/// </summary>
public static class AdminApi
{
    private const string TokenHeader = "X-Slickgate-Token";

    /// <summary>Placeholder returned in place of a configured admin token.</summary>
    private const string RedactedToken = "***";

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
                if (!runtime.Effective.Admin.Enabled)
                {
                    return Results.NotFound();
                }

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
            var views = runtime.Effective.Routes.Select(route => new RouteView(
                route.Id,
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

            // Never echo the admin token back to callers: report only whether one is set.
            var admin = effective.Admin with
            {
                Token = string.IsNullOrEmpty(effective.Admin.Token) ? null : RedactedToken,
            };

            return Results.Json(
                new ConfigResponse(
                    options.ConfigPath,
                    runtime.Revision,
                    effective.Server,
                    admin,
                    effective.Acme,
                    effective.Telemetry),
                AdminJsonContext.Default.ConfigResponse);
        });

        // ---- Raw config file (the UI's YAML editor) ------------------------

        secured.MapGet("config/raw", () =>
        {
            try
            {
                var yaml = File.ReadAllText(reloader.ConfigPath);
                return Results.Json(
                    new RawConfigResponse(reloader.ConfigPath, yaml),
                    AdminJsonContext.Default.RawConfigResponse);
            }
            catch (Exception ex)
            {
                return ActionFailed($"Could not read the config file: {ex.Message}");
            }
        });

        // Handlers stay synchronous and wrap their async work in DeferredResult: the RDG does
        // not generate awaited delegates for Task<IResult>-returning handlers under Native AOT
        // (dotnet/aspnetcore#58509), which otherwise serializes the Task and fails with 500.
        secured.MapPost("config/validate", (HttpContext context, CancellationToken ct) =>
            new DeferredResult(ValidateAsync(context, ct)));

        secured.MapPut("config/raw", (HttpContext context, CancellationToken ct) =>
            new DeferredResult(SaveRawAsync(reloader, context, ct)));

        secured.MapPost("reload", () =>
        {
            var ok = reloader.TryReload(out var error);
            return Results.Json(
                new ReloadResponse(ok, error),
                AdminJsonContext.Default.ReloadResponse,
                statusCode: ok ? StatusCodes.Status200OK : StatusCodes.Status500InternalServerError);
        });
    }

    private static IResult Validate(string yaml)
    {
        try
        {
            YamlConfigLoader.Load(yaml);
            return ActionSucceeded();
        }
        catch (Exception ex)
        {
            return ActionFailed(ex.Message);
        }
    }

    private static async Task<IResult> ValidateAsync(HttpContext context, CancellationToken ct)
    {
        var request = await ReadRawRequestAsync(context, ct);
        if (request is null)
        {
            return ActionFailed("Request body is required: { \"yaml\": \"...\" }");
        }

        return Validate(request.Yaml);
    }

    private static async Task<IResult> SaveRawAsync(
        ConfigReloader reloader,
        HttpContext context,
        CancellationToken ct)
    {
        var request = await ReadRawRequestAsync(context, ct);
        if (request is null)
        {
            return ActionFailed("Request body is required: { \"yaml\": \"...\" }");
        }

        try
        {
            YamlConfigLoader.Load(request.Yaml);
        }
        catch (Exception ex)
        {
            return ActionFailed(ex.Message);
        }

        var path = reloader.ConfigPath;
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, request.Yaml, ct);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            return ActionFailed($"Could not write the config file: {ex.Message}");
        }

        return ActionSucceeded();
    }

    private static async Task<RawConfigRequest?> ReadRawRequestAsync(HttpContext context, CancellationToken ct)
    {
        try
        {
            return await context.Request.ReadFromJsonAsync(
                AdminJsonContext.Default.RawConfigRequest, ct);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Malformed JSON, empty body, or a non-JSON content type: treat as
            // a bad request rather than letting it surface as a 500.
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best effort — the temp file is ignored by the loader
        }
    }

    private static IResult ActionSucceeded() =>
        Results.Json(
            new ConfigActionResponse(true, null),
            AdminJsonContext.Default.ConfigActionResponse);

    private static IResult ActionFailed(string error) =>
        Results.Json(
            new ConfigActionResponse(false, error),
            AdminJsonContext.Default.ConfigActionResponse,
            statusCode: StatusCodes.Status400BadRequest);

    private static bool FixedTimeEquals(string provided, string expected)
    {
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
