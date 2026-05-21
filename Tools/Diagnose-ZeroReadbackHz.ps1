# One-off diagnostic harness for the §1 "ZeroReadback runs at 4-7Hz with no lights" question.
# Launches the editor three times with progressively reduced workloads and compares the
# drop-event rate (a proxy for steady-state fps in this regime where every frame is below
# the drop threshold) plus the P3 indirect-call census.
#
# Usage:
#   pwsh Tools\Diagnose-ZeroReadbackHz.ps1 -WarmupSec 25 -CaptureSec 45

param(
    [int]$WarmupSec = 25,
    [int]$CaptureSec = 45,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoClearCachesBetweenVariants
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$logsRoot = Join-Path $repoRoot 'Build\Logs'
$exe = Join-Path $repoRoot "Build\Editor\$Configuration\AnyCPU\$Configuration\net10.0-windows7.0\XREngine.Editor.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Editor executable not found for $Configuration. Build XREngine.Editor first: $exe"
}
$exe = (Resolve-Path -LiteralPath $exe).Path

$validEnumValues = @{
    XRE_OCCLUSION_CULLING_MODE = @('Disabled', 'GpuHiZ', 'CpuQueryAsync', 'CpuSoftwareOcclusion')
    XRE_FORCE_MESH_SUBMISSION_STRATEGY = @('CpuDirect', 'GpuIndirectInstrumented', 'GpuIndirectZeroReadback', 'GpuMeshletInstrumented', 'GpuMeshletZeroReadback')
    XRE_ZERO_READBACK_MATERIAL_DRAW_PATH = @('FullBucketScan', 'ActiveBucketList', 'MaterialTable', 'BindlessMaterialTable')
}

function Assert-ValidVariantEnvironment {
    param([hashtable]$Env)

    foreach ($key in $Env.Keys) {
        if (-not $validEnumValues.ContainsKey($key)) {
            continue
        }

        $value = [string]$Env[$key]
        $allowed = @($validEnumValues[$key])
        if ($allowed -notcontains $value) {
            throw "Invalid $key='$value'. Allowed values: $($allowed -join ', ')"
        }
    }
}

function Clear-VariantCaches {
    param([string]$Name)

    if ($NoClearCachesBetweenVariants) {
        return
    }

    $cacheDirs = @(
        (Join-Path $repoRoot 'Build\Cache\OpenGL\ShaderPrograms')
    )

    foreach ($cacheDir in $cacheDirs) {
        $fullPath = [System.IO.Path]::GetFullPath($cacheDir)
        $rootWithSeparator = $repoRoot.TrimEnd('\') + '\'
        if (-not $fullPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clear cache outside repo root: $fullPath"
        }

        if (Test-Path -LiteralPath $fullPath) {
            Write-Host "[$Name] clearing cache $fullPath" -ForegroundColor DarkGray
            Remove-Item -LiteralPath $fullPath -Recurse -Force
        }
    }
}

function Run-Variant {
    param(
        [string]$Name,
        [hashtable]$Env
    )

    Assert-ValidVariantEnvironment -Env $Env
    Clear-VariantCaches -Name $Name

    Write-Host ""
    Write-Host "========== $Name ($Configuration) ==========" -ForegroundColor Cyan
    foreach ($k in @('XRE_BUCKET_LOOP_DRY_RUN', 'XRE_OCCLUSION_CULLING_MODE', 'XRE_SKIP_COMMAND_SWAP_IF_CLEAN', 'XRE_GL_DEBUG', 'XRE_CRASH_BREADCRUMBS', 'XRE_ZERO_READBACK_MATERIAL_DRAW_PATH')) {
        Remove-Item "Env:$k" -ErrorAction SilentlyContinue
    }
    # Always set:
    $env:XRE_WORLD_MODE = 'UnitTesting'
    $env:XRE_PROFILER_ENABLED = '1'
    $env:XRE_FORCE_MESH_SUBMISSION_STRATEGY = 'GpuIndirectZeroReadback'
    $env:XRE_P3_LOGGING = '1'
    $env:XRE_WINDOW_TITLE = "XRE Editor ($Name)"
    # Variant-specific overrides:
    foreach ($kv in $Env.GetEnumerator()) {
        Set-Item "Env:$($kv.Key)" $kv.Value
    }

    $proc = Start-Process -FilePath $exe -WorkingDirectory $repoRoot -PassThru
    Write-Host "[$Name] PID=$($proc.Id) warmup ${WarmupSec}s..."
    $deadline = (Get-Date).AddSeconds($WarmupSec)
    while ((Get-Date) -lt $deadline -and -not $proc.HasExited) { Start-Sleep -Milliseconds 500 }
    if ($proc.HasExited) {
        Write-Host "[$Name] PROCESS EXITED during warmup at +$([int]((Get-Date) - $proc.StartTime).TotalSeconds)s exit=0x$([Convert]::ToString($proc.ExitCode,16))" -ForegroundColor Yellow
    }

    $logDir = (Get-ChildItem $logsRoot -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "pid$($proc.Id)$" } |
        Select-Object -First 1).FullName
    if (-not $logDir) {
        $logDir = (Get-ChildItem $logsRoot -Recurse -Directory -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1).FullName
    }

    Write-Host "[$Name] capture ${CaptureSec}s log=$logDir"
    $captureStart = Get-Date
    $deadline = (Get-Date).AddSeconds($CaptureSec)
    $exitedEarly = $false
    while ((Get-Date) -lt $deadline -and -not $proc.HasExited) { Start-Sleep -Milliseconds 500 }
    if ($proc.HasExited) { $exitedEarly = $true }
    $captureEnd = Get-Date

    if (-not $exitedEarly) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
    }

    # Drop log analysis
    $fpsLog = Join-Path $logDir 'profiler-fps-drops.log'
    $drops = 0
    $samples = New-Object System.Collections.Generic.List[double]
    if (Test-Path $fpsLog) {
        $lines = Get-Content $fpsLog
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
                    $samples.Add($fps) | Out-Null
                    $drops++
                }
            }
        }
    }
    $sorted = @($samples | Sort-Object)
    $n = $sorted.Count
    $med = if ($n -gt 0) { $sorted[[int]($n/2)] } else { $null }
    $p10 = if ($n -gt 0) { $sorted[[int]($n*0.1)] } else { $null }
    $p90 = if ($n -gt 0) { $sorted[[int]($n*0.9)] } else { $null }
    $hz  = if ($CaptureSec -gt 0) { [math]::Round($drops / $CaptureSec, 2) } else { 0 }

    # P3 census tail
    $p3Log = Join-Path $logDir 'profiler-indirect-calls.log'
    $p3Tail = $null
    if (Test-Path $p3Log) {
        $p3Tail = Get-Content $p3Log | Select-Object -Last 3
    }

    Write-Host "[$Name] drops=$drops/${CaptureSec}s ≈ $hz Hz   median=$med  p10=$p10  p90=$p90"
    if ($p3Tail) {
        Write-Host "[$Name] last P3 census line:" -ForegroundColor DarkGray
        $p3Tail | Select-Object -Last 1 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    } else {
        Write-Host "[$Name] no profiler-indirect-calls.log present" -ForegroundColor DarkYellow
    }

    return [pscustomobject]@{
        Name=$Name; Drops=$drops; ApproxHz=$hz; Median=$med; P10=$p10; P90=$p90;
        LogDir=$logDir; P3Tail=($p3Tail -join ' | ');
        ExitedEarly=$exitedEarly
    }
}

$results = New-Object System.Collections.Generic.List[object]
$results.Add((Run-Variant -Name 'A_baseline'        -Env @{})) | Out-Null
$results.Add((Run-Variant -Name 'B_bucket_dry_run'  -Env @{ XRE_BUCKET_LOOP_DRY_RUN = '1' })) | Out-Null
$results.Add((Run-Variant -Name 'C_no_occlusion'    -Env @{ XRE_OCCLUSION_CULLING_MODE = 'Disabled' })) | Out-Null

Write-Host ""
Write-Host "================ SUMMARY ================" -ForegroundColor Green
$results | Format-Table -AutoSize Name, Drops, ApproxHz, Median, P10, P90

# Persist
$outFile = Join-Path $repoRoot 'Build\Logs\diagnose-zeroreadback-hz.txt'
$results | Format-List | Out-String | Set-Content $outFile
Write-Host "Wrote $outFile"
