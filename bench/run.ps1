<#
.SYNOPSIS
    Slickgate vs Caddy head-to-head benchmark (Windows / native).

.DESCRIPTION
    Starts one shared backend (bench/backend), then measures each proxy in turn:

        * throughput  -> requests/second (oha)
        * latency     -> average / p50 / p90 / p99 (oha)
        * memory      -> peak working set + peak private bytes (sampled at 200ms)

    Tools (caddy, oha) are downloaded on first run into bench/.tools.
    Results are written to bench/results/<timestamp>/summary.json.

.EXAMPLE
    .\run.ps1
    .\run.ps1 -Duration 20 -Concurrency 400
    .\run.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [int]$Duration = 10,
    [int]$Concurrency = 200,
    [int]$Warmup = 3,
    [switch]$SkipBuild,
    [switch]$SkipDownload
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Silence per-request console logging for every process this script starts.
# A real (hidden) Windows console is an I/O bottleneck that throttles the
# gateway to a few hundred req/s - that measures console throughput, not proxy
# overhead. Caddy has no per-request access logging by default, so raising the
# level here keeps the comparison apples-to-apples.
$env:Logging__LogLevel__Default = 'Warning'
$env:Logging__LogLevel__Microsoft = 'Warning'

$bench = $PSScriptRoot
$repo = Split-Path -Parent $bench
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$tools = Join-Path $bench '.tools'
$build = Join-Path $bench '.build'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outDir = Join-Path (Join-Path $bench 'results') $stamp

$CaddyVersion = '2.11.7'
$CaddyTag = "v$CaddyVersion"
$OhaVersion = 'v1.16.0'

$BackendPort = 8088
$SlickgatePort = 8090
$CaddyPort = 8091

New-Item -ItemType Directory -Force -Path $tools, $build, $outDir | Out-Null

function Get-Tool {
    param([string]$Name, [string]$Url)
    $dest = Join-Path $tools $Name
    if (Test-Path $dest) { return $dest }
    Write-Host "  downloading $Name ..."
    Invoke-WebRequest -Uri $Url -OutFile $dest
    return $dest
}

if (-not $SkipDownload) {
    $caddyZip = Get-Tool -Name 'caddy.zip' -Url "https://github.com/caddyserver/caddy/releases/download/$CaddyTag/caddy_${CaddyVersion}_windows_amd64.zip"
    $null = Get-Tool -Name 'oha.exe' -Url "https://github.com/hatoo/oha/releases/download/$OhaVersion/oha-windows-amd64.exe"
    if (-not (Test-Path (Join-Path $tools 'caddy.exe'))) {
        Write-Host '  extracting caddy.zip ...'
        Expand-Archive -Path $caddyZip -DestinationPath $tools -Force
    }
}

$caddy = Join-Path $tools 'caddy.exe'
$oha = Join-Path $tools 'oha.exe'
$backendOut = Join-Path $build 'backend'
$gatewayOut = Join-Path $build 'gateway'

if (-not $SkipBuild) {
    Write-Host 'Building backend and gateway (Release) ...'
    & $dotnet publish (Join-Path $bench 'backend\Backend.csproj') -c Release -o $backendOut --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'backend build failed' }

    & $dotnet publish (Join-Path $repo 'src\Slickgate.Gateway\Slickgate.Gateway.csproj') -c Release -o $gatewayOut --nologo -v q /p:UseAppHost=false
    if ($LASTEXITCODE -ne 0) { throw 'gateway build failed' }
}

$startedPids = New-Object System.Collections.Generic.List[int]

function Start-Proc {
    param([string]$File, [string[]]$Arguments)
    $p = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    $startedPids.Add($p.Id) | Out-Null
    return $p
}

function Wait-Ready {
    param([string]$Url, [int]$TimeoutSeconds = 25, [string]$Label)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $null = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 2
            Write-Host "  $Label ready ($Url)"
            return
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }
    throw "$Label did not become ready at $Url"
}

function Invoke-Load {
    param(
        [int]$TargetPid,
        [string]$Url,
        [string]$JsonPath
    )
    $target = Get-Process -Id $TargetPid -ErrorAction Stop
    $safeJson = '"{0}"' -f $JsonPath
    $ohaArgs = @('-z', "${Duration}s", '-c', "$Concurrency", '--output-format', 'json', '-o', $safeJson, '--no-tui', $Url)
    $ohaProc = Start-Process -FilePath $oha -ArgumentList $ohaArgs -PassThru -WindowStyle Hidden

    $maxWs = 0
    $maxPrivate = 0
    while (-not $ohaProc.HasExited) {
        Start-Sleep -Milliseconds 200
        try {
            $target.Refresh()
            if ($target.WorkingSet64 -gt $maxWs) { $maxWs = $target.WorkingSet64 }
            if ($target.PrivateMemorySize64 -gt $maxPrivate) { $maxPrivate = $target.PrivateMemorySize64 }
        } catch { }
    }
    $null = $ohaProc.WaitForExit()

    $target.Refresh()
    $json = Get-Content $JsonPath -Raw | ConvertFrom-Json

    # oha 1.16 JSON: request counts come from the status/error distributions and
    # percentiles live in `latencyPercentiles` (values are seconds).
    $requests = 0L
    foreach ($prop in $json.statusCodeDistribution.PSObject.Properties) { $requests += [long]$prop.Value }
    foreach ($prop in $json.errorDistribution.PSObject.Properties) { $requests += [long]$prop.Value }

    $success = 0.0
    if ($null -ne $json.summary.successRate) { $success = [double]$json.summary.successRate }

    $avgMs = $null
    if ($null -ne $json.summary.average) { $avgMs = [math]::Round([double]$json.summary.average * 1000, 3) }

    $p50 = $null; $p90 = $null; $p99 = $null
    if ($null -ne $json.latencyPercentiles.p50) { $p50 = [math]::Round([double]$json.latencyPercentiles.p50 * 1000, 3) }
    if ($null -ne $json.latencyPercentiles.p90) { $p90 = [math]::Round([double]$json.latencyPercentiles.p90 * 1000, 3) }
    if ($null -ne $json.latencyPercentiles.p99) { $p99 = [math]::Round([double]$json.latencyPercentiles.p99 * 1000, 3) }

    return [pscustomobject]@{
        Requests      = [long]$requests
        Rps           = [math]::Round([double]$json.summary.requestsPerSec, 1)
        AvgMs         = $avgMs
        P50Ms         = $p50
        P90Ms         = $p90
        P99Ms         = $p99
        Success       = $success
        PeakWsMb      = [math]::Round($maxWs / 1MB, 1)
        PeakPrivateMb = [math]::Round($maxPrivate / 1MB, 1)
        EndWsMb       = [math]::Round($target.WorkingSet64 / 1MB, 1)
        EndPrivateMb  = [math]::Round($target.PrivateMemorySize64 / 1MB, 1)
    }
}

function Invoke-ProxyRun {
    param(
        [string]$Name,
        [string]$Url,
        [string]$HealthUrl,
        [scriptblock]$Launch
    )
    Write-Host ''
    Write-Host "=== $Name ===" -ForegroundColor Cyan

    $proc = & $Launch
    if (-not $proc) { throw "$Name failed to start" }

    try {
        Wait-Ready -Url $HealthUrl -Label $Name

        Write-Host "  warmup ${Warmup}s ..."
        $warmArgs = @('-z', "${Warmup}s", '-c', '50', '--output-format', 'quiet', $Url)
        $null = Start-Process -FilePath $oha -ArgumentList $warmArgs -PassThru -WindowStyle Hidden -Wait

        $jsonPath = Join-Path $outDir "$($Name.ToLower()).json"
        Write-Host "  measuring ${Duration}s @ $Concurrency connections ..."
        $result = Invoke-Load -TargetPid $proc.Id -Url $Url -JsonPath $jsonPath
        return [pscustomobject]@{ Proxy = $Name; Result = $result }
    }
    finally {
        if ($proc -and -not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

$report = @()

try {
    Write-Host "Starting shared backend on :$BackendPort ..."
    $null = Start-Proc -File $dotnet -Arguments @((Join-Path $backendOut 'Backend.dll'))
    Wait-Ready -Url "http://127.0.0.1:$BackendPort/health" -Label 'backend'

    $report += Invoke-ProxyRun `
        -Name 'Slickgate (JIT)' `
        -Url "http://127.0.0.1:$SlickgatePort/" `
        -HealthUrl "http://127.0.0.1:$SlickgatePort/-/health" `
        -Launch { Start-Process -FilePath $dotnet -ArgumentList @(
                (Join-Path $gatewayOut 'Slickgate.Gateway.dll'),
                '--config', (Join-Path $bench 'slickgate.yaml')
            ) -PassThru -WindowStyle Hidden }

    $report += Invoke-ProxyRun `
        -Name 'Caddy 2.11.7' `
        -Url "http://127.0.0.1:$CaddyPort/" `
        -HealthUrl "http://127.0.0.1:$CaddyPort/" `
        -Launch { Start-Process -FilePath $caddy -ArgumentList @('run', '--config', (Join-Path $bench 'Caddyfile')) -PassThru -WindowStyle Hidden }
}
finally {
    foreach ($procId in $startedPids) {
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
    }
    Get-NetTCPConnection -LocalPort @($BackendPort, $SlickgatePort, $CaddyPort) -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
}

Write-Host ''
Write-Host ('=' * 84)
Write-Host ("{0,-18} {1,10} {2,9} {3,8} {4,8} {5,8} {6,10} {7,10}" -f 'Proxy', 'req/s', 'avg ms', 'p50 ms', 'p90 ms', 'p99 ms', 'peakWS MB', 'peakPriv MB')
Write-Host ('-' * 84)
foreach ($r in $report) {
    if ($r.Result) {
        Write-Host ("{0,-18} {1,10} {2,9} {3,8} {4,8} {5,8} {6,10} {7,10}" -f `
            $r.Proxy, $r.Result.Rps, $r.Result.AvgMs, $r.Result.P50Ms, $r.Result.P90Ms, $r.Result.P99Ms, $r.Result.PeakWsMb, $r.Result.PeakPrivateMb)
    }
}
Write-Host ('=' * 84)
Write-Host "duration=${Duration}s concurrency=$Concurrency warmup=${Warmup}s"

$summary = [pscustomobject]@{
    timestamp   = $stamp
    duration    = $Duration
    concurrency = $Concurrency
    warmup      = $Warmup
    machine     = $env:COMPUTERNAME
    cores       = [Environment]::ProcessorCount
    results     = $report
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $outDir 'summary.json')
Write-Host "results: $outDir"
