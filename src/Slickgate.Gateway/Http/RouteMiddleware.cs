using Microsoft.AspNetCore.StaticFiles;
using Slickgate.Core.Configuration;
using Slickgate.Core.Proxy;
using Slickgate.Core.Security;

namespace Slickgate.Gateway.Http;

/// <summary>
/// Applies per-route behavior that YARP does not cover natively: HTTP basic auth
/// and static file serving. Runs after the HTTPS redirect and before the reverse
/// proxy. Requests that match a static route are answered here and never proxied.
/// </summary>
public sealed class RouteMiddleware(RequestDelegate next)
{
    private const string Realm = "Slickgate";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public async Task InvokeAsync(HttpContext context, GatewayRuntime runtime)
    {
        // The admin API and ACME challenges are handled earlier in the pipeline.
        if (context.Request.Path.StartsWithSegments("/-") ||
            context.Request.Path.StartsWithSegments("/.well-known/acme-challenge"))
        {
            await next(context);
            return;
        }

        var route = RouteMatcher.Match(
            runtime.Effective.Routes,
            context.Request.Host.Host,
            context.Request.Path.Value ?? "/");

        if (route is null)
        {
            await next(context);
            return;
        }

        if (route.BasicAuth.Count > 0 && !IsAuthorized(context, route.BasicAuth))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = $"Basic realm=\"{Realm}\"";
            return;
        }

        if (route.StaticRoot is not null)
        {
            await ServeStaticAsync(context, route.StaticRoot);
            return;
        }

        await next(context);
    }

    private static bool IsAuthorized(HttpContext context, IReadOnlyList<BasicAuthUser> users)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) ||
            !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        var user = decoded[..separator];
        var password = decoded[(separator + 1)..];

        foreach (var candidate in users)
        {
            if (string.Equals(candidate.User, user, StringComparison.Ordinal) &&
                PasswordVerifier.Verify(password, candidate.PasswordHash))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task ServeStaticAsync(HttpContext context, string root)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        var fullRoot = Path.GetFullPath(root);

        var relative = (context.Request.Path.Value ?? "/").TrimStart('/');
        if (relative.Length == 0 || relative.EndsWith('/'))
        {
            relative += "index.html";
        }

        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));

        if (Directory.Exists(candidate))
        {
            candidate = Path.Combine(candidate, "index.html");
        }

        if (!IsUnder(candidate, fullRoot) || !File.Exists(candidate))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var contentType = ContentTypes.TryGetContentType(candidate, out var type)
            ? type
            : "application/octet-stream";

        await Results.File(candidate, contentType, enableRangeProcessing: true)
            .ExecuteAsync(context);
    }

    private static bool IsUnder(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.Ordinal))
        {
            return true;
        }

        var withSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return path.StartsWith(withSeparator, StringComparison.Ordinal);
    }
}
