namespace Slickgate.Gateway.Admin;

/// <summary>
/// CORS for the admin API. The UI is deployed independently (its own origin), so
/// it needs cross-origin access to <c>/-/</c>. Allowed origins come from
/// <c>admin.cors_origins</c> and are re-read per request, so a reload applies
/// immediately. OPTIONS preflights are answered here so the token filter never
/// sees them.
/// </summary>
public sealed class AdminCorsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, GatewayRuntime runtime)
    {
        if (context.Request.Path.StartsWithSegments("/-"))
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (TryResolve(runtime.Effective.Admin.CorsOrigins, origin, out var allowOrigin))
            {
                var headers = context.Response.Headers;
                headers["Access-Control-Allow-Origin"] = allowOrigin;
                headers["Access-Control-Allow-Methods"] = "GET, POST, PUT, DELETE, OPTIONS";
                headers["Access-Control-Allow-Headers"] = "Content-Type, X-Slickgate-Token";
                headers["Access-Control-Max-Age"] = "600";
                headers["Vary"] = "Origin";

                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }
            }
        }

        await next(context);
    }

    private static bool TryResolve(
        IReadOnlyList<string> allowed,
        string origin,
        out string allowOrigin)
    {
        allowOrigin = "";

        if (allowed.Count == 0 || string.IsNullOrEmpty(origin))
        {
            return false;
        }

        foreach (var candidate in allowed)
        {
            if (candidate == "*")
            {
                allowOrigin = "*";
                return true;
            }

            if (string.Equals(candidate, origin, StringComparison.OrdinalIgnoreCase))
            {
                allowOrigin = origin;
                return true;
            }
        }

        return false;
    }
}
