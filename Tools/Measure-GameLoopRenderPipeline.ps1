# Measures the game loop and default render pipeline across mesh-submission strategies.
param(
    [int]$WarmupSec = 25,
    [int]$CaptureSec = 60,
    [int]$Repetitions = 1,
    [string[]]$Strategies = @('CpuDirect', 'GpuIndirectInstrumented', 'GpuIndirectZeroReadback', 'GpuMeshletInstrumented', 'GpuMeshletZeroReadback'),
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('Cold', 'Warm')]
    [string]$CacheMode = 'Cold',
    [ValidateSet('FullBucketScan', 'ActiveBucketList', 'MaterialTable', 'BindlessMaterialTable')]
    [string]$ZeroReadbackMaterialDrawPath = 'FullBucketScan',
    [string]$ProfileScene = '',
    [string]$ProfileCamera = '',
    [string]$ProfileLights = '',
    [string]$ProfileViewport = '',
    [string]$RenderScale = '',
    [string]$GpuClockPolicy = 'Unspecified',
    [double]$TargetRefreshHz = 0,
    [switch]$GpuTimestampDense,
    [switch]$NoClearCachesBetweenVariants,
    [switch]$NoP3Logging,
    [int]$ShutdownGraceSec = 20,
    [int]$NoSampleHangSec = 15,
    [int]$RetainedRunCount = 3,
    [string]$RunLabel = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exe = Join-Path $repoRoot "Build\Editor\$Configuration\AnyCPU\$Configuration\net10.0-windows7.0\XREngine.Editor.exe"
if (-not (Test-Path -LiteralPath $exe)) {
    throw "Editor executable not found for $Configuration. Build XREngine.Editor first: $exe"
}
$exe = (Resolve-Path -LiteralPath $exe).Path

$Strategies = @($Strategies | ForEach-Object {
    [string]$_ -split ','
} | ForEach-Object {
    $_.Trim()
} | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_)
})

$validStrategies = @('CpuDirect', 'GpuIndirectInstrumented', 'GpuIndirectZeroReadback', 'GpuMeshletInstrumented', 'GpuMeshletZeroReadback')
$invalidStrategies = @($Strategies | Where-Object { $validStrategies -notcontains $_ })
if ($invalidStrategies.Count -gt 0) {
    throw "Invalid render path(s): $($invalidStrategies -join ', '). Allowed: $($validStrategies -join ', ')"
}

if ($WarmupSec -lt 0 -or $CaptureSec -le 0 -or $Repetitions -le 0 -or $ShutdownGraceSec -lt 1 -or $NoSampleHangSec -lt 0 -or $RetainedRunCount -lt 1) {
    throw 'WarmupSec must be >= 0, CaptureSec/Repetitions must be > 0, ShutdownGraceSec must be >= 1, NoSampleHangSec must be >= 0, and RetainedRunCount must be >= 1.'
}

function Get-SpeedProfileRoot {
    Join-Path (Join-Path $repoRoot 'Build\Logs') 'speed-profiles\game-loop-render-pipeline'
}

function New-SpeedProfileRunDirectory {
    param([string]$Stamp)

    $profileRoot = Get-SpeedProfileRoot
    if (-not (Test-Path -LiteralPath $profileRoot)) {
        New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
    }

    $profileRunDir = Join-Path $profileRoot $Stamp
    New-Item -ItemType Directory -Path $profileRunDir -Force | Out-Null
    return (Resolve-Path -LiteralPath $profileRunDir).Path
}

function Enforce-SpeedProfileRetention {
    param(
        [string]$ProfileRoot,
        [int]$RetainedRunCount
    )

    if ($RetainedRunCount -lt 1 -or -not (Test-Path -LiteralPath $ProfileRoot)) {
        return
    }

    $rootFullPath = [System.IO.Path]::GetFullPath($ProfileRoot)
    $trimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $rootWithSeparator = $rootFullPath.TrimEnd($trimChars) + [System.IO.Path]::DirectorySeparatorChar

    $runDirectories = @(Get-ChildItem -LiteralPath $rootFullPath -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($runDirectories.Count -le $RetainedRunCount) {
        return
    }

    foreach ($dir in $runDirectories | Select-Object -Skip $RetainedRunCount) {
        $dirFullPath = [System.IO.Path]::GetFullPath($dir.FullName)
        if (-not $dirFullPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to delete speed-profile directory outside profile root: $dirFullPath"
        }

        Remove-Item -LiteralPath $dirFullPath -Recurse -Force
    }
}

function Clear-VariantCaches {
    param([string]$Name)

    if ($NoClearCachesBetweenVariants -or $CacheMode -ne 'Cold') {
        return
    }

    $cacheDir = Join-Path $repoRoot 'Build\Cache\OpenGL\ShaderPrograms'
    $fullPath = [System.IO.Path]::GetFullPath($cacheDir)
    $rootWithSeparator = $repoRoot.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear cache outside repo root: $fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Write-Host "[measure] $Name clearing cache $fullPath" -ForegroundColor DarkGray
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Get-RunLogDir {
    param(
        [int]$EditorProcessId,
        [switch]$AllowFallback
    )

    $logsRoot = Join-Path $repoRoot 'Build\Logs'
    if (-not (Test-Path -LiteralPath $logsRoot)) {
        return $null
    }

    $match = Get-ChildItem -LiteralPath $logsRoot -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "pid$EditorProcessId$" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($match) {
        return $match.FullName
    }

    if ($AllowFallback) {
        $latest = Get-ChildItem -LiteralPath $logsRoot -Recurse -Directory -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if ($latest) {
            return $latest.FullName
        }
    }

    return $null
}

function Set-EnvValue {
    param([string]$Name, [string]$Value)
    Set-Item -Path "Env:$Name" -Value $Value
}

function Assert-EnvOverride {
    param(
        [string]$Name,
        [AllowNull()]
        [string]$Value,
        [string[]]$AllowedValues = @(),
        [switch]$Boolean,
        [switch]$PositiveNumber
    )

    if ($null -eq $Value) {
        $Value = ''
    }

    if ($Boolean) {
        $allowedBooleans = @('0', '1', 'true', 'false', 'yes', 'no', 'on', 'off')
        if ($allowedBooleans -notcontains $Value.ToLowerInvariant()) {
            throw "Invalid $Name='$Value'. Expected a boolean flag: $($allowedBooleans -join ', ')"
        }
    }

    if ($PositiveNumber) {
        [double]$parsed = 0
        if (-not [double]::TryParse($Value, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -or $parsed -le 0) {
            throw "Invalid $Name='$Value'. Expected a positive number."
        }
    }

    if ($AllowedValues.Count -gt 0 -and -not ($AllowedValues | Where-Object { $_ -ieq $Value })) {
        throw "Invalid $Name='$Value'. Allowed: $($AllowedValues -join ', ')"
    }
}

function Set-BenchmarkEnvValue {
    param(
        [string]$Name,
        [string]$Value,
        [string[]]$AllowedValues = @(),
        [switch]$Boolean,
        [switch]$PositiveNumber
    )

    Assert-EnvOverride -Name $Name -Value $Value -AllowedValues $AllowedValues -Boolean:$Boolean -PositiveNumber:$PositiveNumber
    Set-EnvValue -Name $Name -Value $Value
}

function Clear-EnvValue {
    param([string]$Name)
    Remove-Item -Path "Env:$Name" -ErrorAction SilentlyContinue
}

function Test-RenderStatsHung {
    param(
        [string]$LogDir,
        [ref]$LastStatsState,
        [ref]$LastStatsProgressUtc,
        [int]$NoSampleHangSec
    )

    if ($NoSampleHangSec -le 0 -or [string]::IsNullOrWhiteSpace($LogDir)) {
        return $false
    }

    $path = Join-Path $LogDir 'profiler-render-stats.ndjson'
    if (-not (Test-Path -LiteralPath $path)) {
        return $false
    }

    $item = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
    if ($null -eq $item -or $item.Length -le 0) {
        return $false
    }

    $state = "$($item.Length):$($item.LastWriteTimeUtc.Ticks)"
    $now = [datetime]::UtcNow
    if ([string]$LastStatsState.Value -ne $state) {
        $LastStatsState.Value = $state
        $LastStatsProgressUtc.Value = $now
        return $false
    }

    return (($now - ([datetime]$LastStatsProgressUtc.Value)).TotalSeconds -ge $NoSampleHangSec)
}

function Read-AllRenderStatsSamples {
    param([string]$LogDir)

    $samples = New-Object System.Collections.Generic.List[object]
    if ([string]::IsNullOrWhiteSpace($LogDir)) {
        return $samples
    }

    $path = Join-Path $LogDir 'profiler-render-stats.ndjson'
    if (-not (Test-Path -LiteralPath $path)) {
        return $samples
    }

    foreach ($line in Get-Content -LiteralPath $path -ErrorAction SilentlyContinue) {
        $trimmed = $line.Trim()
        if (-not $trimmed.StartsWith('{')) {
            continue
        }

        try {
            $sample = $trimmed | ConvertFrom-Json -ErrorAction Stop
            $samples.Add($sample) | Out-Null
        } catch {
            continue
        }
    }

    return $samples
}

function Select-RenderStatsSamples {
    param(
        [System.Collections.IEnumerable]$Samples,
        [datetime]$CaptureStartUtc,
        [datetime]$CaptureEndUtc
    )

    $selected = New-Object System.Collections.Generic.List[object]
    foreach ($sample in $Samples) {
        try {
            $timestamp = [datetimeoffset]::Parse([string]$sample.ts_utc, [System.Globalization.CultureInfo]::InvariantCulture)
            $utc = $timestamp.UtcDateTime
            if ($utc -ge $CaptureStartUtc -and $utc -le $CaptureEndUtc) {
                $selected.Add($sample) | Out-Null
            }
        } catch {
            continue
        }
    }

    return $selected
}

function Format-SampleTimestamp {
    param([object]$Sample)

    if ($null -eq $Sample) {
        return ''
    }

    $prop = $Sample.PSObject.Properties['ts_utc']
    if (-not $prop -or $null -eq $prop.Value) {
        return ''
    }

    return [string]$prop.Value
}

function Get-SamplePropertyValue {
    param([object]$Sample, [string]$Property)

    if ($null -eq $Sample) {
        return $null
    }

    $prop = $Sample.PSObject.Properties[$Property]
    if (-not $prop) {
        return $null
    }

    return $prop.Value
}

function Get-NumericValues {
    param(
        [System.Collections.IEnumerable]$Samples,
        [string]$Property,
        [switch]$PositiveOnly
    )

    $values = New-Object System.Collections.Generic.List[double]
    foreach ($sample in $Samples) {
        $prop = $sample.PSObject.Properties[$Property]
        if (-not $prop -or $null -eq $prop.Value) {
            continue
        }

        try {
            $value = [double]$prop.Value
        } catch {
            continue
        }

        if ([double]::IsNaN($value) -or [double]::IsInfinity($value)) {
            continue
        }

        if ($PositiveOnly -and $value -le 0.0) {
            continue
        }

        $values.Add($value) | Out-Null
    }

    return ,([double[]]$values.ToArray())
}

function Get-Percentile {
    param([double[]]$SortedValues, [double]$Percentile)

    if ($SortedValues.Count -eq 0) {
        return $null
    }

    $index = [int][Math]::Floor(($SortedValues.Count - 1) * $Percentile)
    $index = [Math]::Max(0, [Math]::Min($SortedValues.Count - 1, $index))
    return [Math]::Round($SortedValues[$index], 3)
}

function Get-NumericStats {
    param(
        [System.Collections.IEnumerable]$Samples,
        [string]$Property,
        [switch]$PositiveOnly
    )

    $values = Get-NumericValues -Samples $Samples -Property $Property -PositiveOnly:$PositiveOnly
    if ($values.Count -eq 0) {
        return [pscustomobject]@{ Count = 0; Avg = $null; Min = $null; Max = $null; P50 = $null; P90 = $null; P95 = $null; P99 = $null }
    }

    $array = [double[]]$values
    [Array]::Sort($array)
    $measure = $array | Measure-Object -Average -Minimum -Maximum

    return [pscustomobject]@{
        Count = $array.Count
        Avg = [Math]::Round([double]$measure.Average, 3)
        Min = [Math]::Round([double]$measure.Minimum, 3)
        Max = [Math]::Round([double]$measure.Maximum, 3)
        P50 = Get-Percentile -SortedValues $array -Percentile 0.50
        P90 = Get-Percentile -SortedValues $array -Percentile 0.90
        P95 = Get-Percentile -SortedValues $array -Percentile 0.95
        P99 = Get-Percentile -SortedValues $array -Percentile 0.99
    }
}

function Sum-NumericProperty {
    param([System.Collections.IEnumerable]$Samples, [string]$Property)

    [double]$sum = 0
    foreach ($sample in $Samples) {
        $prop = $sample.PSObject.Properties[$Property]
        if ($prop -and $null -ne $prop.Value) {
            try { $sum += [double]$prop.Value } catch { }
        }
    }
    return [Math]::Round($sum, 3)
}

function Max-NumericProperty {
    param([System.Collections.IEnumerable]$Samples, [string]$Property)

    $values = Get-NumericValues -Samples $Samples -Property $Property
    if ($values.Count -eq 0) {
        return $null
    }

    return [Math]::Round(($values | Measure-Object -Maximum).Maximum, 3)
}

function Stop-EditorGracefully {
    param(
        [System.Diagnostics.Process]$Process,
        [int]$GraceSeconds
    )

    if ($Process.HasExited) {
        return $false
    }

    Write-Host "[measure] requesting graceful editor shutdown..."
    $requested = $false
    try {
        $requested = $Process.CloseMainWindow()
    } catch {
        $requested = $false
    }

    if ($requested -and $Process.WaitForExit($GraceSeconds * 1000)) {
        return $false
    }

    if (-not $Process.HasExited) {
        Write-Host "[measure] graceful shutdown timed out; forcing process stop" -ForegroundColor Yellow
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        return $true
    }

    return $false
}

function Measure-Variant {
    param([string]$Strategy, [int]$Repetition)

    $labelPrefix = if ([string]::IsNullOrWhiteSpace($RunLabel)) { 'game-loop-render-pipeline' } else { $RunLabel }
    $runName = "$labelPrefix-$Configuration-$CacheMode-$Strategy-r$Repetition"
    Clear-VariantCaches -Name $runName

    $envNames = @(
        'XRE_WORLD_MODE',
        'XRE_PROFILER_ENABLED',
        'XRE_PROFILE_CAPTURE',
        'XRE_PROFILE_AUTO_DUMP',
        'XRE_PROFILE_RUN_LABEL',
        'XRE_FORCE_MESH_SUBMISSION_STRATEGY',
        'XRE_ZERO_READBACK_MATERIAL_DRAW_PATH',
        'XRE_PROFILE_CACHE_MODE',
        'XRE_SHADER_CACHE_MODE',
        'XRE_TEXTURE_CACHE_MODE',
        'XRE_PROFILE_SCENE',
        'XRE_PROFILE_CAMERA',
        'XRE_PROFILE_LIGHTS',
        'XRE_PROFILE_VIEWPORT',
        'XRE_PROFILE_RENDER_SCALE',
        'XRE_PROFILE_WARMUP_SEC',
        'XRE_PROFILE_CAPTURE_SEC',
        'XRE_PROFILE_PHASE',
        'XRE_GPU_CLOCK_POLICY',
        'XRE_TARGET_REFRESH_HZ',
        'XRE_GPU_TIMESTAMP_DENSE',
        'XRE_P3_LOGGING',
        'XRE_WINDOW_TITLE',
        'XRE_BUCKET_LOOP_DRY_RUN',
        'XRE_SKIP_COMMAND_SWAP_IF_CLEAN',
        'XRE_BUCKET_LOOP_SKIP_EMPTY',
        'XRE_FORCE_SINGLE_BUCKET',
        'XRE_HIZ_CULL_TRACE'
    )

    $previousEnv = @{}
    foreach ($name in $envNames) {
        $previousEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }

    $proc = $null
    $captureStartUtc = [datetime]::UtcNow
    $captureEndUtc = $captureStartUtc
    $logDir = $null
    $forcedStop = $false
    $exitedEarly = $false
    $exitAt = $null
    $exitCode = $null
    $exitPhase = ''
    $hangDetected = $false
    $hangPhase = ''
    $hangAt = $null
    $lastStatsState = ''
    $lastStatsProgressUtc = [datetime]::UtcNow

    try {
        Set-BenchmarkEnvValue 'XRE_WORLD_MODE' 'UnitTesting' -AllowedValues @('UnitTesting')
        Set-BenchmarkEnvValue 'XRE_PROFILER_ENABLED' '1' -Boolean
        Set-BenchmarkEnvValue 'XRE_PROFILE_CAPTURE' '1' -Boolean
        Set-BenchmarkEnvValue 'XRE_PROFILE_AUTO_DUMP' '1' -Boolean
        Set-EnvValue 'XRE_PROFILE_RUN_LABEL' $runName
        Set-BenchmarkEnvValue 'XRE_FORCE_MESH_SUBMISSION_STRATEGY' $Strategy -AllowedValues $validStrategies
        Set-BenchmarkEnvValue 'XRE_ZERO_READBACK_MATERIAL_DRAW_PATH' $ZeroReadbackMaterialDrawPath -AllowedValues @('FullBucketScan', 'ActiveBucketList', 'MaterialTable', 'BindlessMaterialTable')
        Set-BenchmarkEnvValue 'XRE_PROFILE_CACHE_MODE' $CacheMode -AllowedValues @('Cold', 'Warm')
        Set-BenchmarkEnvValue 'XRE_SHADER_CACHE_MODE' $CacheMode -AllowedValues @('Cold', 'Warm')
        Set-BenchmarkEnvValue 'XRE_TEXTURE_CACHE_MODE' $CacheMode -AllowedValues @('Cold', 'Warm')
        if ($WarmupSec -gt 0) {
            Set-BenchmarkEnvValue 'XRE_PROFILE_WARMUP_SEC' ([string]$WarmupSec) -PositiveNumber
        } else {
            Clear-EnvValue 'XRE_PROFILE_WARMUP_SEC'
        }
        Set-BenchmarkEnvValue 'XRE_PROFILE_CAPTURE_SEC' ([string]$CaptureSec) -PositiveNumber
        Set-EnvValue 'XRE_PROFILE_PHASE' 'startup-warmup-steady-state'
        Set-EnvValue 'XRE_GPU_CLOCK_POLICY' $GpuClockPolicy
        if ($TargetRefreshHz -gt 0) {
            Set-BenchmarkEnvValue 'XRE_TARGET_REFRESH_HZ' ($TargetRefreshHz.ToString([System.Globalization.CultureInfo]::InvariantCulture)) -PositiveNumber
        } else {
            Clear-EnvValue 'XRE_TARGET_REFRESH_HZ'
        }

        if ($GpuTimestampDense) {
            Set-BenchmarkEnvValue 'XRE_GPU_TIMESTAMP_DENSE' '1' -Boolean
        } else {
            Clear-EnvValue 'XRE_GPU_TIMESTAMP_DENSE'
        }

        if ([string]::IsNullOrWhiteSpace($ProfileScene)) { Clear-EnvValue 'XRE_PROFILE_SCENE' } else { Set-EnvValue 'XRE_PROFILE_SCENE' $ProfileScene }
        if ([string]::IsNullOrWhiteSpace($ProfileCamera)) { Clear-EnvValue 'XRE_PROFILE_CAMERA' } else { Set-EnvValue 'XRE_PROFILE_CAMERA' $ProfileCamera }
        if ([string]::IsNullOrWhiteSpace($ProfileLights)) { Clear-EnvValue 'XRE_PROFILE_LIGHTS' } else { Set-EnvValue 'XRE_PROFILE_LIGHTS' $ProfileLights }
        if ([string]::IsNullOrWhiteSpace($ProfileViewport)) { Clear-EnvValue 'XRE_PROFILE_VIEWPORT' } else { Set-EnvValue 'XRE_PROFILE_VIEWPORT' $ProfileViewport }
        if ([string]::IsNullOrWhiteSpace($RenderScale)) {
            Clear-EnvValue 'XRE_PROFILE_RENDER_SCALE'
        } else {
            Set-BenchmarkEnvValue 'XRE_PROFILE_RENDER_SCALE' $RenderScale -PositiveNumber
        }

        Set-EnvValue 'XRE_WINDOW_TITLE' "XRE Editor (Profile $Strategy r$Repetition)"

        if ($NoP3Logging) {
            Clear-EnvValue 'XRE_P3_LOGGING'
        } else {
            Set-BenchmarkEnvValue 'XRE_P3_LOGGING' '1' -Boolean
        }

        Clear-EnvValue 'XRE_BUCKET_LOOP_DRY_RUN'
        Clear-EnvValue 'XRE_SKIP_COMMAND_SWAP_IF_CLEAN'
        Clear-EnvValue 'XRE_BUCKET_LOOP_SKIP_EMPTY'
        Clear-EnvValue 'XRE_FORCE_SINGLE_BUCKET'
        Clear-EnvValue 'XRE_HIZ_CULL_TRACE'

        Write-Host "[measure] $runName launching..." -ForegroundColor Cyan
        $proc = Start-Process -FilePath $exe -WorkingDirectory $repoRoot -PassThru
        Write-Host "[measure] $runName PID=$($proc.Id) warmup ${WarmupSec}s..."
        for ($second = 0; $second -lt $WarmupSec; $second++) {
            Start-Sleep -Seconds 1
            if ($proc.HasExited) {
                $exitedEarly = $true
                $exitAt = $second
                $exitPhase = 'warmup'
                $exitCode = $proc.ExitCode
                Write-Host "[measure] $runName exited during warmup at +${second}s exitCode=0x$([Convert]::ToString($exitCode, 16))" -ForegroundColor Yellow
                break
            }

            $logDir = Get-RunLogDir -EditorProcessId $proc.Id
            if (Test-RenderStatsHung -LogDir $logDir -LastStatsState ([ref]$lastStatsState) -LastStatsProgressUtc ([ref]$lastStatsProgressUtc) -NoSampleHangSec $NoSampleHangSec) {
                $hangDetected = $true
                $hangPhase = 'warmup'
                $hangAt = $second
                Write-Host "[measure] $runName no render-stats progress for ${NoSampleHangSec}s during warmup; forcing process stop" -ForegroundColor Yellow
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                $proc.WaitForExit(5000) | Out-Null
                $forcedStop = $true
                break
            }
        }

        $logDir = Get-RunLogDir -EditorProcessId $proc.Id

        if (-not $exitedEarly -and -not $hangDetected) {
            Write-Host "[measure] $runName capture ${CaptureSec}s log=$logDir"
            $captureStartUtc = [datetime]::UtcNow
            for ($second = 0; $second -lt $CaptureSec; $second++) {
                Start-Sleep -Seconds 1
                if ($proc.HasExited) {
                    $exitedEarly = $true
                    $exitAt = $second
                    $exitPhase = 'capture'
                    $exitCode = $proc.ExitCode
                    Write-Host "[measure] $runName exited during capture at +${second}s exitCode=0x$([Convert]::ToString($exitCode, 16))" -ForegroundColor Yellow
                    break
                }

                $logDir = Get-RunLogDir -EditorProcessId $proc.Id
                if (Test-RenderStatsHung -LogDir $logDir -LastStatsState ([ref]$lastStatsState) -LastStatsProgressUtc ([ref]$lastStatsProgressUtc) -NoSampleHangSec $NoSampleHangSec) {
                    $hangDetected = $true
                    $hangPhase = 'capture'
                    $hangAt = $second
                    Write-Host "[measure] $runName no render-stats progress for ${NoSampleHangSec}s during capture; forcing process stop" -ForegroundColor Yellow
                    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                    $proc.WaitForExit(5000) | Out-Null
                    $forcedStop = $true
                    break
                }
            }
            $captureEndUtc = [datetime]::UtcNow
        } else {
            $captureStartUtc = [datetime]::UtcNow
            $captureEndUtc = $captureStartUtc
        }

        if (-not $exitedEarly -and -not $hangDetected) {
            $forcedStop = Stop-EditorGracefully -Process $proc -GraceSeconds $ShutdownGraceSec
        }

        $logDir = Get-RunLogDir -EditorProcessId $proc.Id
    } finally {
        foreach ($name in $envNames) {
            if ($null -eq $previousEnv[$name]) {
                Clear-EnvValue $name
            } else {
                Set-EnvValue $name $previousEnv[$name]
            }
        }
    }

    $allSamples = Read-AllRenderStatsSamples -LogDir $logDir
    $samples = Select-RenderStatsSamples -Samples $allSamples -CaptureStartUtc $captureStartUtc -CaptureEndUtc $captureEndUtc
    $render = Get-NumericStats -Samples $samples -Property 'render_dispatch_ms' -PositiveOnly
    $update = Get-NumericStats -Samples $samples -Property 'update_ms' -PositiveOnly
    $collect = Get-NumericStats -Samples $samples -Property 'collect_visible_ms' -PositiveOnly
    $gpu = Get-NumericStats -Samples $samples -Property 'gpu_pipeline_frame_ms' -PositiveOnly
    $gap = Get-NumericStats -Samples $samples -Property 'render_thread_minus_gpu_ms' -PositiveOnly
    $gpuReadyCount = @($samples | Where-Object { $_.gpu_pipeline_timings_ready -eq $true }).Count
    $lastSample = if ($allSamples.Count -gt 0) { $allSamples[$allSamples.Count - 1] } else { $null }
    $lastSampleUtc = Format-SampleTimestamp -Sample $lastSample
    $lastRenderFrameId = Get-SamplePropertyValue -Sample $lastSample -Property 'render_frame_id'
    $lastCompletedFrameId = Get-SamplePropertyValue -Sample $lastSample -Property 'completed_frame_id'
    $lastRenderMs = Get-SamplePropertyValue -Sample $lastSample -Property 'render_dispatch_ms'
    $lastGpuMs = Get-SamplePropertyValue -Sample $lastSample -Property 'gpu_pipeline_frame_ms'
    $lastReadbackBytes = Get-SamplePropertyValue -Sample $lastSample -Property 'gpu_readback_bytes'
    $lastMappedBuffers = Get-SamplePropertyValue -Sample $lastSample -Property 'gpu_mapped_buffers'
    $lastFallbackEvents = Get-SamplePropertyValue -Sample $lastSample -Property 'gpu_cpu_fallback_events'
    $lastForbiddenFallbackEvents = Get-SamplePropertyValue -Sample $lastSample -Property 'forbidden_gpu_fallback_events'
    $captureReadbackTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_readback_bytes'
    $captureMappedTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_mapped_buffers'
    $allReadbackTotal = Sum-NumericProperty -Samples $allSamples -Property 'gpu_readback_bytes'
    $allMappedTotal = Sum-NumericProperty -Samples $allSamples -Property 'gpu_mapped_buffers'
    $allFallbackTotal = Sum-NumericProperty -Samples $allSamples -Property 'gpu_cpu_fallback_events'
    $allForbiddenFallbackTotal = Sum-NumericProperty -Samples $allSamples -Property 'forbidden_gpu_fallback_events'
    $gpuDumpCount = if ($logDir -and (Test-Path -LiteralPath $logDir)) {
        @(Get-ChildItem -LiteralPath $logDir -Filter 'profiler-gpu-pipeline-*.log' -File -ErrorAction SilentlyContinue).Count
    } else {
        0
    }

    $noteParts = New-Object System.Collections.Generic.List[string]
    if ($forcedStop) { $noteParts.Add('forced stop; GPU timing dump may be missing') | Out-Null }
    if ($hangDetected) { $noteParts.Add("no render-stats progress for ${NoSampleHangSec}s during $hangPhase at +${hangAt}s") | Out-Null }
    if ($exitedEarly) { $noteParts.Add("exited early during $exitPhase at +${exitAt}s exit=0x$([Convert]::ToString($exitCode, 16))") | Out-Null }
    if ($samples.Count -eq 0) {
        if ($allSamples.Count -gt 0) {
            $noteParts.Add("no capture-window samples; totalSamples=$($allSamples.Count) lastTs=$lastSampleUtc lastFrame=$lastRenderFrameId lastRenderMs=$lastRenderMs readbackBytes=$lastReadbackBytes fallbackEvents=$lastFallbackEvents forbiddenFallbackEvents=$lastForbiddenFallbackEvents") | Out-Null
        } else {
            $noteParts.Add('no render-stats samples parsed') | Out-Null
        }
    }
    if ($Strategy -eq 'GpuIndirectZeroReadback' -or $Strategy -eq 'GpuMeshletZeroReadback') {
        if ($captureReadbackTotal -ne 0 -or $captureMappedTotal -ne 0 -or $allReadbackTotal -ne 0 -or $allMappedTotal -ne 0) {
            $noteParts.Add("zero-readback violation capture(readbackBytes=$captureReadbackTotal mappedBuffers=$captureMappedTotal) all(readbackBytes=$allReadbackTotal mappedBuffers=$allMappedTotal)") | Out-Null
        }
    }

    return [pscustomobject]@{
        Strategy = $Strategy
        Repetition = $Repetition
        Configuration = $Configuration
        CacheMode = $CacheMode
        ZeroReadbackMaterialDrawPath = $ZeroReadbackMaterialDrawPath
        ProfileScene = $ProfileScene
        ProfileCamera = $ProfileCamera
        ProfileLights = $ProfileLights
        ProfileViewport = $ProfileViewport
        RenderScale = $RenderScale
        GpuClockPolicy = $GpuClockPolicy
        TargetRefreshHz = if ($TargetRefreshHz -gt 0) { $TargetRefreshHz } else { $null }
        GpuTimestampDense = [bool]$GpuTimestampDense
        StartupPhase = 'process-launch-to-first-sample'
        WarmupPhaseSec = $WarmupSec
        SteadyStatePhaseSec = $CaptureSec
        StreamingPhase = 'included-in-startup-and-warmup-until asset counters stabilize'
        Samples = $samples.Count
        AllSamples = $allSamples.Count
        CaptureStartUtc = $captureStartUtc.ToString('O')
        CaptureEndUtc = $captureEndUtc.ToString('O')
        RenderAvgMs = $render.Avg
        RenderP50Ms = $render.P50
        RenderP95Ms = $render.P95
        RenderP99Ms = $render.P99
        UpdateP95Ms = $update.P95
        CollectVisibleP95Ms = $collect.P95
        GpuSamples = $gpu.Count
        GpuReadySamples = $gpuReadyCount
        GpuP50Ms = $gpu.P50
        GpuP95Ms = $gpu.P95
        RenderMinusGpuP95Ms = $gap.P95
        DrawCallsP50 = (Get-NumericStats -Samples $samples -Property 'draw_calls').P50
        MultiDrawCallsP50 = (Get-NumericStats -Samples $samples -Property 'multi_draw_calls').P50
        TrianglesP50 = (Get-NumericStats -Samples $samples -Property 'triangles_rendered').P50
        ShaderProgramSwitchesTotal = Sum-NumericProperty -Samples $samples -Property 'shader_program_switches'
        ProgramPipelineSwitchesTotal = Sum-NumericProperty -Samples $samples -Property 'program_pipeline_switches'
        VaoBindsTotal = Sum-NumericProperty -Samples $samples -Property 'vao_binds'
        TextureBindsTotal = Sum-NumericProperty -Samples $samples -Property 'texture_binds'
        TextureBindSkipsTotal = Sum-NumericProperty -Samples $samples -Property 'texture_bind_skips'
        UniformCallsTotal = Sum-NumericProperty -Samples $samples -Property 'uniform_calls'
        BarrierCallsTotal = Sum-NumericProperty -Samples $samples -Property 'barrier_calls'
        BufferUploadBytesTotal = Sum-NumericProperty -Samples $samples -Property 'buffer_upload_bytes'
        TimestampQueriesTotal = Sum-NumericProperty -Samples $samples -Property 'timestamp_query_count'
        TimestampQueryReadbackBytesTotal = Sum-NumericProperty -Samples $samples -Property 'timestamp_query_readback_bytes'
        VisibleRenderersP50 = (Get-NumericStats -Samples $samples -Property 'visible_renderer_count').P50
        VisibleSubmeshesP50 = (Get-NumericStats -Samples $samples -Property 'visible_submesh_count').P50
        VisibleTrianglesP50 = (Get-NumericStats -Samples $samples -Property 'visible_triangle_count').P50
        MaterialSlotsP50 = (Get-NumericStats -Samples $samples -Property 'material_slot_count').P50
        TextureCountP50 = (Get-NumericStats -Samples $samples -Property 'texture_count').P50
        SkinnedRenderersP50 = (Get-NumericStats -Samples $samples -Property 'skinned_renderer_count').P50
        BoneMatrixUploadBytesTotal = Sum-NumericProperty -Samples $samples -Property 'bone_matrix_upload_bytes'
        BlendshapeWeightUploadBytesTotal = Sum-NumericProperty -Samples $samples -Property 'blendshape_weight_upload_bytes'
        SkinningComputeDispatchTotal = Sum-NumericProperty -Samples $samples -Property 'skinning_compute_dispatch_count'
        BlendshapeComputeDispatchTotal = Sum-NumericProperty -Samples $samples -Property 'blendshape_compute_dispatch_count'
        ShaderVariantsRequestedTotal = Sum-NumericProperty -Samples $allSamples -Property 'shader_variants_requested'
        ShaderVariantsLinkedTotal = Sum-NumericProperty -Samples $allSamples -Property 'shader_variants_linked'
        ShaderVariantsFailedTotal = Sum-NumericProperty -Samples $allSamples -Property 'shader_variants_failed'
        GpuDrivenActiveBucketsP50 = (Get-NumericStats -Samples $samples -Property 'gpu_driven_active_bucket_count').P50
        GpuDrivenEmptyBucketSkipsTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_driven_empty_bucket_skips'
        GpuDrivenFullBucketScansTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_driven_full_bucket_scans'
        GpuDrivenDelayedDiagnosticReadbackBytesTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_driven_delayed_diagnostic_readback_bytes'
        GpuCompactionOverflowTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_compaction_overflow'
        GpuHiZPhaseOneDrawsTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_hiz_phase_one_draws'
        GpuHiZPhaseTwoDrawsTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_hiz_phase_two_draws'
        GpuReadbackBytesTotal = $captureReadbackTotal
        GpuReadbackBytesMaxFrame = Max-NumericProperty -Samples $samples -Property 'gpu_readback_bytes'
        GpuMappedBuffersTotal = $captureMappedTotal
        FallbackEventsTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_cpu_fallback_events'
        ForbiddenFallbackEventsTotal = Sum-NumericProperty -Samples $samples -Property 'forbidden_gpu_fallback_events'
        AllGpuReadbackBytesTotal = $allReadbackTotal
        AllGpuMappedBuffersTotal = $allMappedTotal
        AllFallbackEventsTotal = $allFallbackTotal
        AllForbiddenFallbackEventsTotal = $allForbiddenFallbackTotal
        LastSampleUtc = $lastSampleUtc
        LastRenderFrameId = $lastRenderFrameId
        LastCompletedFrameId = $lastCompletedFrameId
        LastRenderMs = $lastRenderMs
        LastGpuMs = $lastGpuMs
        LastGpuReadbackBytes = $lastReadbackBytes
        LastGpuMappedBuffers = $lastMappedBuffers
        LastFallbackEvents = $lastFallbackEvents
        LastForbiddenFallbackEvents = $lastForbiddenFallbackEvents
        GpuTimingDumpFiles = $gpuDumpCount
        LogDir = $logDir
        Note = ($noteParts -join '; ')
    }
}

$results = New-Object System.Collections.Generic.List[object]
foreach ($strategy in $Strategies) {
    for ($rep = 1; $rep -le $Repetitions; $rep++) {
        $result = Measure-Variant -Strategy $strategy -Repetition $rep
        $results.Add($result) | Out-Null
        $result | Format-List
        Write-Host ''
    }
}

Write-Host '=== GAME LOOP / DEFAULT RENDER PIPELINE SUMMARY ===' -ForegroundColor Green
$results | Format-Table -AutoSize Strategy, Repetition, CacheMode, Samples, AllSamples, RenderP50Ms, RenderP95Ms, RenderP99Ms, GpuP50Ms, GpuP95Ms, DrawCallsP50, VisibleRenderersP50, SkinnedRenderersP50, TextureBindsTotal, GpuReadbackBytesTotal, AllGpuReadbackBytesTotal, GpuDrivenFullBucketScansTotal, FallbackEventsTotal, AllFallbackEventsTotal, LastRenderMs, GpuTimingDumpFiles, Note

$stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
$profileRunDir = New-SpeedProfileRunDirectory -Stamp $stamp
$summaryJson = Join-Path $profileRunDir 'summary.json'
$summaryText = Join-Path $profileRunDir 'summary.txt'
$runLogDirs = Join-Path $profileRunDir 'run-logdirs.txt'
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryJson -Encoding UTF8
@(
    "XRENGINE game loop / default render pipeline profile"
    "Created: $(Get-Date -Format o)"
    "ProfileRunDir: $profileRunDir"
    "Configuration: $Configuration"
    "CacheMode: $CacheMode"
    "ZeroReadbackMaterialDrawPath: $ZeroReadbackMaterialDrawPath"
    "Scene: $ProfileScene"
    "Camera: $ProfileCamera"
    "Lights: $ProfileLights"
    "Viewport: $ProfileViewport"
    "RenderScale: $RenderScale"
    "GpuClockPolicy: $GpuClockPolicy"
    "TargetRefreshHz: $TargetRefreshHz"
    "GpuTimestampDense: $([bool]$GpuTimestampDense)"
    "WarmupSec: $WarmupSec"
    "CaptureSec: $CaptureSec"
    "Phases: startup=process launch to first sample; warmup=$WarmupSec sec; steady-state capture=$CaptureSec sec; streaming is identified by asset upload/shader-variant counters during startup/warmup."
    "Repetitions: $Repetitions"
    "RetainedRunCount: $RetainedRunCount"
    ''
    ($results | Format-Table -AutoSize | Out-String)
) | Set-Content -LiteralPath $summaryText -Encoding UTF8

@($results |
    ForEach-Object { $_.LogDir } |
    Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) } |
    Select-Object -Unique) | Set-Content -LiteralPath $runLogDirs -Encoding UTF8

Enforce-SpeedProfileRetention -ProfileRoot (Get-SpeedProfileRoot) -RetainedRunCount $RetainedRunCount

Write-Host "Wrote $summaryJson"
Write-Host "Wrote $summaryText"
Write-Host "Wrote $runLogDirs"
