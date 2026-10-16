# Slickgate benchmarks

A head-to-head comparison of **Slickgate** against **Caddy 2.11.7** as a reverse
proxy, measuring throughput, latency, and memory.

Two harnesses are provided:

| Harness           | Runtimes compared                              | Memory metric                          |
| ----------------- | ---------------------------------------------- | -------------------------------------- |
| `run.ps1`         | both proxies as **native Windows** processes   | working set + private bytes (200 ms)   |
| `run-docker.ps1`  | both proxies as **Linux containers** (WSL2)    | `docker stats` + cgroup `memory.peak`  |

Both drive the same load generator ([oha](https://github.com/hatoo/oha) 1.16.0)
against the same shared backend and write `summary.json` plus the raw oha JSON
per proxy under `results/` and `results-docker/`.

## What is measured

- **Throughput** — requests/second (`summary.requestsPerSec`).
- **Latency** — average, p50, p90, p99.
- **Memory** — idle and peak, per the table above.
- **Image/runtime size** — for the container harness.

The backend (`bench/backend`) returns a fixed 1 KiB payload with no per-request
allocation, so the numbers reflect *proxy* overhead only. Everything that costs
work in Slickgate (ACME, Docker polling, telemetry) is disabled in
`bench/slickgate.yaml`; Caddy runs with `admin off`, `auto_https off`.

### Logging is silenced on purpose

Slickgate logs every request at `Information` by default. Writing those lines to
a real console is an I/O bottleneck: on Windows it throttled the gateway to
~600 req/s. Both harnesses set `Logging__LogLevel__Default=Warning` for the
processes/containers they start so the result measures proxy work, not console
I/O. (Caddy has no per-request access log by default, so this levels the field.)

> Heads-up: this is worth knowing in production too — see *Findings* below.

## Running

Requirements: .NET 11 SDK, PowerShell. The Docker harness additionally needs
Docker Desktop (or any Docker engine).

Tools (`caddy`, `oha`) are downloaded into `bench/.tools/` on first run; both
directories are git-ignored.

```powershell
# Native Windows: Slickgate (JIT) vs Caddy
./bench/run.ps1 -Duration 20 -Concurrency 200 -Warmup 5

# Linux containers: Slickgate (JIT + AOT) vs Caddy
./bench/run-docker.ps1 -Duration 20 -Concurrency 200 -Warmup 5

# A single proxy, no rebuild
./bench/run-docker.ps1 -SkipBuild -Proxy 'Caddy (Docker)' -Concurrency 400
```

`run-docker.ps1` builds three images from the repo: the backend
(`bench/docker/backend.Dockerfile`), the load generator
(`bench/docker/loadgen.Dockerfile`, built from `bench/.tools/`), and the two
Slickgate flavors from the root `Dockerfile` (`--target final` and `--target aot`).

## Results

Environment: Windows 11, 16 logical cores. Docker runs on Docker Desktop (WSL2)
with a 16 vCPU / ~15.6 GiB VM. Load = 20 s at 200 concurrent connections,
5 s warm-up, fixed 1 KiB responses. Windows figures are the median of 3 runs;
container figures are from repeated runs of the same settings.

### Native Windows

| Proxy            | req/s  | avg ms | p50 ms | p90 ms | p99 ms | peak WS | peak private |
| ---------------- | -----: | -----: | -----: | -----: | -----: | ------: | ------------ |
| Slickgate (JIT)  | 44,294 |   4.51 |   4.08 |   6.00 |  13.46 |   167 M |        125 M |
| Caddy 2.11.7     | 16,642 |   6.78 |   3.65 |  13.54 |  56.57 |   572 M |        727 M |

### Linux containers (Docker Desktop / WSL2)

20 s @ 200 concurrent connections:

| Proxy                  | req/s  | avg ms | p50 ms | p90 ms | p99 ms | idle   | peak   | image |
| ---------------------- | ------ | ------ | ------ | ------ | ------ | ------ | ------ | ----- |
| Slickgate (JIT)        | 40,323 |  4.93  |  4.64  |   7.20 |  10.43 |  30 M  | 110 M  | 199 M |
| Slickgate (AOT)        | 36,208 |  5.48  |  5.18  |   7.98 |  11.51 |  29 M  | 108 M  | 387 M |
| Caddy 2.11.7           |  4,962 | 40.01  | 12.58  |  59.97 | 523.36 |  14 M  | 134 M  |  93 M |

For reference, the **backend alone** (no proxy) sustains **~159,000 req/s** at
200 concurrency in the same VM, so both proxies — not the origin — are the
bottleneck.

Concurrency sweep (15 s each, same environment):

| Concurrency | Slickgate JIT | Slickgate AOT | Caddy 2.11.7 |
| ----------- | ------------- | ------------- | ------------ |
| 50          | 27,892        | 26,772        | 20,509       |
| 200         | 38,551        | 36,956        | 6,880        |
| 400         | 41,510        | 41,346        | 4,962        |

## Findings

- **Slickgate scales with concurrency; Caddy degrades** in this particular
  container environment. At 50 connections they are close (28k vs 21k), but by
  200+ connections Caddy saturates 14+ vCPUs to serve ~5–7k req/s while
  Slickgate serves ~40k on ~7 vCPUs. Pointing Caddy at the backend by IP instead
  of Docker DNS only partly helped (6.9k → 10.1k), so this is not simply DNS.

  **Caveat:** Caddy is normally one of the most efficient proxies on Linux bare
  metal, so this collapse is very likely an artifact of Go's runtime/network
  stack under the WSL2 virtualized network, not a fair reflection of Caddy.
  Treat the container comparison as environment-specific and re-run on a native
  Linux host for authoritative numbers — the harness is portable.

- **Native Windows is the fairer same-platform comparison**, and there Slickgate
  is ~2.7× Caddy's throughput at a fraction of the memory (125 MB vs 727 MB
  private).

- **Default request logging is a real bottleneck.** Slickgate's per-request
  `Information` logging capped it at ~600 req/s on a hidden Windows console.
  Operators should raise the log level (e.g. `Logging__LogLevel__Default=Warning`)
  in production until a configurable log level lands.

- **Native AOT** boots instantly and runs essentially neck-and-neck with JIT
  (~37–41k vs ~39–42k req/s here) at a similar memory footprint, at the cost of a
  much larger image (387 M vs 199 M) because the AOT toolchain output is a fully
  self-contained native binary.

- **Idle memory** favours Caddy (~14 M vs ~30 M) — expected for a small Go
  binary — but under load Slickgate stays lower in these runs.

## Reproducing on Linux bare metal

The Docker harness works unchanged on a Linux host; only the overhead of the
WSL2 network disappears:

```bash
pwsh ./bench/run-docker.ps1 -Duration 20 -Concurrency 200 -Warmup 5
```

(Or port `run-docker.ps1` to bash; the container images and `bench/docker/`
configs are all that matter.)
