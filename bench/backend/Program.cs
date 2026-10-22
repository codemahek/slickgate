using System.Buffers;
using System.Globalization;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("BACKEND_URLS") ?? "http://0.0.0.0:8088");

// No body size limit: the upload benchmark pushes >28.6 MiB (Kestrel's default)
// through the proxy, so the origin must accept it too. The slow-body protection is
// disabled so the origin never becomes the reason an upload is aborted while the
// proxy itself is under test.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = null;
    kestrel.Limits.MinRequestBodyDataRate = null;
});
builder.Logging.ClearProviders();

var app = builder.Build();

// Fixed 1 KiB body, written straight to the response — no routing, no per-request
// allocation. Both proxies benchmark against this same backend so the numbers
// reflect proxy overhead only.
var payload = new byte[1024];
Array.Fill(payload, (byte)'x');
Encoding.ASCII.GetBytes("slickgate-bench-payload-").CopyTo(payload, 0);

app.Run(async context =>
{
    if (context.Request.Path == "/health")
    {
        await context.Response.WriteAsync("ok");
        return;
    }

    // Upload endpoint: drain the request body so the proxy streams it end to end,
    // then report how many bytes arrived (used by the upload benchmark).
    if (HttpMethods.IsPost(context.Request.Method))
    {
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory())) > 0)
            {
                total += read;
            }

            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(total.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return;
    }

    context.Response.ContentType = "text/plain";
    context.Response.ContentLength = payload.Length;
    await context.Response.Body.WriteAsync(payload);
});

app.Run();
