namespace Slickgate.Gateway.Http;

/// <summary>
/// Redirects plain HTTP requests to HTTPS (except the ACME challenge path and the
/// admin API), mirroring Caddy's automatic HTTP-to-HTTPS redirect.
/// </summary>
public sealed class HttpsRedirectMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, GatewayRuntime runtime, GatewayHostOptions options)
    {
        var server = runtime.Effective.Server;

        if (server.RedirectHttpToHttps &&
            options.HttpsPort > 0 &&
            !context.Request.IsHttps &&
            !context.Request.Path.StartsWithSegments("/.well-known/acme-challenge") &&
            !context.Request.Path.StartsWithSegments("/-"))
        {
            var host = context.Request.Host.Host;
            var port = options.HttpsPort == 443 ? "" : ":" + options.HttpsPort;
            var target = $"https://{host}{port}{context.Request.Path}{context.Request.QueryString}";

            context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location = target;
            return;
        }

        await next(context);
    }
}
