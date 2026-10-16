using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("BACKEND_URLS") ?? "http://0.0.0.0:8088");
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

    context.Response.ContentType = "text/plain";
    context.Response.ContentLength = payload.Length;
    await context.Response.Body.WriteAsync(payload);
});

app.Run();
