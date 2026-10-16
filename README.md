# Slickgate

A Caddy-style automated edge gateway for .NET. Point it at a YAML file (or a
Docker socket) and it terminates TLS, routes traffic, and keeps itself up to
date — without a config DSL, without manual certificate renewals, and without a
control-plane database.

Built on [YARP](https://github.com/dotnet/yarp) and ASP.NET Core, and designed
to compile all the way down to a single **Native AOT** binary.

```
Internet ──▶ Slickgate ──┬─▶ api     (api.example.com/api/*)
                         ├─▶ app     (app.localhost/*)
                         └─▶ …       (auto-discovered Docker containers)
```

## Features

- **YAML configuration** with **hot reload** — edit the file, routes update in
  place; a broken edit never takes the running gateway down.
- **Automatic HTTPS** via ACME (Let's Encrypt et al., HTTP-01), plus a built-in
  `internal` mode that mints self-signed certs per host for local development.
- **Docker service discovery** — containers opt in with a handful of labels and
  Slickgate builds routes for them automatically.
- **Path routing & rewriting** — `path_prefix` / `path_exact` matching,
  `strip_prefix`, and request/response header transforms.
- **Health checks** — active probing per cluster with automatic failover.
- **Admin API** — inspect effective routes/config and trigger reloads at
  `/-/…` (optionally token-protected).
- **Admin UI** — a standalone Blazor WebAssembly dashboard (its own container)
  for routes, mappings, Docker containers, and settings, talking to the gateway
  purely over the admin API.
- **Observability** — OpenTelemetry (OTLP) traces/metrics when configured.
- **Native AOT** — `-p:SlickgateAot=true` produces a self-contained native
  binary for fast cold starts and a tiny footprint.

## Quick start

Requires the [.NET 11 SDK](https://dotnet.microsoft.com/).

```bash
git clone https://github.com/codemahek/slickgate.git
cd slickgate
dotnet run --project src/Slickgate.Gateway -- --config samples/gateway.yaml
```

The sample listens on `:8080` (HTTP) and `:8443` (HTTPS) and proxies to a local
echo backend on `:5099`.

```bash
curl http://localhost:8080/-/health          # gateway health
curl -k https://localhost:8443/ping          # proxied request
curl http://localhost:8080/-/routes          # effective routes
```

### Docker

```bash
docker compose up --build
```

Then browse `http://app.localhost:8080` (static route) and
`http://whoami.localhost:8080` (route discovered from labels). On
Windows/macOS `*.localhost` resolves to the loopback address automatically.

The standalone admin UI is served at `http://localhost:8081` and connects to the
gateway over its admin API.

## Configuration

See [`samples/gateway.yaml`](samples/gateway.yaml) for a full example.

```yaml
server:
  http_port: 80
  https_port: 443
  redirect_http_to_https: true

admin:
  enabled: true
  token: change-me            # omit to leave the admin API open

acme:
  enabled: true
  email: admin@example.com
  directory_url: https://acme-v02.api.letsencrypt.org/directory
  storage_dir: ./certs

docker:
  enabled: true
  endpoint: unix:///var/run/docker.sock
  network: slickgate
  label_prefix: slickgate
  poll_interval: 10s

routes:
  - id: api
    host: api.example.com
    path_prefix: /api
    strip_prefix: /api
    upstreams:
      - http://127.0.0.1:5001
    tls: auto
    request_headers:
      set:
        X-Gateway: slickgate
    health_check:
      path: /healthz
      interval: 10s
      timeout: 2s

  - id: app
    hosts: [app.example.com, www.example.com]
    path_prefix: /
    upstreams:
      - http://127.0.0.1:5002
    tls: auto
```

### Reference

| Section   | Key                    | Default                 | Notes                                            |
| --------- | ---------------------- | ----------------------- | ------------------------------------------------ |
| `server`  | `http_port`            | `80`                    | HTTP listener                                    |
|           | `https_port`           | `443`                   | HTTPS listener                                   |
|           | `redirect_http_to_https` | `true`                | 308 redirect (skips `/-/` and ACME challenges)   |
| `admin`   | `enabled`              | `true`                  | Mounts the `/-/` endpoints                       |
|           | `token`                | –                       | Sent as `X-Slickgate-Token`                      |
| `acme`    | `enabled`              | `false`                 | Enable Let's Encrypt issuance                    |
|           | `email`                | –                       | Required when `enabled`                          |
|           | `directory_url`        | LE production           | Use the staging URL while testing                |
|           | `storage_dir`          | `./certs`               | Certificates + account key                       |
| `docker`  | `enabled`              | `false`                 | Poll the Docker Engine API                       |
|           | `endpoint`             | `unix:///var/run/docker.sock` | Also `npipe://` and `tcp://`               |
|           | `network`              | `slickgate`             | Network to resolve container IPs on              |
|           | `poll_interval`        | `10s`                   | Durations accept `d/h/m/s/ms`                    |
| `route`   | `id`                   | –                       | Required, unique                                 |
|           | `host` / `hosts`       | –                       | At least one of host or `path_*` required        |
|           | `path_prefix` / `path_exact` | –                 | Prefix matches are `{**catch-all}` templates     |
|           | `upstreams`            | –                       | Required, `http(s)://host[:port]`                |
|           | `strip_prefix`         | –                       | Removes a prefix before forwarding               |
|           | `tls`                  | `off`                   | `off` \| `internal` (self-signed) \| `auto` (ACME) |
|           | `request_headers.set`  | –                       | Map of header → value                            |
|           | `response_headers.set` | –                       | Map of header → value                            |
|           | `health_check`         | –                       | `path`, `interval`, `timeout`                    |
|           | `order`                | declaration order × 10  | Lower runs first                                 |

### Docker labels

Every label is prefixed (default `slickgate`, configurable via
`docker.label_prefix`):

| Label                     | Example             | Meaning                                   |
| ------------------------- | ------------------- | ----------------------------------------- |
| `slickgate.enable`        | `true`              | Required — opt the container in           |
| `slickgate.host`/`.hosts` | `app.example.com`   | Hostname(s); defaults to container name   |
| `slickgate.port`          | `3000`              | Container port to proxy to (default `80`) |
| `slickgate.id`            | `app`               | Route id (defaults to a slug of the name) |
| `slickgate.path`          | `/api`              | Path prefix (default `/`)                 |
| `slickgate.tls`           | `internal`          | `off` \| `internal` \| `auto`             |
| `slickgate.strip_prefix`  | `true` or `/api`    | Strip the path prefix                     |
| `slickgate.health_path`   | `/healthz`          | Active health-check endpoint              |
| `slickgate.order`         | `5`                 | Route priority                            |

## Admin API

| Method | Path        | Description                          |
| ------ | ----------- | ------------------------------------ |
| GET    | `/-/health` | Liveness/health (always open)        |
| GET    | `/-/routes` | Effective routes (file + Docker)     |
| GET    | `/-/config` | Effective configuration              |
| POST   | `/-/reload` | Re-read the config file from disk    |

When `admin.token` is set, all endpoints except `/-/health` require the
`X-Slickgate-Token` header.

## Native AOT

```bash
dotnet publish src/Slickgate.Gateway -c Release -r linux-x64 -p:SlickgateAot=true
```

Or build the container image:

```bash
docker build --target aot -t slickgate:aot .
```

Under Native AOT, YAML is parsed through YamlDotNet's representation model
(no reflection), JSON uses `System.Text.Json` source generators, and the
Certes/BouncyCastle assemblies are rooted because their trimming annotations are
incomplete.

## Project layout

```
src/
  Slickgate.Core/        configuration model, YAML loader, YARP mapping,
                         Docker label parsing + Engine API client
  Slickgate.Gateway/     host bootstrap, certificates/ACME, discovery and
                         reload services, admin API, middleware
tests/
  Slickgate.Core.Tests/  xUnit tests
samples/                 example configs
```

## Comparison with Caddy

| | Caddy | Slickgate |
| --- | --- | --- |
| Language | Go | C# / .NET |
| Config | Caddyfile / JSON | YAML |
| Automatic HTTPS | ✅ | ✅ (ACME HTTP-01) |
| Docker discovery | via plugin | built in (labels) |
| Hot reload | ✅ | ✅ |
| Runtime | single static binary | managed or **Native AOT** binary |
| Ecosystem | mature | early MVP |

## Roadmap

- HTTP/3 and QUIC listeners.
- Zero-copy / `io_uring` fast paths where the platform allows (Kestrel and YARP
  already run on `System.IO.Pipelines`).
- TLS-ALPN-01 and DNS-01 ACME challenges.
- Wildcard certificates and SNI-based cert selection from disk.
- Middleware plugins (rate limiting, auth, caching).

## Status

Early MVP, built as a focused ~1.5–2 week project (AI-assisted). The public
surface (`GatewayConfig`, the admin API, and label names) is still subject to
change.

## License

[MIT](LICENSE) © codemahek
