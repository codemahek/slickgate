namespace Slickgate.Gateway.Certificates;

/// <summary>
/// Serves ACME HTTP-01 key authorizations at
/// <c>/.well-known/acme-challenge/{token}</c>.
/// </summary>
public sealed class AcmeChallengeMiddleware(RequestDelegate next)
{
    private static readonly PathString Prefix = new("/.well-known/acme-challenge");

    public async Task InvokeAsync(HttpContext context, AcmeChallengeStore store)
    {
        if (context.Request.Path.StartsWithSegments(Prefix, out var remainder))
        {
            var token = remainder.Value?.Trim('/') ?? "";
            if (token.Length > 0 && store.TryGet(token, out var keyAuthorization))
            {
                context.Response.ContentType = "text/plain";
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsync(keyAuthorization);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);
    }
}
