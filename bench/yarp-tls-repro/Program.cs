// Minimal repro: uploading a large request body through YARP is far slower when
// the client connection is HTTPS + HTTP/1.1 than when the body is read directly by
// Kestrel, or when the client uses plaintext or HTTP/2.
//
//   POST https://localhost:7443/up           -> Kestrel reads the body directly
//   POST https://localhost:7443/proxied/up   -> YARP reads the body and forwards it
//                                               over plaintext HTTP/1.1 to /up on the
//                                               same process (the origin leg is neutral)
//
// Both endpoints run in one Kestrel instance, so only the presence of the YARP
// request-body copy differs. On a 16-core Linux box the direct path reaches
// ~550-800 MB/s while the proxied path sits at ~150-260 MB/s (2-4x slower).
// The gap disappears over plaintext or when the client uses HTTP/2.
//
// See README.md.

using System.Buffers;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

const int HttpsPort = 7443;
const int OriginPort = 7080;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = null;
    kestrel.Limits.MinRequestBodyDataRate = null;

    // Plaintext "origin" the proxy forwards to.
    kestrel.ListenLocalhost(OriginPort);

    // TLS ingress the client uploads to.
    kestrel.ListenLocalhost(HttpsPort, listen => listen.UseHttps(CreateSelfSignedCertificate()));
});

builder.Services
    .AddReverseProxy()
    .LoadFromMemory(
        routes:
        [
            new RouteConfig
            {
                RouteId = "proxied",
                ClusterId = "origin",
                Match = new RouteMatch { Path = "/proxied/{**catch-all}" },
                Transforms =
                [
                    new Dictionary<string, string> { ["PathRemovePrefix"] = "/proxied" },
                ],
            },
        ],
        clusters:
        [
            new ClusterConfig
            {
                ClusterId = "origin",
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    ["origin"] = new DestinationConfig { Address = $"http://127.0.0.1:{OriginPort}" },
                },
                // Disable YARP's own 100s idle timeout so it never hides a throughput result.
                HttpRequest = new ForwarderRequestConfig { ActivityTimeout = Timeout.InfiniteTimeSpan },
            },
        ]);

var app = builder.Build();

app.MapPost("/up", DrainAsync);
app.MapReverseProxy();

Console.WriteLine($"HTTPS ingress: https://localhost:{HttpsPort}  (direct: /up, proxied: /proxied/up)");
Console.WriteLine($"Origin:        http://127.0.0.1:{OriginPort}");

app.Run();

static async Task<IResult> DrainAsync(HttpRequest request)
{
    var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
    try
    {
        long total = 0;
        int read;
        while ((read = await request.Body.ReadAsync(buffer)) > 0)
        {
            total += read;
        }

        return Results.Text(total.ToString(CultureInfo.InvariantCulture));
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buffer);
    }
}

static X509Certificate2 CreateSelfSignedCertificate()
{
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest(
        "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    var san = new SubjectAlternativeNameBuilder();
    san.AddDnsName("localhost");
    san.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(san.Build());

    var certificate = request.CreateSelfSigned(
        DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    // Reload through PKCS#12 so the key is in a form Kestrel accepts on every platform.
    return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
}
