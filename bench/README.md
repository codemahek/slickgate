# Slickgate benchmarks

A head-to-head comparison of **Slickgate** against **Caddy 2.11.7** and
**HAProxy** as a reverse proxy, measuring throughput, latency, and memory.
Slickgate is measured twice — with .NET 11's `runtime-async` feature **on**
(default) and **off** — so the compiler-lowering speedup can be isolated.

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
allocations, so the numbers reflect *proxy* overhead only. Everything that costs
work in Slickgate (ACME, Docker polling, telemetry) is disabled in
`bench/slickgate.yaml`; Caddy runs with `admin off`, `auto_https off`,
`persist_config off`; HAProxy runs a single frontend/backend with no health
checks or ACLs.

### Logging is silenced on purpose

Per-request `Information` logging is a real I/O bottleneck (on Windows it
throttled the gateway to ~600 req/s), so the gateway now applies a `Warning`
default when no log level is configured. Both harnesses still set
`Logging__LogLevel__Default=Warning` explicitly for the processes/containers
they start — belt and braces, and it keeps the field level with Caddy, which
has no per-request access log by default.

Explicit configuration always wins: `Logging__LogLevel__Default=Information`
restores per-request logs (measured: 1.2 M log lines in 8 s at 19.8k req/s).

## Running

Requirements: .NET 11 SDK, PowerShell. The Docker harness additionally needs
Docker Desktop (or any Docker engine).

Tools (`caddy`, `oha`) are downloaded into `bench/.tools/` on first run; both
directories are git-ignored.

```powershell
# Native Windows: Slickgate (JIT) vs Caddy
./bench/run.ps1 -Duration 20 -Concurrency 200 -Warmup 5

# Linux containers: Slickgate (runtime-async on/off) vs Caddy vs HAProxy
./bench/run-docker.ps1 -Duration 20 -Concurrency 200 -Warmup 5

# A single proxy, no rebuild
./bench/run-docker.ps1 -SkipBuild -Proxy 'Caddy (Docker)' -Concurrency 400

# Just the runtime-async A/B, longer samples
./bench/run-docker.ps1 -SkipBuild -Duration 30 `
    -Proxy @('Slickgate (runtime-async on)', 'Slickgate (runtime-async off)')
```

Known `-Proxy` values: `Slickgate (runtime-async on)`,
`Slickgate (runtime-async off)`, `Slickgate (Docker AOT)`, `Caddy (Docker)`,
`HAProxy (Docker)`.

`run-docker.ps1` builds the backend (`bench/docker/backend.Dockerfile`), the
load generator (`bench/docker/loadgen.Dockerfile`, built from `bench/.tools/`),
and the Slickgate flavors from the root `Dockerfile` (`--target final`, twice —
once per `SlickgateRuntimeAsync` value — plus `--target aot`). Caddy and HAProxy
are pulled and configured from `bench/docker/Caddyfile` and
`bench/docker/haproxy.cfg`.

## Results

Environment: Windows 11, 16 logical cores. Docker runs on Docker Desktop (WSL2)
with a 16 vCPU / ~15.6 GiB VM. Load = fixed 1 KiB responses, 5 s warm-up.
Container figures below are medians of repeated runs (see each table); Windows
figures are the median of 3 runs. Captured 2026-10-18 under
`bench/results-docker/20261018-*`.

### Native Windows

| Proxy            | req/s  | avg ms | p50 ms | p90 ms | p99 ms | peak WS | peak private |
| ---------------- | -----: | -----: | -----: | -----: | -----: | ------: | ------------ |
| Slickgate (JIT)  | 44,294 |   4.51 |   4.08 |   6.00 |  13.46 |   167 M |        125 M |
| Caddy 2.11.7     | 16,642 |   6.78 |   3.65 |  13.54 |  56.57 |   572 M |        727 M |

### Linux containers (Docker Desktop / WSL2)

6 runs × 20 s @ 200 concurrent connections, medians:

| Proxy                        | req/s  | avg ms | p50 ms | p90 ms | p99 ms | idle   | peak   | cgroup | image |
| ---------------------------- | ------ | ------ | ------ | ------ | ------ | ------ | ------ | ------ | ----- |
| Slickgate (runtime-async on) | 38,208 |  5.21  |  4.83  |  7.76  | 11.90  | 32 M   | 108 M  | 118 M  | 199 M |
| Slickgate (runtime-async off)| 36,356 |  5.48  |  5.07  |  8.23  | 12.57  | 31 M   | 111 M  | 115 M  | 199 M |
| HAProxy (`haproxy:alpine`)   | 48,463 |  4.10  |  3.55  |  7.00  | 13.87  | 32 M   |  37 M  |  39 M  |  75 M |
| Caddy 2.11.7                 |  4,913 | 37.35  | 12.38  | 60.65  | 459.41 | 14 M   | 148 M  | 150 M  |  93 M |

For reference, the **backend alone** (no proxy) sustains **~159,000 req/s** at
200 concurrency in the same VM, so the proxies — not the origin — are the
bottleneck.

#### runtime-async A/B (same binary, one build flag)

4 paired runs × 30 s @ 200 connections, Slickgate only (no Caddy competing for
CPU in the same VM):

| Build                  | req/s  | avg ms | p99 ms |
| ---------------------- | ------ | ------ | ------ |
| `SlickgateRuntimeAsync=on`  (default) | 43,296 | 4.60 | 10.21 |
| `SlickgateRuntimeAsync=off`          | 42,750 | 4.66 | 10.31 |

Across all 10 paired samples (6 × 20 s + 4 × 30 s) the median delta is
**+1.5 %** (mean +4.1 %, skewed by one bad `off` run). runtime-async is
performance-neutral: it is free, not a large win.

Concurrency sweep (15 s each; c=200 column is the median of 6 runs above):

| Concurrency | Slickgate async on | Slickgate async off | HAProxy | Caddy 2.11.7 |
| ----------- | ------------------ | ------------------- | ------- | ------------ |
| 50          | 27,914             | 28,617              | 35,754  | 23,566       |
| 200         | 38,208             | 36,356              | 48,463  | 4,913        |
| 400         | 48,151             | 47,692              | 61,285  | 5,307        |

## Findings

- **HAProxy is the fast, small baseline: ~48k req/s at 27 % of Slickgate's
  memory.** It leads every run (48–61k req/s, 37–43 M peak, 75 M image) and
  sits ~0.5 ms ahead of Slickgate on p50. That is the expected shape of the
  result: an event-driven C proxy with no per-request object model versus a
  managed runtime. Slickgate is within ~20 % of HAProxy on throughput while
  costing ~3× the memory — respectable for a .NET gateway, but not a
  contention-free win.

- **Slickgate is 6–8× Caddy in this environment**, on throughput *and* p99
  (12 ms vs 460 ms). At 50 connections they are close (28k vs 24k), but by
  200+ connections Caddy collapses to ~5k req/s while Slickgate and HAProxy
  keep climbing. Pointing Caddy at the backend by IP instead of Docker DNS
  only partly helped historically (6.9k → 10.1k), so this is not simply DNS.

  **Caveat:** Caddy is normally one of the most efficient proxies on Linux bare
  metal, so this collapse is very likely an artifact of Go's runtime/network
  stack under the WSL2 virtualized network, not a fair reflection of Caddy.
  Treat the container comparison as environment-specific and re-run on a native
  Linux host for authoritative numbers — the harness is portable.

- **`runtime-async` is free, not transformative: median +1.5 %** across 10
  paired samples (max observed +22 % on one noisy sample, one sample −2 %).
  Turn it on — it is the default — but do not expect it to change your
  benchmarks. The toggle (`-p:SlickgateRuntimeAsync=off`, or
  `--build-arg` in the Dockerfile) exists so the claim stays honest and
  reproducible.

- **Native Windows is the fairer same-platform comparison** (see above), and
  there Slickgate is ~2.7× Caddy's throughput at a fraction of the memory
  (125 MB vs 727 MB private).

- **Default request logging was a real bottleneck — now fixed in code.**
  Without any configuration the gateway logged every request at `Information`
  to the console: measured in Docker at **14.7k–24.6k req/s** (p99 27–50 ms)
  against ~41k req/s with `Warning`. `Program.cs` now sets a `Warning`
  minimum only when `Logging:LogLevel:Default` is unset, so explicit
  configuration still wins (verified: `-e Logging__LogLevel__Default=Information`
  emits per-request lines again, 1.2 M in 8 s). Out of the box a 15 s run now
  writes 3 log lines instead of ~1 M.

- **Native AOT boots instantly and runs essentially neck-and-neck with JIT**
  (~37–41k vs ~39–42k req/s in earlier runs) at a similar memory footprint, at
  the cost of a much larger image (387 M vs 199 M). It is still selectable with
  `-Proxy 'Slickgate (Docker AOT)'`.

- **Idle memory favours Caddy (~14 M vs ~32 M)** — expected for a small Go
  binary. HAProxy also idles at ~32 M but stays at ~37 M under load where
  Slickgate climbs to ~108–111 M.

- **What the counters say under load.** A `dotnet-counters` / `dotnet-gcdump` /
  `dotnet-trace` session at 200 connections (artifacts in
  `bench/results-docker/20261018-195137-diag`) shows **~8.8 CPU-seconds per
  second** for ~21k req/s (≈ 410 µs of CPU per proxied request, 1.9 of those in
  syscalls) and **~7.4 KB allocated per request** (158 MB/s → a gen0 collection
  every ~0.4 s, 3.1 % of wall time in GC pauses), ~43 lock contentions/s and
  ~154 exceptions/s (mostly `TaskCanceled` from client disconnects, with 100 %
  request success). The diagnostics session itself depresses throughput
  (21k vs 39k clean), so treat these as structural ratios, not benchmark
  numbers.

- **Server GC reservation — not live objects — drives peak memory.** The same
  session's `dotnet-gcdump` shows only **~10.5 M of live objects** (gen0 1 M +
  gen1 6 M + gen2 8 M) against a 105–115 M peak: the rest is Server GC heap
  reservation on a 16-vCPU VM. 3 runs each, same harness settings:

  | GC mode                          | median req/s | peak  | idle |
  | -------------------------------- | -----------: | ----: | ---: |
  | Server GC (default)              |       41,093 | 105 M | 32 M |
  | Workstation GC (`DOTNET_gcServer=0`) |    35,406 |  65 M | 29 M |

  Workstation GC trades ~14 % of peak throughput for **41 % less memory** and
  still clears a home gateway's load by an order of magnitude — the right
  trade where memory is tight (set `DOTNET_gcServer=0` in the container
  environment). `DOTNET_GCHeapCount=2` and `DOTNET_GCConserveMemory=9` moved
  neither number meaningfully.

## Large uploads (TLS + HTTP/1.1)

The tables above measure small, fast responses. Large request bodies behave
differently, and this is where the interesting proxy differences are. Using
`curl --data-binary` of a 100 MiB file against a neutral Kestrel origin that
simply drains the body (all endpoints on the same box, loopback):

| Ingress                 | Slickgate | Caddy 2.11.7 | Origin Kestrel (no proxy) |
| ----------------------- | --------: | -----------: | ------------------------: |
| HTTP/1.1 plaintext      | 488-499   | 520-783      | 533-568                   |
| **HTTPS + HTTP/1.1**    | **254-274** | 506-569    | 526-619                   |
| HTTPS + HTTP/2          | 306-333   | 294-323      | 271-302                   |

(MB/s, 100 MiB upload.) Kestrel alone is fine over TLS; adding Slickgate's proxy
hop halves it, while Caddy does not. The gap is **specific to HTTPS + HTTP/1.1** —
plaintext and HTTP/2 are at parity. `bench/yarp-tls-repro/` reproduces it with
YARP alone (no Slickgate), e.g. 562-638 MB/s read directly vs **107-209 MB/s**
proxied in the same process.

Two Slickgate-specific *timeout* behaviours on long/slow uploads (both fixed):

- **Kestrel `MinRequestBodyDataRate`** (240 B/s over a 5 s grace) dropped clients
  that trickle their body — a 1 KiB/6 s upload died at ~13 s. Caddy has no such
  limit. Fixed in `Program.cs` (`kestrel.Limits.MinRequestBodyDataRate = null`).
- **YARP `ActivityTimeout`** (100 s default) aborted any request idle ≥100 s
  mid-body — a 100 MiB upload with a 120 s stall returned `408` at 100.4 s.
  Slickgate now disables it by default and exposes `server.request_timeout` /
  per-route `timeout` (see README) to set one explicitly. Note Caddy also drops
  very-long-stall uploads (measured: 60 s stall passes, 90 s stall fails), so the
  fixed Slickgate behaviour is the more permissive of the two.

## Reproducing on Linux bare metal

The Docker harness works unchanged on a Linux host; only the overhead of the
WSL2 network disappears:

```bash
pwsh ./bench/run-docker.ps1 -Duration 20 -Concurrency 200 -Warmup 5
```

(Or port `run-docker.ps1` to bash; the container images and `bench/docker/`
configs are all that matter.)
