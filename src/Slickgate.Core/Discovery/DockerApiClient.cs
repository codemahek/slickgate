using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Slickgate.Core.Discovery;

public sealed class DockerApiException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// A deliberately small Docker Engine API client (list + inspect containers only).
/// Speaks HTTP over a unix socket, Windows named pipe, or plain TCP without taking
/// a dependency on Docker.DotNet, and uses source-generated JSON so it is AOT-safe.
/// </summary>
public sealed class DockerApiClient : IDisposable
{
    private readonly HttpClient _http;

    public DockerApiClient(string endpoint, TimeSpan? timeout = null)
    {
        Endpoint = endpoint;
        var transport = DockerTransport.Parse(endpoint);

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = transport.ConnectAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = transport.BaseAddress,
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
    }

    public string Endpoint { get; }

    public async Task<IReadOnlyList<ContainerListItem>> ListRunningContainersAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("/containers/json", ct).ConfigureAwait(false);
        await EnsureSuccess(response, ct).ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, DockerJsonContext.Default.ContainerListItemArray) ?? [];
    }

    public async Task<ContainerInspect?> InspectAsync(string containerId, CancellationToken ct)
    {
        var id = Uri.EscapeDataString(containerId);
        using var response = await _http.GetAsync($"/containers/{id}/json", ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccess(response, ct).ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, DockerJsonContext.Default.ContainerInspect);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            body = "";
        }

        if (body.Length > 500)
        {
            body = body[..500];
        }

        throw new DockerApiException(
            $"Docker API returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
            response.StatusCode);
    }

    public void Dispose() => _http.Dispose();

    private sealed class DockerTransport
    {
        private readonly string? _socketPath;
        private readonly string? _pipeServer;
        private readonly string? _pipeName;
        private readonly string? _tcpHost;
        private readonly int _tcpPort;

        private DockerTransport(
            string? socketPath,
            string? pipeServer,
            string? pipeName,
            string? tcpHost,
            int tcpPort)
        {
            _socketPath = socketPath;
            _pipeServer = pipeServer;
            _pipeName = pipeName;
            _tcpHost = tcpHost;
            _tcpPort = tcpPort;
        }

        public Uri BaseAddress { get; private init; } = new("http://docker");

        public static DockerTransport Parse(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new DockerApiException("Docker endpoint must not be empty.");
            }

            var value = endpoint.Trim();

            if (value.StartsWith("unix://", StringComparison.OrdinalIgnoreCase))
            {
                var path = value["unix://".Length..].TrimEnd('/');
                if (path.Length == 0)
                {
                    throw new DockerApiException($"Invalid unix socket endpoint '{endpoint}'.");
                }

                return new DockerTransport(path, null, null, null, 0)
                {
                    BaseAddress = new Uri("http://docker"),
                };
            }

            if (value.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("pipe://", StringComparison.OrdinalIgnoreCase))
            {
                var schemeEnd = value.IndexOf("://", StringComparison.Ordinal) + 3;
                var path = value[schemeEnd..].TrimStart('/');
                var pipeIndex = path.LastIndexOf("/pipe/", StringComparison.OrdinalIgnoreCase);
                if (pipeIndex < 0)
                {
                    throw new DockerApiException($"Invalid named pipe endpoint '{endpoint}'.");
                }

                var server = pipeIndex == 0 ? "." : path[..pipeIndex].Trim('/');
                if (server.Length == 0)
                {
                    server = ".";
                }

                var pipeName = path[(pipeIndex + "/pipe/".Length)..];
                if (pipeName.Length == 0)
                {
                    throw new DockerApiException($"Invalid named pipe endpoint '{endpoint}'.");
                }

                return new DockerTransport(null, server, pipeName, null, 0)
                {
                    BaseAddress = new Uri("http://docker"),
                };
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme is "tcp" or "http" or "https")
            {
                var host = uri.Host;
                var port = uri.Port > 0 ? uri.Port : 2375;
                var builder = new UriBuilder("http", host, port);
                return new DockerTransport(null, null, null, host, port)
                {
                    BaseAddress = builder.Uri,
                };
            }

            throw new DockerApiException(
                $"Unsupported Docker endpoint '{endpoint}' (use unix://, npipe://, or tcp://).");
        }

        public async ValueTask<Stream> ConnectAsync(
            SocketsHttpConnectionContext context,
            CancellationToken cancellationToken)
        {
            if (_socketPath is not null)
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken)
                        .ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            if (_pipeName is not null)
            {
                var pipe = new NamedPipeClientStream(
                    _pipeServer ?? ".",
                    _pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return pipe;
            }

            var tcp = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await tcp.ConnectAsync(_tcpHost!, _tcpPort, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(tcp, ownsSocket: true);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
    }
}
