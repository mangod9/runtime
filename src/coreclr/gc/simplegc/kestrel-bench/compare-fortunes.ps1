# Cross-GC comparison harness for kestrel-bench /fortunes endpoint.
#
# Runs the same /fortunes workload under different GC configurations and
# memory caps, parsing kestrel-bench's stdout for rps / failures / latency
# / peakWS / GC telemetry. Produces a markdown comparison table.
#
# Usage examples:
#   .\compare-fortunes.ps1                              # default matrix
#   .\compare-fortunes.ps1 -C 16 -N 20000               # custom load
#   .\compare-fortunes.ps1 -Modes default,simplegc      # specific modes
#   .\compare-fortunes.ps1 -CapsMb 0,1024,512,256       # specific caps
#   .\compare-fortunes.ps1 -Endpoint search             # different endpoint
#
# Notes:
# - simplegc.dll must exist next to kestrel-bench.exe before this runs.
#   (`dotnet build -c Release` copies it as part of the kestrel-bench
#   build via the post-build step in the csproj — verify if missing.)
# - Each run is a fresh process, which is required because the GC selection
#   is bound at runtime startup.

[CmdletBinding()]
param(
    [string[]] $Modes    = @('default', 'simplegc'),
    [long[]]   $CapsMb   = @(0, 1024, 512),
    [int]      $C        = 8,
    [int]      $N        = 10000,
    [string]   $Endpoint = 'fortunes',
    [string]   $BenchExe = (Join-Path $PSScriptRoot 'bin\Release\net10.0\kestrel-bench.exe'),
    [int]      $WarmupSeconds = 0
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $BenchExe)) {
    Write-Error "kestrel-bench.exe not found at: $BenchExe`nBuild first: dotnet build -c Release"
    return
}

$dllPath = Join-Path (Split-Path $BenchExe) 'simplegc.dll'
$simplegcAvailable = Test-Path $dllPath
if (-not $simplegcAvailable) {
    Write-Warning "simplegc.dll not found next to kestrel-bench.exe — simplegc modes will be skipped."
}

# Patterns for parsing kestrel-bench output. Lines look like:
#   "  requests : 10,000"
#   "  failures : 0"
#   "  rps      :      37376"
#   "  p50      :   123.45 us"
#   "  peakWS   :     203.4 MB"
#   "  alloc    :    1234.5 MB"
function Parse-BenchOutput {
    param([string[]] $Lines)

    $out = [ordered]@{
        Requests = $null
        Failures = $null
        Rps      = $null
        P50Us    = $null
        P99Us    = $null
        MaxUs    = $null
        PeakWsMb = $null
        AllocMb  = $null
        Gen0     = $null
        Gen1     = $null
        Gen2     = $null
        SimplegcGcCount = $null
        SimplegcPermUsedMb = $null
        Failed   = $false
    }

    foreach ($line in $Lines) {
        if ($line -match '^\s*requests\s*:\s*([\d,]+)')      { $out.Requests = [long]($Matches[1] -replace ',') }
        elseif ($line -match '^\s*failures\s*:\s*([\d,]+)')  { $out.Failures = [long]($Matches[1] -replace ',') }
        elseif ($line -match '^\s*rps\s*:\s*([\d.,]+)')      { $out.Rps      = [double]($Matches[1] -replace ',') }
        elseif ($line -match '^\s*p50\s*:\s*([\d.]+)\s*us')  { $out.P50Us    = [double]$Matches[1] }
        elseif ($line -match '^\s*p99\s*:\s*([\d.]+)\s*us')  { $out.P99Us    = [double]$Matches[1] }
        elseif ($line -match '^\s*max\s*:\s*([\d.]+)\s*us')  { $out.MaxUs    = [double]$Matches[1] }
        elseif ($line -match '^\s*peakWS\s*:\s*([\d.]+)\s*MB') { $out.PeakWsMb = [double]$Matches[1] }
        elseif ($line -match '^\s*alloc\s*:\s*([\d.]+)\s*MB')  { $out.AllocMb  = [double]$Matches[1] }
        elseif ($line -match '^\s*gen0\s*:\s*([\d]+)')       { $out.Gen0     = [int]$Matches[1] }
        elseif ($line -match '^\s*gen1\s*:\s*([\d]+)')       { $out.Gen1     = [int]$Matches[1] }
        elseif ($line -match '^\s*gen2\s*:\s*([\d]+)')       { $out.Gen2     = [int]$Matches[1] }
        elseif ($line -match '^\s*gcCount\s*:\s*([\d]+)')    { $out.SimplegcGcCount = [int]$Matches[1] }
        elseif ($line -match '^\s*perm used\s*:\s*([\d.]+)\s*MB') { $out.SimplegcPermUsedMb = [double]$Matches[1] }
    }

    return $out
}

function Run-One {
    param(
        [string] $Mode,
        [long]   $CapMb,
        [int]    $C,
        [int]    $N,
        [string] $Endpoint
    )

    $label = "{0,-9} cap={1,5} c={2,3} n={3,6}" -f $Mode, ($CapMb -gt 0 ? "${CapMb}MB" : 'none'), $C, $N
    Write-Host -NoNewline "  > $label ... " -ForegroundColor Cyan

    # Build env block
    $envBlock = @{}
    if ($Mode -eq 'simplegc') {
        $envBlock['DOTNET_GCName']           = 'simplegc.dll'
        $envBlock['DOTNET_StandaloneGCName'] = 'simplegc.dll'
        # NOTE: SIMPLEGC_USE_ARENA bracket retired (see Server.Build) —
        # set it for parity with M1j.2 reproducer but it is now a no-op
        # in the kestrel-bench binary.
    }
    # else 'default': leave env empty so runtime uses regular GC.

    $args = @('--ep', $Endpoint, '--c', $C, '--n', $N)
    if ($CapMb -gt 0) { $args += @('--mem-mb', $CapMb) }

    # Apply env, run, capture, restore. Snapshot prior values for restore.
    $prior = @{}
    foreach ($k in @('DOTNET_GCName','DOTNET_StandaloneGCName')) {
        $prior[$k] = [Environment]::GetEnvironmentVariable($k)
    }
    foreach ($kv in $envBlock.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($kv.Key, $kv.Value)
    }

    $stdout = New-TemporaryFile
    $stderr = New-TemporaryFile
    $exit   = -1
    $sw     = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $p = Start-Process -FilePath $BenchExe -ArgumentList $args `
            -NoNewWindow -PassThru `
            -RedirectStandardOutput $stdout -RedirectStandardError $stderr
        # Wait up to 10 minutes — should always finish in <1 minute for
        # standard --c / --n. Catches hangs.
        if (-not $p.WaitForExit(10 * 60 * 1000)) {
            try { $p.Kill($true) } catch {}
            $exit = -2
        } else {
            $exit = $p.ExitCode
        }
    }
    finally {
        $sw.Stop()
        # Restore env
        foreach ($k in @('DOTNET_GCName','DOTNET_StandaloneGCName')) {
            [Environment]::SetEnvironmentVariable($k, $prior[$k])
        }
    }

    $stdoutLines = if (Test-Path $stdout) { Get-Content $stdout } else { @() }
    $stderrLines = if (Test-Path $stderr) { Get-Content $stderr } else { @() }
    Remove-Item $stdout, $stderr -ErrorAction SilentlyContinue

    $parsed = Parse-BenchOutput -Lines $stdoutLines

    $row = [PSCustomObject][ordered]@{
        Mode      = $Mode
        CapMb     = $CapMb
        C         = $C
        N         = $N
        Exit      = $exit
        WallSec   = [Math]::Round($sw.Elapsed.TotalSeconds, 2)
        Requests  = $parsed.Requests
        Failures  = $parsed.Failures
        Rps       = if ($parsed.Rps) { [int]$parsed.Rps } else { $null }
        P50Us     = $parsed.P50Us
        P99Us     = $parsed.P99Us
        MaxUs     = $parsed.MaxUs
        PeakWsMb  = $parsed.PeakWsMb
        AllocMb   = $parsed.AllocMb
        Gen0      = $parsed.Gen0
        Gen1      = $parsed.Gen1
        Gen2      = $parsed.Gen2
        SimplegcGc = $parsed.SimplegcGcCount
        SimplegcPermMb = $parsed.SimplegcPermUsedMb
        StdoutTail = ($stdoutLines | Select-Object -Last 5) -join ' | '
        StderrTail = ($stderrLines | Select-Object -Last 3) -join ' | '
    }

    if ($exit -ne 0) {
        Write-Host "FAILED (exit=$exit)" -ForegroundColor Red
    } elseif ($parsed.Requests -eq $null -or $parsed.Failures -gt 0) {
        Write-Host "PARTIAL (failures=$($parsed.Failures))" -ForegroundColor Yellow
    } else {
        Write-Host ("OK rps={0,7} peakWs={1,7:F1}MB p99={2,7:F0}us" -f $row.Rps, $row.PeakWsMb, $row.P99Us) -ForegroundColor Green
    }

    return $row
}

# ---- Run matrix ----
Write-Host ""
Write-Host "kestrel-bench cross-GC comparison" -ForegroundColor White
Write-Host "  endpoint = $Endpoint"
Write-Host "  c = $C, n = $N"
Write-Host "  modes = $($Modes -join ', ')"
Write-Host "  caps  = $(($CapsMb | ForEach-Object { if ($_ -gt 0) { "${_}MB" } else { 'none' } }) -join ', ')"
Write-Host ""

$results = New-Object System.Collections.Generic.List[object]
foreach ($mode in $Modes) {
    if ($mode -eq 'simplegc' -and -not $simplegcAvailable) {
        Write-Warning "Skipping mode '$mode' — simplegc.dll missing."
        continue
    }
    foreach ($cap in $CapsMb) {
        $row = Run-One -Mode $mode -CapMb $cap -C $C -N $N -Endpoint $Endpoint
        [void]$results.Add($row)
    }
}

# ---- Render comparison table ----
Write-Host ""
Write-Host "=== Comparison ===" -ForegroundColor White
Write-Host ""

$results | Format-Table Mode, CapMb, C, N, Exit, Requests, Failures, Rps, P50Us, P99Us, PeakWsMb, AllocMb, Gen0, Gen1, Gen2, SimplegcGc -AutoSize

# ---- Markdown table for plan.md / committed write-up ----
$md = New-Object System.Collections.Generic.List[string]
[void]$md.Add("| mode | cap | c | n | exit | reqs | fails | rps | p50us | p99us | peakWS MB | alloc MB | gen0 | gen1 | gen2 |")
[void]$md.Add("|------|-----|---|---|------|------|-------|-----|-------|-------|-----------|----------|------|------|------|")
foreach ($r in $results) {
    $cap = if ($r.CapMb -gt 0) { "$($r.CapMb)MB" } else { '—' }
    $reqs = if ($r.Requests -ne $null) { "{0:N0}" -f $r.Requests } else { '—' }
    $rps  = if ($r.Rps      -ne $null) { "{0:N0}" -f $r.Rps      } else { '—' }
    $p50  = if ($r.P50Us    -ne $null) { "{0:N1}" -f $r.P50Us    } else { '—' }
    $p99  = if ($r.P99Us    -ne $null) { "{0:N1}" -f $r.P99Us    } else { '—' }
    $pws  = if ($r.PeakWsMb -ne $null) { "{0:N1}" -f $r.PeakWsMb } else { '—' }
    $al   = if ($r.AllocMb  -ne $null) { "{0:N1}" -f $r.AllocMb  } else { '—' }
    $g0 = $r.Gen0; if ($g0 -eq $null) { $g0 = '—' }
    $g1 = $r.Gen1; if ($g1 -eq $null) { $g1 = '—' }
    $g2 = $r.Gen2; if ($g2 -eq $null) { $g2 = '—' }
    [void]$md.Add(("| {0} | {1} | {2} | {3} | {4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {12} | {13} | {14} |" -f `
        $r.Mode, $cap, $r.C, $r.N, $r.Exit, $reqs, $r.Failures, $rps, $p50, $p99, $pws, $al, $g0, $g1, $g2))
}

Write-Host ""
Write-Host "=== Markdown table ===" -ForegroundColor White
$md | ForEach-Object { Write-Host $_ }

# ---- CSV alongside the binaries for archival ----
$csvPath = Join-Path (Split-Path $BenchExe) 'compare-fortunes.csv'
$results | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
Write-Host ""
Write-Host "CSV: $csvPath" -ForegroundColor DarkGray
