<#
.SYNOPSIS
    Slickgate vs Caddy head-to-head benchmark (Linux containers via Docker Desktop / WSL2).

.DESCRIPTION
    The container counterpart to run.ps1. Every component runs in Docker so both
    proxies are measured under an identical Linux runtime:

        bench-backend   fixed 1 KiB payload origin     (bench/docker/backend.Dockerfile)
        bench-proxy     one proxy under test at a time  (root Dockerfile: --target final / aot, or caddy)
        bench-loadgen   oha, driven from inside the same Docker network

    Memory is the headline metric and is reported two ways:

        * idle / peak   -> Docker stats "MemUsage" (working-set-like, cache excluded),
                           peak sampled while the load generator runs
        * cgroup peak   -> exact high-water mark of the container cgroup (best effort)

    Results land in bench/results-docker/<timestamp>/summary.json next to the raw
    oha JSON for each proxy. Compare them with the Windows numbers from run.ps1.

.EXAMPLE
    .\run-docker.ps1
    .\run-docker.ps1 -Duration 20 -Concurrency 400
    .\run-docker.ps1 -SkipBuild -Proxy 'Caddy (Docker)'
#>
[CmdletBinding()]
param(
    [int]$Duration = 10,
    [int]$Concurrency = 200,
    [int]$Warmup = 3,
    [string[]]$Proxy = @('Slickgate (Docker JIT)', 'Slickgate (Docker AOT)', 'Caddy (Docker)'),
    [switch]$SkipBuild,
    [switch]$SkipDownload
)

$bench = $PSScriptRoot
$repo = Split-Path -Parent $bench
$tools = Join-Path $bench '.tools'
$network = 'slickgate-bench'
$backendName = 'bench-backend'
$proxyName = 'bench-proxy'
$loadgenName = 'bench-loadgen'
$backendImage = 'slickgate-bench-backend:dev'
$loadgenImage = 'slickgate-bench-loadgen:dev'
$caddyImage = 'caddy:2.11.7'
$ohaVersion = 'v1.16.0'

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path (Join-Path $bench 'results-docker') $stamp
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

function Assert-Ok([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

function Convert-MemToBytes([string]$text) {
    $text = ($text | Out-String).Trim()
    if ($text -match '^([\d.]+)\s*([KMGT]i?B)') {
        $v = [double]$Matches[1]
        switch -Regex ($Matches[2]) {
            '^KiB$' { $v *= 1KB }
            '^MiB$' { $v *= 1MB }
            '^GiB$' { $v *= 1GB }
            '^TiB$' { $v *= 1TB }
        }
        return [long]$v
    }
    return 0
}

function Get-StatsMemBytes([string]$container) {
    $raw = docker stats --no-stream --format '{{.MemUsage}}' $container 2>$null
    if (-not $raw) { return 0 }
    return (Convert-MemToBytes ($raw -split '/')[0])
}

function Read-OhaResult([string]$jsonPath) {
    $json = Get-Content $jsonPath -Raw | ConvertFrom-Json

    $requests = 0L
    if ($null -ne $json.statusCodeDistribution) {
        foreach ($prop in $json.statusCodeDistribution.PSObject.Properties) { $requests += [long]$prop.Value }
    }
    $errors = 0L
    if ($null -ne $json.errorDistribution) {
        foreach ($prop in $json.errorDistribution.PSObject.Properties) { $errors += [long]$prop.Value }
    }
    $requests += $errors

    $successRate = 0.0
    if ($null -ne $json.summary.successRate) { $successRate = [double]$json.summary.successRate }

    $avgMs = $null
    if ($null -ne $json.summary.average) { $avgMs = [math]::Round([double]$json.summary.average * 1000, 3) }

    $p50 = $null; $p90 = $null; $p99 = $null
    if ($null -ne $json.latencyPercentiles) {
        if ($null -ne $json.latencyPercentiles.p50) { $p50 = [math]::Round([double]$json.latencyPercentiles.p50 * 1000, 3) }
        if ($null -ne $json.latencyPercentiles.p90) { $p90 = [math]::Round([double]$json.latencyPercentiles.p90 * 1000, 3) }
        if ($null -ne $json.latencyPercentiles.p99) { $p99 = [math]::Round([double]$json.latencyPercentiles.p99 * 1000, 3) }
    }

    $errorRate = if ($requests -gt 0) { [math]::Round((1.0 - $successRate), 6) } else { 1.0 }

    return [pscustomobject]@{
        Requests    = [long]$requests
        Rps         = [math]::Round([double]$json.summary.requestsPerSec, 1)
        AvgMs       = $avgMs
        P50Ms       = $p50
        P90Ms       = $p90
        P99Ms       = $p99
        SuccessRate = $successRate
        ErrorRate   = $errorRate
    }
}

function Get-CgroupPeakBytes([string]$container) {
    try {
        $raw = docker exec $container sh -c "cat /sys/fs/cgroup/memory.peak 2>/dev/null || cat /sys/fs/cgroup/memory/memory.max_usage_in_bytes 2>/dev/null" 2>$null
        $raw = ($raw | Out-String).Trim()
        if ($raw -match '^\d+') { return [long]$raw }
    } catch { }
    return 0
}

function Wait-Url([string]$url, [string]$label, [int]$timeoutSeconds = 45) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        docker run --rm --network $network alpine:3.20 wget -q -O /dev/null $url 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Host "  $label ready ($url)"; return }
        Start-Sleep -Milliseconds 300
    }
    throw "$label did not become ready at $url"
}

function Invoke-Load([string]$url, [string]$jsonName) {
    $ohaArgs = @(
        'run', '--rm', '--name', $loadgenName, '--network', $network,
        '-v', "${outDir}:/out",
        $loadgenImage,
        '-z', "${Duration}s", '-c', "$Concurrency",
        '--output-format', 'json', '-o', "/out/$jsonName", '--no-tui', $url
    )
    $proc = Start-Process -FilePath 'docker' -ArgumentList $ohaArgs -PassThru -WindowStyle Hidden

    $maxBytes = 0
    while (-not $proc.HasExited) {
        $m = Get-StatsMemBytes $proxyName
        if ($m -gt $maxBytes) { $maxBytes = $m }
        Start-Sleep -Milliseconds 400
    }
    $proc.WaitForExit()
    if ($proc.ExitCode -ne 0) { throw "oha failed (exit code $($proc.ExitCode))" }

    return [pscustomobject]@{
        MaxBytes = $maxBytes
        JsonPath = Join-Path $outDir $jsonName
    }
}

# --- preflight -----------------------------------------------------------
docker version --format '{{.Server.Version}}' *> $null
Assert-Ok 'Docker is not reachable (is Docker Desktop running?)'

$ohaBin = Join-Path $tools 'oha-linux-amd64'
if (-not (Test-Path $ohaBin)) {
    if ($SkipDownload) { throw "Missing $ohaBin and -SkipDownload was set." }
    New-Item -ItemType Directory -Force -Path $tools | Out-Null
    Write-Host "Downloading oha $ohaVersion (linux-amd64) ..."
    Invoke-WebRequest -Uri "https://github.com/hatoo/oha/releases/download/$ohaVersion/oha-linux-amd64" -OutFile $ohaBin
}

# --- images --------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Host 'Building benchmark images (backend, loadgen, slickgate jit + aot) ...'
    docker build -f bench/docker/backend.Dockerfile -t $backendImage .
    Assert-Ok 'backend image build'
    docker build -f bench/docker/loadgen.Dockerfile -t $loadgenImage bench/.tools
    Assert-Ok 'loadgen image build'
    docker build -t slickgate-bench:jit --target final .
    Assert-Ok 'slickgate (jit) image build'
    docker build -t slickgate-bench:aot --target aot .
    Assert-Ok 'slickgate (aot) image build'
    docker pull $caddyImage
    Assert-Ok 'caddy pull'
}

# --- network + shared backend -------------------------------------------
docker network inspect $network *> $null
if ($LASTEXITCODE -ne 0) { docker network create $network | Out-Null; Assert-Ok 'network create' }
docker rm -f $backendName $proxyName $loadgenName *> $null

Write-Host 'Starting shared backend ...'
docker run -d --name $backendName --network $network $backendImage | Out-Null
Assert-Ok 'backend start'
Wait-Url "http://${backendName}:8088/health" 'backend'

$defs = @{
    'Slickgate (Docker JIT)' = @{
        Image  = 'slickgate-bench:jit'
        Run    = @('--network', $network, '-e', 'Logging__LogLevel__Default=Warning', '-e', 'Logging__LogLevel__Microsoft=Warning', '-v', "${repo}/bench/docker/slickgate.yaml:/etc/slickgate/gateway.yaml:ro")
        Url    = "http://${proxyName}:80/"
        Health = "http://${proxyName}:80/-/health"
    }
    'Slickgate (Docker AOT)' = @{
        Image  = 'slickgate-bench:aot'
        Run    = @('--network', $network, '-e', 'Logging__LogLevel__Default=Warning', '-e', 'Logging__LogLevel__Microsoft=Warning', '-v', "${repo}/bench/docker/slickgate.yaml:/etc/slickgate/gateway.yaml:ro")
        Url    = "http://${proxyName}:80/"
        Health = "http://${proxyName}:80/-/health"
    }
    'Caddy (Docker)' = @{
        Image  = $caddyImage
        Run    = @('--network', $network, '-v', "${repo}/bench/docker/Caddyfile:/etc/caddy/Caddyfile:ro")
        Url    = "http://${proxyName}:80/"
        Health = "http://${proxyName}:80/"
    }
}

$report = @()

foreach ($name in $Proxy) {
    $def = $defs[$name]
    if (-not $def) { Write-Warning "Unknown proxy '$name' - skipping."; continue }

    Write-Host ''
    Write-Host "=== $name ===" -ForegroundColor Cyan

    try {
        $imageSize = (docker image ls $def.Image --format '{{.Size}}' | Select-Object -First 1)

        docker rm -f $proxyName $loadgenName *> $null
        $runArgs = @('run', '-d', '--name', $proxyName) + $def.Run + @($def.Image)
        docker @runArgs | Out-Null
        Assert-Ok "$name start"

        Wait-Url $def.Health $name
        $idleBytes = Get-StatsMemBytes $proxyName

        Write-Host "  warmup ${Warmup}s ..."
        docker run --rm --network $network $loadgenImage -z "${Warmup}s" -c 50 --output-format quiet --no-tui $def.Url *> $null
        Assert-Ok "$name warmup"

        $jsonName = ($name -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLower() + '.json'
        Write-Host "  measuring ${Duration}s @ $Concurrency connections ..."
        $load = Invoke-Load $def.Url $jsonName
        $cgroupPeak = Get-CgroupPeakBytes $proxyName

        $json = Read-OhaResult $load.JsonPath

        $report += [pscustomobject]@{
            Proxy        = $name
            Failed       = $false
            Error        = $null
            Image        = $def.Image
            ImageSize    = $imageSize
            Requests     = $json.Requests
            Rps          = $json.Rps
            AvgMs        = $json.AvgMs
            P50Ms        = $json.P50Ms
            P90Ms        = $json.P90Ms
            P99Ms        = $json.P99Ms
            ErrorRate    = $json.ErrorRate
            IdleMB       = [math]::Round($idleBytes / 1MB, 2)
            PeakStatsMB  = [math]::Round($load.MaxBytes / 1MB, 2)
            PeakCgroupMB = [math]::Round($cgroupPeak / 1MB, 2)
            SuccessRate  = $json.SuccessRate
        }
    }
    catch {
        Write-Warning "$name failed: $($_.Exception.Message)"
        $report += [pscustomobject]@{ Proxy = $name; Failed = $true; Error = $_.Exception.Message; Image = $def.Image }
    }
    finally {
        docker rm -f $proxyName $loadgenName *> $null
    }
}

# --- teardown ------------------------------------------------------------
docker rm -f $backendName $proxyName $loadgenName *> $null

# --- report --------------------------------------------------------------
Write-Host ''
Write-Host ('=' * 104)
Write-Host ("{0,-24} {1,10} {2,8} {3,8} {4,8} {5,8} {6,10} {7,12}" -f 'Proxy', 'req/s', 'avg ms', 'p50 ms', 'p90 ms', 'p99 ms', 'peak MB', 'cgroup MB')
Write-Host ('-' * 104)
foreach ($r in $report) {
    if ($r.Failed) {
        Write-Host ("{0,-24} {1}" -f $r.Proxy, 'FAILED')
    }
    else {
        Write-Host ("{0,-24} {1,10} {2,8} {3,8} {4,8} {5,8} {6,10} {7,12}" -f `
                $r.Proxy, $r.Rps, $r.AvgMs, $r.P50Ms, $r.P90Ms, $r.P99Ms, $r.PeakStatsMB, $r.PeakCgroupMB)
    }
}
Write-Host ('=' * 104)
foreach ($r in $report) {
    if ($r.Failed) { Write-Host ("  {0,-24} {1}" -f $r.Proxy, $r.Error) }
    else { Write-Host ("  {0,-24} idle {1,6} MB   image {2}" -f $r.Proxy, $r.IdleMB, $r.ImageSize) }
}
Write-Host "duration=${Duration}s concurrency=$Concurrency warmup=${Warmup}s"

$summary = [pscustomobject]@{
    timestamp   = $stamp
    runtime     = 'docker (linux)'
    duration    = $Duration
    concurrency = $Concurrency
    warmup      = $Warmup
    machine     = $env:COMPUTERNAME
    cores       = "$([Environment]::ProcessorCount) (host)"
    results     = $report
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $outDir 'summary.json')
Write-Host "results: $outDir"
