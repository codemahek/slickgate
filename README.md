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

- **YAML configuration** — a single `gateway.yaml` is the only source of truth.
  The admin API validates and writes the file; applying changes is an explicit
  reload (`POST /-/reload` / **Save & Apply**), like `caddy reload` — no file
  watcher, and a broken edit never takes the running gateway down.
- **Automatic HTTPS** via ACME (Let's Encrypt et al., HTTP-01), plus a built-in
  `internal` mode that mints self-signed certs per host for local development.
- **Path routing & rewriting** — `path_prefix` / `path_exact` matching,
  `strip_prefix`, and request/response header transforms.
- **Health checks** — active probing per cluster with automatic failover.
- **Admin API** — inspect effective routes/config, edit and validate the config
  file, and trigger reloads at `/-/…` (optionally token-protected).
- **Admin UI** — a standalone Blazor WebAssembly dashboard (its own container)
  for the dashboard, routes, a YAML config editor, and settings, talking to the
  gateway purely over the admin API.
- **Observability** — OpenTelemetry (OTLP) traces/metrics when configured.
- **Native AOT** — `-p:SlickgateAot=true` produces a self-contained native
  binary for fast cold starts and a tiny footprint.

## Quick start

Requires the [.NET 11 SDK](https://dotnet.microsoft.com/) (version 11.0.100-rc.2 or later).

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

Then browse `http://app.localhost:8080` and `http://whoami.localhost:8080`
(both static routes from the bundled config). On
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
  # request_timeout: 5m       # idle timeout for proxied requests; omit or 0 to disable (default)

admin:
  enabled: true
  token: change-me            # omit to leave the admin API open

acme:
  enabled: true
  email: admin@example.com
  directory_url: https://acme-v02.api.letsencrypt.org/directory
  storage_dir: ./certs

tls:                          # used by routes with `tls: file`
  cert_file: ./certs/origin.pem
  key_file: ./certs/origin.key

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
    # timeout: 0                # override request_timeout for this route (0 disables)

  - id: app
    hosts: [app.example.com, www.example.com]
    path_prefix: /
    upstreams:
      - http://127.0.0.1:5002
    tls: auto

  - id: home                 # static file server (no upstream)
    hosts: [example.com, www.example.com]
    path_prefix: /
    static_root: ./www
    tls: file

  - id: nas                  # basic auth in front of an upstream
    host: my-nas.example.com
    upstreams:
      - http://127.0.0.1:81
    tls: file
    basic_auth:
      - user: rishi
        password_hash: $2a$05$s/qPQElSJre9RA82467.qOc.jJgT7X/59vEtY9lHM.44UosArUSZy
```


### Reference

| Section   | Key                    | Default                 | Notes                                            |
| --------- | ---------------------- | ----------------------- | ------------------------------------------------ |
| `server`  | `http_port`            | `80`                    | HTTP listener                                    |
|           | `https_port`           | `443`                   | HTTPS listener                                   |
|           | `redirect_http_to_https` | `true`                | 308 redirect (skips `/-/` and ACME challenges)   |
|           | `request_timeout`      | disabled                | Idle timeout for proxied requests (e.g. `5m`); `0` disables |
| `admin`   | `enabled`              | `true`                  | Mounts the `/-/` endpoints (`/-/health` is always available) |
|           | `token`                | –                       | Sent as `X-Slickgate-Token`                      |
| `acme`    | `enabled`              | `false`                 | Enable Let's Encrypt issuance                    |
|           | `email`                | –                       | Required when `enabled`                          |
|           | `directory_url`        | LE production           | Use the staging URL while testing                |
|           | `storage_dir`          | `./certs`               | Certificates + account key                       |
| `tls`     | `cert_file` / `key_file` | –                     | BYO PEM cert/key used by routes with `tls: file` |
| `route`   | `id`                   | –                       | Required, unique                                 |
|           | `host` / `hosts`       | –                       | At least one of host or `path_*` required        |
|           | `path_prefix` / `path_exact` | –                 | Prefix matches are `{**catch-all}` templates     |
|           | `upstreams`            | –                       | `http(s)://host[:port]`; required unless `static_root` |
|           | `static_root`          | –                       | Serve files from this directory (no upstream)    |
|           | `basic_auth`           | –                       | List of `{user, password_hash}` (bcrypt `$2a$`/`$2y$`) |
|           | `strip_prefix`         | –                       | Removes a prefix before forwarding               |
|           | `tls`                  | `off`                   | `off` \| `internal` (self-signed) \| `auto` (ACME) \| `file` (BYO cert) |
|           | `request_headers.set`  | –                       | Map of header → value                            |
|           | `response_headers.set` | –                       | Map of header → value                            |
|           | `health_check`         | –                       | `path`, `interval`, `timeout`                    |
|           | `timeout`              | `server.request_timeout` | Idle timeout override; `0` disables             |
|           | `order`                | declaration order × 10  | Lower runs first                                 |

## Admin API

| Method | Path                   | Description                             |
| ------ | ---------------------- | --------------------------------------- |
| GET    | `/-/health`            | Liveness/health (always open)           |
| GET    | `/-/routes`            | Effective routes                        |
| GET    | `/-/config`            | Effective configuration                 |
| GET    | `/-/config/raw`        | The raw config file (YAML text)         |
| POST   | `/-/config/validate`   | Validate YAML without writing           |
| PUT    | `/-/config/raw`        | Validate + write the config file        |
| POST   | `/-/reload`            | Apply the saved config file (no downtime) |

When `admin.token` is set, all endpoints except `/-/health` require the
`X-Slickgate-Token` header. `GET /-/config` never echoes the token back (it
reports `***` when one is configured); `GET /-/config/raw` returns the file
verbatim, so it does contain any secrets in it. Nothing watches the file —
changes take effect only when `/-/reload` is called (or the process restarts).

## Admin UI

A standalone Blazor WebAssembly dashboard built with
[MudBlazor](https://mudblazor.com/) and
[ReactiveUI](https://www.reactiveui.net/). It is deployed as its own container
(static files served by nginx) and talks to the gateway only through the admin
API above, so the gateway and UI can be deployed, updated, and scaled
independently.

```bash
docker build -f src/Slickgate.Admin/Dockerfile -t slickgate-admin .
docker run --rm -p 8081:80 slickgate-admin
```

Point it at a gateway from the **Settings** page (base URL + optional token);
the connection is kept in the browser. If the UI is served from a different
origin, add it to `admin.cors_origins` in the gateway config.

Pages: **dashboard** (health + reload), **routes** (effective route table),
**config** (edit, validate, save, and apply `gateway.yaml` from the browser),
and **settings** (edit + test the gateway connection).

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
  Slickgate.Core/        configuration model, YAML loader, YARP mapping
  Slickgate.Gateway/     host bootstrap, certificates/ACME, reload,
                         admin API, middleware
tests/
  Slickgate.Core.Tests/  xUnit tests
samples/                 example configs
```

## Benchmarks

Slickgate is benchmarked head-to-head against Caddy 2.11.7 with
[oha](https://github.com/hatoo/oha) driving a 1 KiB static backend (per-request
logging silenced on both sides). Two harnesses live under
[`bench/`](bench/README.md): `run.ps1` compares both proxies as native Windows
processes, and `run-docker.ps1` compares them as Linux containers.

Representative native Windows run (20 s, 200 concurrent connections, 16-core
host):

| Proxy           | req/s   | avg    | p99    | peak private |
| --------------- | ------- | ------ | ------ | ------------ |
| Slickgate (JIT) | ~44,000 | 4.5 ms | 13 ms  | ~125 MB      |
| Caddy 2.11.7    | ~16,600 | 5.3 ms | 51 ms  | ~730 MB      |

In containers on this machine Slickgate sustained ~40k req/s while Caddy collapsed
to ~5k under high concurrency — but that is almost certainly a WSL2
virtualized-network artifact, not a fair reflection of Caddy. See
[`bench/README.md`](bench/README.md) for the full methodology, the concurrency
sweep, the caveats, and how to reproduce the run on a native Linux host.


## Comparison with Caddy

| | Caddy | Slickgate |
| --- | --- | --- |
| Language | Go | C# / .NET |
| Config | Caddyfile / JSON | YAML |
| Automatic HTTPS | ✅ | ✅ (ACME HTTP-01) |
| Docker discovery | via plugin | no — explicit YAML only |
| Hot reload | ✅ | ✅ (explicit Apply, like `caddy reload`) |
| Runtime | single static binary | managed or **Native AOT** binary |
| Measured performance | see [`bench/`](bench/README.md) | see [`bench/`](bench/README.md) |
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
surface (`GatewayConfig` and the admin API) is still subject to
change.

## License

[MIT](LICENSE) © codemahek
