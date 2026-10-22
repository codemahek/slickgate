# YARP large-upload TLS/HTTP1.1 repro

A single-process repro showing that **proxying a large request body through YARP is
~2-4x slower than reading it directly when the client uses HTTPS + HTTP/1.1**, while
plaintext and HTTP/2 show no such gap.

Both paths run in the same Kestrel instance, so the only difference is whether the
body is handed to the application directly or copied by YARP's forwarder.

| Endpoint                            | Path taken                                                    |
| ----------------------------------- | ------------------------------------------------------------- |
| `POST https://localhost:7443/up`         | Kestrel reads the body → handler (no proxy)              |
| `POST https://localhost:7443/proxied/up` | YARP (`StreamCopier`) copies the body → plaintext origin |

The origin is the *same* process listening on `http://127.0.0.1:7080` (the `origin`
leg is plaintext HTTP/1.1, which is fast on its own), and YARP's `ActivityTimeout`
is disabled, so neither the origin nor an idle timeout can explain the numbers.

## Run

```bash
# Linux/macOS (self-contained publish, no SDK needed)
dotnet publish -c Release -r linux-x64 --self-contained true -o ./out
./out/YarpTlsRepro

# or with an SDK
dotnet run -c Release
```

Create a 100 MiB payload and upload it to both endpoints:

```bash
head -c 104857600 /dev/zero > /tmp/big.bin
CT='Content-Type: application/octet-stream'

# direct (Kestrel reads the body)
curl -s -o /dev/null -w '%{http_code} %{speed_upload} B/s\n' \
  --http1.1 -k -H "$CT" --data-binary @/tmp/big.bin https://127.0.0.1:7443/up

# through YARP
curl -s -o /dev/null -w '%{http_code} %{speed_upload} B/s\n' \
  --http1.1 -k -H "$CT" --data-binary @/tmp/big.bin https://127.0.0.1:7443/proxied/up
```

## Observed (WSL2 Ubuntu 24.04, 16 cores, loopback)

100 MiB upload, `curl` `%{speed_upload}`, three runs each:

| Client → Kestrel | Through YARP | Result              |
| ---------------- | ------------ | ------------------- |
| HTTPS + HTTP/1.1 | no           | 562-638 MB/s        |
| HTTPS + HTTP/1.1 | yes          | **107-209 MB/s**    |
| HTTP (plaintext) | no           | 673-685 MB/s        |
| HTTP (plaintext) | yes          | 572-653 MB/s        |
| HTTPS + HTTP/2   | no           | 367-376 MB/s        |
| HTTPS + HTTP/2   | yes          | 352-425 MB/s        |

The gap is specific to **HTTPS + HTTP/1.1**: the same YARP copy costs little over
plaintext or HTTP/2. HTTP/2 throughput is limited by Kestrel's own flow control on
both paths, which masks the difference.

## Where to look

- `src/ReverseProxy/Forwarder/StreamCopier.cs` — the request-body copy. 64 KiB
  `ArrayPool` buffer, a zero-byte read before each read, `WriteAsync` per read, no
  per-write flush.
- `StreamCopyHttpContent.SerializeToStreamAsync` / `HttpForwarder` — the copy runs
  inside `HttpContent` serialization on the outgoing `HttpClient` send.
- Likely interaction: reading a Kestrel `HttpRequestStream` over an
  `SslDuplexPipe` (TLS) returns small chunks (per TLS record), so the per-iteration
  overhead (extra zero-byte read, pipe scheduling, `HttpContent` write path) is paid
  far more often than over plaintext.

This repro intentionally isolates YARP from Slickgate; see `bench/README.md` for the
full Slickgate-vs-Caddy comparison that surfaced it.
