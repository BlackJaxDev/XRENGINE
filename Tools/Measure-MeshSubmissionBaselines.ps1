# Sequential FPS measurement of all three mesh-submission strategies.
# Used by the render-submission perf debug plan section 10.5 step 6.
param(
    [int]$WarmupSec = 25,
    [int]$CaptureSec = 60,
    [string[]]$Strategies = @('CpuDirect','GpuIndirectInstrumented','GpuIndirectZeroReadback')
)

$ErrorActionPreference = 'Continue'
$exe = Join-Path $PSScriptRoot '..\Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.exe'
$exe = (Resolve-Path $exe).Path
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Measure-Strategy {
    param([string]$Strategy)
    Write-Host "[measure] $Strategy launching..." -ForegroundColor Cyan
    $env:XRE_WORLD_MODE = 'UnitTesting'
    $env:XRE_PROFILER_ENABLED = '1'
    $env:XRE_FORCE_MESH_SUBMISSION_STRATEGY = $Strategy
    $env:XRE_WINDOW_TITLE = "XRE Editor (Measure $Strategy)"
    Remove-Item Env:XRE_HIZ_CULL_TRACE -ErrorAction SilentlyContinue

    $proc = Start-Process -FilePath $exe -WorkingDirectory $repoRoot -PassThru
    Write-Host "[measure] $Strategy PID=$($proc.Id) warmup ${WarmupSec}s..."
    Start-Sleep -Seconds $WarmupSec

    $logsRoot = Join-Path $repoRoot 'Build\Logs'
    $logDir = (Get-ChildItem $logsRoot -Recurse -Directory |
        Where-Object { $_.Name -match "pid$($proc.Id)$" } |
        Select-Object -First 1).FullName
    if (-not $logDir) {
        $logDir = (Get-ChildItem $logsRoot -Recurse -Directory |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1).FullName
    }

    Write-Host "[measure] $Strategy capture ${CaptureSec}s log=$logDir"
    $captureStart = Get-Date
    $exitedEarly = $false
    $exitAt = $null
    $exitCode = $null
    for ($s = 0; $s -lt $CaptureSec; $s++) {
        Start-Sleep -Seconds 1
        if ($proc.HasExited) {
            $exitedEarly = $true
            $exitAt = $s
            $exitCode = $proc.ExitCode
            Write-Host "[measure] $Strategy PROCESS EXITED at +${s}s exitCode=0x$([Convert]::ToString($exitCode, 16))" -ForegroundColor Yellow
            break
        }
    }
    $captureEnd = Get-Date

    if (-not $exitedEarly) {
        Write-Host "[measure] $Strategy stopping..."
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
    }

    # Heuristic: real crash = nonzero exit code (NT status) OR WER record for this PID.
    # Manual window-close = exit code 0.
    $crashed = $false
    if ($exitedEarly -and $exitCode -ne 0 -and $exitCode -ne $null) { $crashed = $true }
    if ($exitedEarly) {
        $wer = Get-WinEvent -LogName Application -MaxEvents 80 -ErrorAction SilentlyContinue |
            Where-Object { $_.ProviderName -eq 'Windows Error Reporting' -and $_.Message -match "XREngine\.Editor.*\b$($proc.Id)\b" } |
            Select-Object -First 1
        if ($wer) { $crashed = $true }
    }

    $fpsLog = Join-Path $logDir 'profiler-fps-drops.log'
    if (-not (Test-Path $fpsLog)) {
        $noteMsg = if ($crashed) { "CRASHED at +${exitAt}s" } elseif ($exitedEarly) { "user-closed at +${exitAt}s; no fps log" } else { 'no fps log' }
        return [pscustomobject]@{
            Strategy=$Strategy; Samples=0; Median=$null; P10=$null; P90=$null;
            DropEvents=0; LogDir=$logDir; Note=$noteMsg
        }
    }

    $lines = Get-Content $fpsLog
    $fpsSamples = New-Object System.Collections.Generic.List[double]
    $dropEvents = 0
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match 'CurrentFps:\s*([\d.]+)') {
            $fps = [double]$matches[1]
            $ts = $null
            for ($j = $i; $j -ge [Math]::Max(0, $i-12); $j--) {
                if ($lines[$j] -match '^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
                    try { $ts = [datetime]$matches[1] } catch { $ts = $null }
                    break
                }
            }
            if ($ts -and $ts -ge $captureStart -and $ts -le $captureEnd) {
                $fpsSamples.Add($fps) | Out-Null
                $dropEvents++
            }
        }
    }
    $arr = @($fpsSamples)
    $sorted = $arr | Sort-Object
    $n = $sorted.Count
    $median = if ($n -gt 0) { $sorted[[int]($n/2)] } else { $null }
    $p10 = if ($n -gt 0) { $sorted[[int]($n*0.1)] } else { $null }
    $p90 = if ($n -gt 0) { $sorted[[int]($n*0.9)] } else { $null }
    $note = if ($crashed) { "CRASHED at +${exitAt}s (exit=0x$([Convert]::ToString($exitCode,16)))" } elseif ($exitedEarly) { "user-closed at +${exitAt}s" } elseif ($n -eq 0) { 'no drops in window (above threshold = healthy)' } else { '' }
    return [pscustomobject]@{
        Strategy=$Strategy; Samples=$n; Median=$median; P10=$p10; P90=$p90;
        DropEvents=$dropEvents; LogDir=$logDir; Note=$note
    }
}

$results = New-Object System.Collections.Generic.List[object]
foreach ($s in $Strategies) {
    $r = Measure-Strategy -Strategy $s
    $results.Add($r) | Out-Null
    $r | Format-List
    Write-Host ""
}

Write-Host "=== SUMMARY ===" -ForegroundColor Green
$results | Format-Table -AutoSize Strategy, Samples, DropEvents, Median, P10, P90, Note

# Persist for the plan
$outFile = Join-Path $repoRoot 'Build\Logs\baseline-summary.txt'
$results | Format-Table -AutoSize | Out-String | Set-Content $outFile
Write-Host "Wrote $outFile"
