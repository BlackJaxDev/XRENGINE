# Single-shot ZeroReadback crash repro with breadcrumbs + GL debug.
# Captures stderr to a file, watches for process exit (or timeout), and
# reports the last [CRUMB] line so the killing GL call is identifiable.
param(
    [int]$WarmupSec = 25,
    [int]$CaptureSec = 60,
    [switch]$NoBreadcrumbs
)

$ErrorActionPreference = 'Continue'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $repoRoot 'Build\Editor\Debug\AnyCPU\Debug\net10.0-windows7.0\XREngine.Editor.exe'
if (-not (Test-Path $exe)) { throw "Editor not built: $exe" }

$env:XRE_WORLD_MODE = 'UnitTesting'
$env:XRE_PROFILER_ENABLED = '1'
$env:XRE_FORCE_MESH_SUBMISSION_STRATEGY = 'GpuIndirectZeroReadback'
$env:XRE_GL_DEBUG = '1'
if (-not $NoBreadcrumbs) { $env:XRE_CRASH_BREADCRUMBS = '1' } else { Remove-Item Env:XRE_CRASH_BREADCRUMBS -ErrorAction SilentlyContinue }
$env:XRE_WINDOW_TITLE = 'XRE (ReproZeroReadback)'

$logsDir = Join-Path $repoRoot 'Build\Logs'
$ts = Get-Date -Format 'yyyyMMdd_HHmmss'
$stderrLog = Join-Path $logsDir "repro_stderr_$ts.log"
$stdoutLog = Join-Path $logsDir "repro_stdout_$ts.log"

Write-Host "[repro] launching editor..." -ForegroundColor Cyan
$proc = Start-Process -FilePath $exe -WorkingDirectory $repoRoot -PassThru `
    -RedirectStandardError $stderrLog -RedirectStandardOutput $stdoutLog
Write-Host "[repro] PID=$($proc.Id) stderr=$stderrLog"

$totalSec = $WarmupSec + $CaptureSec
$elapsed = 0
$exitedNaturally = $false
while ($elapsed -lt $totalSec) {
    Start-Sleep -Seconds 2
    $elapsed += 2
    $live = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    if (-not $live) {
        $exitedNaturally = $true
        Write-Host "[repro] process EXITED at +${elapsed}s (likely crash)" -ForegroundColor Yellow
        break
    }
}

if (-not $exitedNaturally) {
    Write-Host "[repro] timeout reached, killing pid $($proc.Id)" -ForegroundColor Yellow
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

# Look for a fresh WER record matching this pid.
$wer = Get-WinEvent -LogName Application -MaxEvents 30 -FilterXPath "*[System[Provider[@Name='Application Error']]]" -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match "Faulting process id: 0x$([Convert]::ToString($proc.Id,16))(?![0-9A-Fa-f])" } |
    Select-Object -First 1

Write-Host ""
Write-Host "=== RESULT ===" -ForegroundColor Green
Write-Host "Pid:           $($proc.Id)"
Write-Host "Exit reason:   $(if ($exitedNaturally) { 'PROCESS DIED' } else { 'TIMEOUT (killed)' })"
Write-Host "WER record:    $(if ($wer) { 'YES (' + $wer.TimeCreated + ')' } else { 'none' })"
if ($wer) {
    if ($wer.Message -match 'Faulting module name: (\S+)') { Write-Host "  Module:      $($matches[1])" }
    if ($wer.Message -match 'Fault offset: (\S+)')          { Write-Host "  Offset:      $($matches[1])" }
    if ($wer.Message -match 'Exception code: (\S+)')        { Write-Host "  Exception:   $($matches[1])" }
}

if (Test-Path $stderrLog) {
    Write-Host ""
    Write-Host "--- last [CRUMB] lines ---" -ForegroundColor Cyan
    $crumbs = Select-String -Path $stderrLog -Pattern '\[CRUMB\]' -SimpleMatch | Select-Object -Last 12
    if ($crumbs) { $crumbs | ForEach-Object { Write-Host "  $($_.Line)" } } else { Write-Host "  (no [CRUMB] lines emitted)" }

    Write-Host ""
    Write-Host "--- last [GLDebug] HIGH lines ---" -ForegroundColor Cyan
    $gldebug = Select-String -Path $stderrLog -Pattern '\[GLDebug\] OPENGL High' | Select-Object -Last 5
    if ($gldebug) { $gldebug | ForEach-Object { Write-Host "  $($_.Line)" } } else { Write-Host "  (no high-severity GL errors)" }
} else {
    Write-Host "stderr log missing: $stderrLog"
}

Write-Host ""
Write-Host "Stderr log: $stderrLog"
