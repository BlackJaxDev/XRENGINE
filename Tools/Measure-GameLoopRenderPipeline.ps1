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
    [switch]$FailOnSteadyStateResourceChurn,
    [int]$MaxSteadyStateVulkanLiveResources = 50000,
    [int]$MaxSteadyStateVulkanDescriptorSets = 25000,
    [switch]$FailOnSteadyStateCommandBufferChurn,
    [switch]$FailOnSteadyStateCommandBufferAllocations,
    [double]$MinSteadyStateCommandBufferCleanReuseRatio = 0,
    [long]$MaxSteadyStateRecordCommandBufferAllocatedBytes = 0,
    [int]$StabilityWindowSec = 5,
    [int]$StabilityTimeoutSec = 120,
    [switch]$NoStabilityGate,
    [int]$ShutdownGraceSec = 20,
    [int]$NoSampleHangSec = 15,
    [int]$RetainedRunCount = 3,
    [string]$RunLabel = '',
    [ValidateSet('Configured', 'Desktop', 'Emulated', 'MonadoOpenXR', 'OpenVR', 'OpenXR')]
    [string]$UnitTestVrMode = 'Configured',
    [ValidateSet('Configured', 'DynamicRendering', 'LegacyRenderPass')]
    [string]$VulkanRenderTargetMode = 'Configured',
    [ValidateSet('Configured', 'Enabled', 'Disabled')]
    [string]$VulkanPrimaryReuse = 'Configured',
    [ValidateSet('Configured', 'Enabled', 'Disabled')]
    [string]$VulkanCommandChains = 'Configured',
    [ValidateSet('Configured', 'Enabled', 'Disabled')]
    [string]$VulkanParallelCommandChainRecording = 'Configured',
    [ValidateSet('Configured', 'Enabled', 'Disabled')]
    [string]$VulkanParallelSecondaryRecording = 'Configured',
    [ValidateSet('Configured', 'Disabled', 'CpuQueryAsync', 'CpuSoftwareOcclusion', 'GpuHiZ')]
    [string]$OcclusionCullingMode = 'Configured',
    [ValidateSet('Configured', 'Off', 'StandardValidation', 'SyncValidation', 'GpuAssisted', 'BestPractices', 'CrashDiagnostics', 'RenderDocFriendly')]
    [string]$VulkanDiagnosticPreset = 'Configured',
    [switch]$VulkanCommandBufferLabels,
    [switch]$VulkanValidation
)

$ErrorActionPreference = 'Stop'

if (-not ('XREngineMeasurementNativeWindow' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class XREngineMeasurementNativeWindow
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static bool PostCloseToProcess(int processId)
    {
        bool posted = false;
        EnumWindows((window, state) =>
        {
            uint ownerProcessId;
            GetWindowThreadProcessId(window, out ownerProcessId);
            if (ownerProcessId == (uint)processId)
                posted |= PostMessage(window, 0x0010u, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return posted;
    }
}
'@
}
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

if ($WarmupSec -lt 0 -or $CaptureSec -le 0 -or $Repetitions -le 0 -or $ShutdownGraceSec -lt 1 -or $NoSampleHangSec -lt 0 -or $RetainedRunCount -lt 1 -or $StabilityWindowSec -lt 1 -or $StabilityTimeoutSec -lt 1 -or $MinSteadyStateCommandBufferCleanReuseRatio -lt 0 -or $MinSteadyStateCommandBufferCleanReuseRatio -gt 1 -or $MaxSteadyStateVulkanLiveResources -lt 1 -or $MaxSteadyStateVulkanDescriptorSets -lt 1) {
    throw 'WarmupSec must be >= 0, CaptureSec/Repetitions must be > 0, ShutdownGraceSec/StabilityWindowSec/StabilityTimeoutSec must be >= 1, NoSampleHangSec must be >= 0, RetainedRunCount must be >= 1, and MinSteadyStateCommandBufferCleanReuseRatio must be between 0 and 1.'
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

function Test-RenderStatsStability {
    param(
        [string]$LogDir,
        [int]$WindowSec,
        [string]$Strategy
    )

    $allSamples = Read-AllRenderStatsSamples -LogDir $LogDir
    if ($allSamples.Count -lt 2) {
        return [pscustomobject]@{ Stable = $false; Reason = 'waiting for profiler samples'; WorkloadIdentityHash = ''; Samples = 0 }
    }

    $timestamped = New-Object System.Collections.Generic.List[object]
    foreach ($sample in $allSamples) {
        try {
            $timestamp = [datetimeoffset]::Parse([string]$sample.ts_utc, [System.Globalization.CultureInfo]::InvariantCulture)
            $timestamped.Add([pscustomobject]@{ Sample = $sample; Utc = $timestamp.UtcDateTime }) | Out-Null
        } catch { }
    }
    if ($timestamped.Count -lt 2) {
        return [pscustomobject]@{ Stable = $false; Reason = 'waiting for timestamped profiler samples'; WorkloadIdentityHash = ''; Samples = 0 }
    }

    $latestUtc = $timestamped[$timestamped.Count - 1].Utc
    # Include one extra second so the first retained sample brackets the requested
    # interval instead of always landing just after its exact boundary.
    $windowStartUtc = $latestUtc.AddSeconds(-($WindowSec + 1))
    $window = @($timestamped | Where-Object { $_.Utc -ge $windowStartUtc })
    $observedSec = ($latestUtc - $window[0].Utc).TotalSeconds
    if ($observedSec -lt $WindowSec -or $window.Count -lt 2) {
        return [pscustomobject]@{ Stable = $false; Reason = "collecting stability window ($([Math]::Round($observedSec, 1))/${WindowSec}s)"; WorkloadIdentityHash = ''; Samples = $window.Count }
    }

    $samples = @($window | ForEach-Object { $_.Sample })
    $hashes = @($samples | ForEach-Object {
        $value = Get-SamplePropertyValue -Sample $_ -Property 'frame_output_workload_identity_hash'
        if ($null -ne $value -and [string]$value -ne '0') { [string]$value }
    } | Select-Object -Unique)
    if ($hashes.Count -ne 1) {
        return [pscustomobject]@{ Stable = $false; Reason = "workload identity changed ($($hashes.Count) identities)"; WorkloadIdentityHash = ($hashes -join ','); Samples = $samples.Count }
    }

    $emptyOutputSamples = @($samples | Where-Object {
        $value = Get-SamplePropertyValue -Sample $_ -Property 'frame_output_request_count'
        $null -eq $value -or [double]$value -le 0
    }).Count
    if ($emptyOutputSamples -gt 0) {
        return [pscustomobject]@{ Stable = $false; Reason = "output manifest incomplete ($emptyOutputSamples empty samples)"; WorkloadIdentityHash = $hashes[0]; Samples = $samples.Count }
    }

    $quietProperties = @(
        'texture_upload_jobs',
        'texture_upload_bytes',
        'shader_variants_requested',
        'shader_variants_warming',
        'shader_variants_linked',
        'vulkan_retired_resource_plan_replacements',
        'vulkan_retired_resource_plan_images',
        'vulkan_retired_resource_plan_buffers',
        'vulkan_descriptor_pool_create_count',
        'vulkan_retired_descriptor_pool_count',
        'vulkan_retired_descriptor_set_count',
        'vulkan_retired_command_buffer_count',
        'vulkan_retired_query_pool_count',
        'vulkan_retired_buffer_view_count',
        'vulkan_retired_pipeline_count',
        'vulkan_retired_framebuffer_count',
        'vulkan_retired_image_count',
        'vulkan_retired_image_view_count',
        'vulkan_retired_sampler_count',
        'vulkan_retired_image_memory_count',
        'frame_output_planner_prune_count',
        'frame_output_global_in_flight_wait_count',
        'frame_output_force_flush_count'
    )
    # GPU-driven strategies stream their current indirect command payload through
    # one bounded staging buffer per frame. Those retirements are steady upload
    # work, not evidence that startup/resource publication is still changing.
    if ($Strategy -notin @('GpuIndirectInstrumented', 'GpuIndirectZeroReadback', 'GpuMeshletInstrumented', 'GpuMeshletZeroReadback')) {
        $quietProperties += 'vulkan_retired_buffer_count'
        $quietProperties += 'vulkan_retired_buffer_memory_count'
    }
    $busy = New-Object System.Collections.Generic.List[string]
    foreach ($property in $quietProperties) {
        $total = Sum-NumericProperty -Samples $samples -Property $property
        if ($total -gt 0) {
            $busy.Add("$property=$total") | Out-Null
        }
    }
    if ($busy.Count -gt 0) {
        return [pscustomobject]@{ Stable = $false; Reason = "startup/resource work active: $($busy -join ', ')"; WorkloadIdentityHash = $hashes[0]; Samples = $samples.Count }
    }

    $unapproved = Sum-NumericProperty -Samples $samples -Property 'frame_output_unapproved_policy_event_count'
    $rejections = Sum-NumericProperty -Samples $samples -Property 'frame_output_submission_rejection_count'
    if ($unapproved -gt 0 -or $rejections -gt 0) {
        return [pscustomobject]@{ Stable = $false; Reason = "invalid output policy/submission state (unapproved=$unapproved rejections=$rejections)"; WorkloadIdentityHash = $hashes[0]; Samples = $samples.Count }
    }

    return [pscustomobject]@{ Stable = $true; Reason = "stable for ${WindowSec}s"; WorkloadIdentityHash = $hashes[0]; Samples = $samples.Count }
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

    try {
        $requested = [XREngineMeasurementNativeWindow]::PostCloseToProcess($Process.Id) -or $requested
    } catch {
        # Keep the normal Process API result when native window enumeration is unavailable.
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
        'XRE_UNIT_TEST_VR_MODE',
        'XRE_VK_RENDER_TARGET_MODE',
        'XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE',
        'XRE_VULKAN_COMMAND_CHAINS',
        'XRE_VULKAN_DISABLE_PARALLEL_CHAIN_RECORDING',
        'XRE_VULKAN_DISABLE_PARALLEL_SECONDARY_RECORDING',
        'XRE_VULKAN_VALIDATION',
        'XRE_VULKAN_DIAGNOSTIC_PRESET',
        'XRE_VULKAN_COMMAND_BUFFER_LABELS',
        'XRE_OCCLUSION_CULLING_MODE',
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
    $stabilityReady = [bool]$NoStabilityGate
    $stabilityTimedOut = $false
    $stabilityWaitSec = 0
    $stabilityReason = if ($NoStabilityGate) { 'disabled by NoStabilityGate' } else { 'not evaluated' }
    $stableWorkloadIdentityHash = ''

    try {
        Set-BenchmarkEnvValue 'XRE_WORLD_MODE' 'UnitTesting' -AllowedValues @('UnitTesting')
        if ($UnitTestVrMode -eq 'Configured') {
            Clear-EnvValue 'XRE_UNIT_TEST_VR_MODE'
        } else {
            Set-BenchmarkEnvValue 'XRE_UNIT_TEST_VR_MODE' $UnitTestVrMode -AllowedValues @('Desktop', 'Emulated', 'MonadoOpenXR', 'OpenVR', 'OpenXR')
        }
        if ($VulkanRenderTargetMode -eq 'Configured') {
            Clear-EnvValue 'XRE_VK_RENDER_TARGET_MODE'
        } else {
            Set-BenchmarkEnvValue 'XRE_VK_RENDER_TARGET_MODE' $VulkanRenderTargetMode -AllowedValues @('DynamicRendering', 'LegacyRenderPass')
        }
        if ($VulkanPrimaryReuse -eq 'Configured') {
            Clear-EnvValue 'XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE'
        } else {
            Set-BenchmarkEnvValue 'XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE' $(if ($VulkanPrimaryReuse -eq 'Enabled') { '1' } else { '0' }) -Boolean
        }
        if ($VulkanCommandChains -eq 'Configured') {
            Clear-EnvValue 'XRE_VULKAN_COMMAND_CHAINS'
        } else {
            Set-BenchmarkEnvValue 'XRE_VULKAN_COMMAND_CHAINS' $(if ($VulkanCommandChains -eq 'Enabled') { '1' } else { '0' }) -Boolean
        }
        if ($VulkanParallelCommandChainRecording -eq 'Disabled') {
            Set-BenchmarkEnvValue 'XRE_VULKAN_DISABLE_PARALLEL_CHAIN_RECORDING' '1' -Boolean
        } else {
            Clear-EnvValue 'XRE_VULKAN_DISABLE_PARALLEL_CHAIN_RECORDING'
        }
        if ($VulkanParallelSecondaryRecording -eq 'Disabled') {
            Set-BenchmarkEnvValue 'XRE_VULKAN_DISABLE_PARALLEL_SECONDARY_RECORDING' '1' -Boolean
        } else {
            Clear-EnvValue 'XRE_VULKAN_DISABLE_PARALLEL_SECONDARY_RECORDING'
        }
        if ($VulkanValidation) {
            Set-BenchmarkEnvValue 'XRE_VULKAN_VALIDATION' '1' -Boolean
        } else {
            Clear-EnvValue 'XRE_VULKAN_VALIDATION'
        }
        if ($VulkanDiagnosticPreset -eq 'Configured') {
            Clear-EnvValue 'XRE_VULKAN_DIAGNOSTIC_PRESET'
        } else {
            Set-BenchmarkEnvValue 'XRE_VULKAN_DIAGNOSTIC_PRESET' $VulkanDiagnosticPreset -AllowedValues @('Off', 'StandardValidation', 'SyncValidation', 'GpuAssisted', 'BestPractices', 'CrashDiagnostics', 'RenderDocFriendly')
        }
        if ($VulkanCommandBufferLabels) {
            Set-BenchmarkEnvValue 'XRE_VULKAN_COMMAND_BUFFER_LABELS' '1' -Boolean
        } else {
            Clear-EnvValue 'XRE_VULKAN_COMMAND_BUFFER_LABELS'
        }
        if ($OcclusionCullingMode -eq 'Configured') {
            Clear-EnvValue 'XRE_OCCLUSION_CULLING_MODE'
        } else {
            Set-BenchmarkEnvValue 'XRE_OCCLUSION_CULLING_MODE' $OcclusionCullingMode -AllowedValues @('Disabled', 'CpuQueryAsync', 'CpuSoftwareOcclusion', 'GpuHiZ')
        }
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

        if (-not $exitedEarly -and -not $hangDetected -and -not $NoStabilityGate) {
            Write-Host "[measure] $runName waiting for a ${StabilityWindowSec}s stable workload window (timeout ${StabilityTimeoutSec}s)..."
            for ($second = 0; $second -lt $StabilityTimeoutSec; $second++) {
                Start-Sleep -Seconds 1
                $stabilityWaitSec = $second + 1
                if ($proc.HasExited) {
                    $exitedEarly = $true
                    $exitAt = $second
                    $exitPhase = 'stability'
                    $exitCode = $proc.ExitCode
                    break
                }

                $logDir = Get-RunLogDir -EditorProcessId $proc.Id
                if (Test-RenderStatsHung -LogDir $logDir -LastStatsState ([ref]$lastStatsState) -LastStatsProgressUtc ([ref]$lastStatsProgressUtc) -NoSampleHangSec $NoSampleHangSec) {
                    $hangDetected = $true
                    $hangPhase = 'stability'
                    $hangAt = $second
                    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                    $proc.WaitForExit(5000) | Out-Null
                    $forcedStop = $true
                    break
                }

                $stability = Test-RenderStatsStability -LogDir $logDir -WindowSec $StabilityWindowSec -Strategy $strategy
                $stabilityReason = $stability.Reason
                $stableWorkloadIdentityHash = $stability.WorkloadIdentityHash
                if ($stability.Stable) {
                    $stabilityReady = $true
                    Write-Host "[measure] $runName stability gate passed after ${stabilityWaitSec}s identity=$stableWorkloadIdentityHash"
                    break
                }
            }

            if (-not $stabilityReady -and -not $exitedEarly -and -not $hangDetected) {
                $stabilityTimedOut = $true
                Write-Host "[measure] $runName stability gate timed out: $stabilityReason" -ForegroundColor Yellow
            }
        }

        if (-not $exitedEarly -and -not $hangDetected -and $stabilityReady) {
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
    $collectWaitForRender = Get-NumericStats -Samples $samples -Property 'collect_wait_for_render_ms' -PositiveOnly
    $renderWaitForCollect = Get-NumericStats -Samples $samples -Property 'render_wait_for_collect_ms' -PositiveOnly
    $gpu = Get-NumericStats -Samples $samples -Property 'gpu_pipeline_frame_ms' -PositiveOnly
    $vulkanGpuCommandBuffer = Get-NumericStats -Samples $samples -Property 'vulkan_frame_gpu_command_buffer_ms' -PositiveOnly
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
    $vkFrame = Get-NumericStats -Samples $samples -Property 'vulkan_frame_total_ms' -PositiveOnly
    $vkWaitFrameSlot = Get-NumericStats -Samples $samples -Property 'vulkan_frame_wait_fence_ms' -PositiveOnly
    $vkSampleTimingQueries = Get-NumericStats -Samples $samples -Property 'vulkan_frame_sample_timing_queries_ms' -PositiveOnly
    $vkDrainRetiredResources = Get-NumericStats -Samples $samples -Property 'vulkan_frame_drain_retired_resources_ms' -PositiveOnly
    $vkAcquireNextImage = Get-NumericStats -Samples $samples -Property 'vulkan_frame_acquire_image_ms' -PositiveOnly
    $vkAcquireBridgeSubmit = Get-NumericStats -Samples $samples -Property 'vulkan_frame_acquire_bridge_submit_ms' -PositiveOnly
    $vkWaitSwapchainImage = Get-NumericStats -Samples $samples -Property 'vulkan_frame_wait_swapchain_image_ms' -PositiveOnly
    $vkResetDynamicUniformRing = Get-NumericStats -Samples $samples -Property 'vulkan_frame_reset_dynamic_uniform_ring_ms' -PositiveOnly
    $vkRecordCommandBuffer = Get-NumericStats -Samples $samples -Property 'vulkan_frame_record_command_buffer_ms' -PositiveOnly
    $vkRecordCommandBufferAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_record_command_buffer_allocated_bytes'
    $vkFrameOpPreparationAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_cpu_frame_op_preparation_allocated_bytes'
    $vkResourcePlanningAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_cpu_resource_planning_allocated_bytes'
    $vkFrameDataRefreshAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_cpu_frame_data_refresh_allocated_bytes'
    $vkPacketConstructionAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_cpu_packet_construction_allocated_bytes'
    $vkPrimaryRecordingAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_cpu_primary_recording_allocated_bytes'
    $vkSecondaryRecordingAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_cpu_secondary_recording_allocated_bytes'
    $vkDescriptorPublicationAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_cpu_descriptor_publication_allocated_bytes'
    $vkSubmissionAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_cpu_submission_allocated_bytes'
    $vkSubmit = Get-NumericStats -Samples $samples -Property 'vulkan_frame_submit_ms' -PositiveOnly
    $vkTrimStaging = Get-NumericStats -Samples $samples -Property 'vulkan_frame_trim_ms' -PositiveOnly
    $vkQueuePresent = Get-NumericStats -Samples $samples -Property 'vulkan_frame_present_ms' -PositiveOnly
    $vkFrameOps = Get-NumericStats -Samples $samples -Property 'vulkan_frame_op_total_count'
    $vkCommandChainsScheduled = Get-NumericStats -Samples $samples -Property 'vulkan_command_chains_scheduled'
    $vkCommandChainsRecorded = Get-NumericStats -Samples $samples -Property 'vulkan_command_chains_recorded'
    $vkCommandChainsReused = Get-NumericStats -Samples $samples -Property 'vulkan_command_chains_reused'
    $vkIndirectParallelSecondaryRecordOpsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_indirect_parallel_secondary_record_ops'
    $vkCommandChainWorkerRecord = Get-NumericStats -Samples $samples -Property 'vulkan_command_chain_worker_record_ms' -PositiveOnly
    $vkRenderThreadWaitForChainWorkers = Get-NumericStats -Samples $samples -Property 'vulkan_render_thread_wait_for_chain_workers_ms' -PositiveOnly
    $vkResourcePlanReplacementsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_resource_plan_replacements'
    $vkResourcePlanImagesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_resource_plan_images'
    $vkResourcePlanBuffersTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_resource_plan_buffers'
    $vkRetiredDescriptorPoolsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_descriptor_pool_count'
    $vkRetiredDescriptorSetsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_descriptor_set_count'
    $vkRetiredCommandBuffersTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_command_buffer_count'
    $vkRetiredQueryPoolsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_query_pool_count'
    $vkRetiredBufferViewsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_buffer_view_count'
    $vkRetiredPipelinesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_pipeline_count'
    $vkRetiredFramebuffersTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_framebuffer_count'
    $vkRetiredBuffersTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_buffer_count'
    $vkRetiredBufferMemoriesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_buffer_memory_count'
    $vkRetiredImagesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_image_count'
    $vkRetiredImageViewsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_image_view_count'
    $vkRetiredSamplersTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_sampler_count'
    $vkRetiredImageMemoriesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_image_memory_count'
    $vkRetiredResourceCountTotal = $vkResourcePlanReplacementsTotal + $vkRetiredDescriptorPoolsTotal + $vkRetiredDescriptorSetsTotal + $vkRetiredCommandBuffersTotal + $vkRetiredQueryPoolsTotal + $vkRetiredBufferViewsTotal + $vkRetiredPipelinesTotal + $vkRetiredFramebuffersTotal + $vkRetiredBuffersTotal + $vkRetiredBufferMemoriesTotal + $vkRetiredImagesTotal + $vkRetiredImageViewsTotal + $vkRetiredSamplersTotal + $vkRetiredImageMemoriesTotal
    $vkCommandBufferRecordsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_command_buffer_record_count'
    $vkCommandBufferCleanReuseTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_command_buffer_clean_reuse_count'
    $vkCommandBufferForcedDirtyTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_command_buffer_forced_dirty_count'
    $vkExactVariantsDirtiedTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_exact_variants_dirtied'
    $vkExactCommandChainsDirtiedTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_exact_command_chains_dirtied'
    $vkUnrelatedVariantsPreservedTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_unrelated_variants_preserved'
    $vkGlobalFallbackInvalidationsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_global_fallback_invalidations'
    $vkTrackingDependencyBindsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_tracking_dependency_binds'
    $vkTrackingUniqueDependenciesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_tracking_unique_dependencies'
    $vkTrackingImageAccessWritesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_tracking_image_access_writes'
    $vkTrackingCompactImageRangesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_tracking_compact_image_ranges'
    $vkDescriptorExpansionCacheHitsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_descriptor_expansion_cache_hits'
    $vkDescriptorExpansionCacheMissesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_descriptor_expansion_cache_misses'
    $vkDescriptorPoolCreatesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_descriptor_pool_create_count'
    $vkLifetimeLiveResourcesMax = Max-NumericProperty -Samples $samples -Property 'vulkan_lifetime_live_resource_count'
    $vkTrackedDescriptorSetsMax = Max-NumericProperty -Samples $samples -Property 'vulkan_tracked_descriptor_set_count'
    $vkLifetimeLockContentionsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_lifetime_lock_contentions'
    $vkLayoutLockContentionsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_layout_lock_contentions'
    $vkCommandBufferDirtySummaries = @($samples | ForEach-Object {
        $value = Get-SamplePropertyValue -Sample $_ -Property 'vulkan_command_buffer_dirty_summary'
        if (-not [string]::IsNullOrWhiteSpace([string]$value)) { [string]$value }
    } | Select-Object -Unique)
    $vkCommandBufferOutcomeTotal = $vkCommandBufferRecordsTotal + $vkCommandBufferCleanReuseTotal
    $vkCommandBufferCleanReuseRatio = if ($vkCommandBufferOutcomeTotal -gt 0) { [Math]::Round($vkCommandBufferCleanReuseTotal / $vkCommandBufferOutcomeTotal, 6) } else { 0.0 }
    $plannerPruneTotal = Sum-NumericProperty -Samples $samples -Property 'frame_output_planner_prune_count'
    $globalInFlightWaitTotal = Sum-NumericProperty -Samples $samples -Property 'frame_output_global_in_flight_wait_count'
    $forceFlushTotal = Sum-NumericProperty -Samples $samples -Property 'frame_output_force_flush_count'
    $submissionRejectionTotal = Sum-NumericProperty -Samples $samples -Property 'frame_output_submission_rejection_count'
    $unapprovedPolicyEventTotal = Sum-NumericProperty -Samples $samples -Property 'frame_output_unapproved_policy_event_count'
    $workloadIdentityHashes = @($samples | ForEach-Object {
        $value = Get-SamplePropertyValue -Sample $_ -Property 'frame_output_workload_identity_hash'
        if ($null -ne $value -and [string]$value -ne '0') { [string]$value }
    } | Select-Object -Unique)
    [double]$collectGenerationAgeMax = 0
    foreach ($sample in $samples) {
        [double]$requestedGeneration = Get-SamplePropertyValue -Sample $sample -Property 'collect_generation_requested'
        [double]$consumedGeneration = Get-SamplePropertyValue -Sample $sample -Property 'collect_generation_consumed'
        $collectGenerationAgeMax = [Math]::Max($collectGenerationAgeMax, [Math]::Max(0, $requestedGeneration - $consumedGeneration))
    }
    $validationLayersEnabledSamples = @($samples | Where-Object { $_.validation_layers_enabled -eq $true }).Count
    $vulkanValidationVuidCount = 0
    $vulkanValidationUniqueVuids = @()
    $vulkanLogPath = if ($logDir) { Join-Path $logDir 'log_vulkan.log' } else { '' }
    if ($vulkanLogPath -and (Test-Path -LiteralPath $vulkanLogPath)) {
        $vuidMatches = @(Select-String -LiteralPath $vulkanLogPath -Pattern 'VUID-[A-Za-z0-9_-]+' -AllMatches -ErrorAction SilentlyContinue)
        $vulkanValidationVuidCount = @($vuidMatches | ForEach-Object { $_.Matches } | ForEach-Object { $_.Value }).Count
        $vulkanValidationUniqueVuids = @($vuidMatches | ForEach-Object { $_.Matches } | ForEach-Object { $_.Value } | Sort-Object -Unique)
    }
    $gpuDumpCount = if ($logDir -and (Test-Path -LiteralPath $logDir)) {
        @(Get-ChildItem -LiteralPath $logDir -Filter 'profiler-gpu-pipeline-*.log' -File -ErrorAction SilentlyContinue).Count
    } else {
        0
    }

    $noteParts = New-Object System.Collections.Generic.List[string]
    if ($forcedStop) { $noteParts.Add('forced stop; GPU timing dump may be missing') | Out-Null }
    if ($hangDetected) { $noteParts.Add("no render-stats progress for ${NoSampleHangSec}s during $hangPhase at +${hangAt}s") | Out-Null }
    if ($exitedEarly) { $noteParts.Add("exited early during $exitPhase at +${exitAt}s exit=0x$([Convert]::ToString($exitCode, 16))") | Out-Null }
    if ($stabilityTimedOut) { $noteParts.Add("stability gate timeout after ${stabilityWaitSec}s: $stabilityReason") | Out-Null }
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
    if ($FailOnSteadyStateResourceChurn -and ($vkRetiredResourceCountTotal -gt 0 -or $plannerPruneTotal -gt 0 -or $globalInFlightWaitTotal -gt 0 -or $forceFlushTotal -gt 0 -or $vkDescriptorPoolCreatesTotal -gt 0 -or $vkLifetimeLiveResourcesMax -gt $MaxSteadyStateVulkanLiveResources -or $vkTrackedDescriptorSetsMax -gt $MaxSteadyStateVulkanDescriptorSets)) {
        $noteParts.Add("steady-state resource churn failure retired=$vkRetiredResourceCountTotal planReplacements=$vkResourcePlanReplacementsTotal plannerPrunes=$plannerPruneTotal globalWaits=$globalInFlightWaitTotal forceFlushes=$forceFlushTotal descriptorPoolCreates=$vkDescriptorPoolCreatesTotal liveResourcesMax=$vkLifetimeLiveResourcesMax/$MaxSteadyStateVulkanLiveResources descriptorSetsMax=$vkTrackedDescriptorSetsMax/$MaxSteadyStateVulkanDescriptorSets") | Out-Null
    }
    if ($FailOnSteadyStateCommandBufferChurn -and ($vkCommandBufferForcedDirtyTotal -gt 0 -or $vkCommandBufferDirtySummaries.Count -gt 0 -or $vkCommandBufferCleanReuseRatio -lt $MinSteadyStateCommandBufferCleanReuseRatio -or $vkGlobalFallbackInvalidationsTotal -gt 0)) {
        $noteParts.Add("steady-state command-buffer churn failure records=$vkCommandBufferRecordsTotal reuse=$vkCommandBufferCleanReuseTotal forcedDirty=$vkCommandBufferForcedDirtyTotal ratio=$vkCommandBufferCleanReuseRatio exactVariants=$vkExactVariantsDirtiedTotal exactChains=$vkExactCommandChainsDirtiedTotal unrelatedPreserved=$vkUnrelatedVariantsPreservedTotal globalFallbacks=$vkGlobalFallbackInvalidationsTotal dirty=$($vkCommandBufferDirtySummaries -join '|')") | Out-Null
    }
    if ($FailOnSteadyStateCommandBufferAllocations -and $vkRecordCommandBufferAllocatedBytesTotal -gt $MaxSteadyStateRecordCommandBufferAllocatedBytes) {
        $noteParts.Add("steady-state command-buffer allocation failure bytes=$vkRecordCommandBufferAllocatedBytesTotal threshold=$MaxSteadyStateRecordCommandBufferAllocatedBytes") | Out-Null
    }
    if ($workloadIdentityHashes.Count -ne 1) {
        $noteParts.Add("capture workload identity changed or missing: identities=$($workloadIdentityHashes -join ',')") | Out-Null
    }
    if ($unapprovedPolicyEventTotal -gt 0) {
        $noteParts.Add("unapproved output policy events=$unapprovedPolicyEventTotal") | Out-Null
    }
    if ($submissionRejectionTotal -gt 0) {
        $noteParts.Add("rejected submissions=$submissionRejectionTotal") | Out-Null
    }
    if ($VulkanDiagnosticPreset -in @('StandardValidation', 'SyncValidation', 'GpuAssisted', 'BestPractices') -and $validationLayersEnabledSamples -eq 0) {
        $noteParts.Add("requested Vulkan diagnostic preset $VulkanDiagnosticPreset but no retained sample reported validation layers enabled") | Out-Null
    }
    if ($vulkanValidationVuidCount -gt 0) {
        $noteParts.Add("Vulkan validation VUIDs=$vulkanValidationVuidCount unique=$($vulkanValidationUniqueVuids -join ',')") | Out-Null
    }

    return [pscustomobject]@{
        Strategy = $Strategy
        Repetition = $Repetition
        Configuration = $Configuration
        CacheMode = $CacheMode
        UnitTestVrMode = $UnitTestVrMode
        VulkanRenderTargetMode = $VulkanRenderTargetMode
        VulkanPrimaryReuse = $VulkanPrimaryReuse
        VulkanCommandChains = $VulkanCommandChains
        VulkanParallelCommandChainRecording = $VulkanParallelCommandChainRecording
        VulkanParallelSecondaryRecording = $VulkanParallelSecondaryRecording
        VulkanValidation = [bool]$VulkanValidation
        VulkanDiagnosticPreset = $VulkanDiagnosticPreset
        VulkanCommandBufferLabels = [bool]$VulkanCommandBufferLabels
        OcclusionCullingMode = $OcclusionCullingMode
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
        StabilityGateEnabled = -not [bool]$NoStabilityGate
        StabilityReady = $stabilityReady
        StabilityWaitSec = $stabilityWaitSec
        StabilityReason = $stabilityReason
        StableWorkloadIdentityHash = $stableWorkloadIdentityHash
        CaptureWorkloadIdentityHash = if ($workloadIdentityHashes.Count -eq 1) { $workloadIdentityHashes[0] } else { $workloadIdentityHashes -join ',' }
        CaptureWorkloadIdentityCount = $workloadIdentityHashes.Count
        StreamingPhase = 'included-in-startup-and-warmup-until asset counters stabilize'
        Samples = $samples.Count
        AllSamples = $allSamples.Count
        CaptureStartUtc = $captureStartUtc.ToString('O')
        CaptureEndUtc = $captureEndUtc.ToString('O')
        RenderAvgMs = $render.Avg
        RenderP50Ms = $render.P50
        RenderP95Ms = $render.P95
        RenderP99Ms = $render.P99
        RenderWorstMs = $render.Max
        UpdateP50Ms = $update.P50
        UpdateP95Ms = $update.P95
        UpdateP99Ms = $update.P99
        UpdateWorstMs = $update.Max
        CollectVisibleP50Ms = $collect.P50
        CollectVisibleP95Ms = $collect.P95
        CollectVisibleP99Ms = $collect.P99
        CollectVisibleWorstMs = $collect.Max
        CollectWaitForRenderP50Ms = $collectWaitForRender.P50
        CollectWaitForRenderP95Ms = $collectWaitForRender.P95
        CollectWaitForRenderWorstMs = $collectWaitForRender.Max
        RenderWaitForCollectP50Ms = $renderWaitForCollect.P50
        RenderWaitForCollectP95Ms = $renderWaitForCollect.P95
        RenderWaitForCollectWorstMs = $renderWaitForCollect.Max
        CollectGenerationAgeMaxFrames = $collectGenerationAgeMax
        StaleCollectReuseFramesTotal = Sum-NumericProperty -Samples $samples -Property 'stale_collect_reuse_frames'
        GpuSamples = $gpu.Count
        GpuReadySamples = $gpuReadyCount
        GpuP50Ms = $gpu.P50
        GpuP95Ms = $gpu.P95
        GpuP99Ms = $gpu.P99
        GpuWorstMs = $gpu.Max
        VulkanGpuCommandBufferP50Ms = $vulkanGpuCommandBuffer.P50
        VulkanGpuCommandBufferP95Ms = $vulkanGpuCommandBuffer.P95
        VulkanGpuCommandBufferP99Ms = $vulkanGpuCommandBuffer.P99
        VulkanGpuCommandBufferWorstMs = $vulkanGpuCommandBuffer.Max
        RenderMinusGpuP95Ms = $gap.P95
        VulkanFrameP50Ms = $vkFrame.P50
        VulkanFrameP95Ms = $vkFrame.P95
        VulkanFrameMaxMs = $vkFrame.Max
        VulkanWaitFrameSlotP50Ms = $vkWaitFrameSlot.P50
        VulkanWaitFrameSlotP95Ms = $vkWaitFrameSlot.P95
        VulkanWaitFrameSlotMaxMs = $vkWaitFrameSlot.Max
        VulkanSampleTimingQueriesP50Ms = $vkSampleTimingQueries.P50
        VulkanSampleTimingQueriesP95Ms = $vkSampleTimingQueries.P95
        VulkanSampleTimingQueriesMaxMs = $vkSampleTimingQueries.Max
        VulkanDrainRetiredResourcesP50Ms = $vkDrainRetiredResources.P50
        VulkanDrainRetiredResourcesP95Ms = $vkDrainRetiredResources.P95
        VulkanDrainRetiredResourcesMaxMs = $vkDrainRetiredResources.Max
        VulkanAcquireNextImageP50Ms = $vkAcquireNextImage.P50
        VulkanAcquireNextImageP95Ms = $vkAcquireNextImage.P95
        VulkanAcquireNextImageMaxMs = $vkAcquireNextImage.Max
        VulkanAcquireBridgeSubmitP50Ms = $vkAcquireBridgeSubmit.P50
        VulkanAcquireBridgeSubmitP95Ms = $vkAcquireBridgeSubmit.P95
        VulkanAcquireBridgeSubmitMaxMs = $vkAcquireBridgeSubmit.Max
        VulkanWaitSwapchainImageP50Ms = $vkWaitSwapchainImage.P50
        VulkanWaitSwapchainImageP95Ms = $vkWaitSwapchainImage.P95
        VulkanWaitSwapchainImageMaxMs = $vkWaitSwapchainImage.Max
        VulkanResetDynamicUniformRingP50Ms = $vkResetDynamicUniformRing.P50
        VulkanResetDynamicUniformRingP95Ms = $vkResetDynamicUniformRing.P95
        VulkanResetDynamicUniformRingMaxMs = $vkResetDynamicUniformRing.Max
        VulkanRecordCommandBufferP50Ms = $vkRecordCommandBuffer.P50
        VulkanRecordCommandBufferP95Ms = $vkRecordCommandBuffer.P95
        VulkanRecordCommandBufferMaxMs = $vkRecordCommandBuffer.Max
        VulkanRecordCommandBufferAllocatedBytesTotal = $vkRecordCommandBufferAllocatedBytesTotal
        VulkanFrameOpPreparationAllocatedBytesTotal = $vkFrameOpPreparationAllocatedBytesTotal
        VulkanResourcePlanningAllocatedBytesTotal = $vkResourcePlanningAllocatedBytesTotal
        VulkanFrameDataRefreshAllocatedBytesTotal = $vkFrameDataRefreshAllocatedBytesTotal
        VulkanPacketConstructionAllocatedBytesTotal = $vkPacketConstructionAllocatedBytesTotal
        VulkanPrimaryRecordingAllocatedBytesTotal = $vkPrimaryRecordingAllocatedBytesTotal
        VulkanSecondaryRecordingAllocatedBytesTotal = $vkSecondaryRecordingAllocatedBytesTotal
        VulkanDescriptorPublicationAllocatedBytesTotal = $vkDescriptorPublicationAllocatedBytesTotal
        VulkanSubmissionAllocatedBytesTotal = $vkSubmissionAllocatedBytesTotal
        VulkanSubmitP50Ms = $vkSubmit.P50
        VulkanSubmitP95Ms = $vkSubmit.P95
        VulkanSubmitMaxMs = $vkSubmit.Max
        VulkanTrimStagingP50Ms = $vkTrimStaging.P50
        VulkanTrimStagingP95Ms = $vkTrimStaging.P95
        VulkanTrimStagingMaxMs = $vkTrimStaging.Max
        VulkanQueuePresentP50Ms = $vkQueuePresent.P50
        VulkanQueuePresentP95Ms = $vkQueuePresent.P95
        VulkanQueuePresentMaxMs = $vkQueuePresent.Max
        VulkanFrameOpsP50 = $vkFrameOps.P50
        VulkanFrameOpsMax = $vkFrameOps.Max
        VulkanCommandBufferRecordsTotal = $vkCommandBufferRecordsTotal
        VulkanCommandBufferCleanReuseTotal = $vkCommandBufferCleanReuseTotal
        VulkanCommandBufferForcedDirtyTotal = $vkCommandBufferForcedDirtyTotal
        VulkanCommandBufferCleanReuseRatio = $vkCommandBufferCleanReuseRatio
        VulkanCommandBufferDirtySummaries = $vkCommandBufferDirtySummaries
        VulkanExactVariantsDirtiedTotal = $vkExactVariantsDirtiedTotal
        VulkanExactCommandChainsDirtiedTotal = $vkExactCommandChainsDirtiedTotal
        VulkanUnrelatedVariantsPreservedTotal = $vkUnrelatedVariantsPreservedTotal
        VulkanGlobalFallbackInvalidationsTotal = $vkGlobalFallbackInvalidationsTotal
        VulkanTrackingDependencyBindsTotal = $vkTrackingDependencyBindsTotal
        VulkanTrackingUniqueDependenciesTotal = $vkTrackingUniqueDependenciesTotal
        VulkanTrackingImageAccessWritesTotal = $vkTrackingImageAccessWritesTotal
        VulkanTrackingCompactImageRangesTotal = $vkTrackingCompactImageRangesTotal
        VulkanDescriptorExpansionCacheHitsTotal = $vkDescriptorExpansionCacheHitsTotal
        VulkanDescriptorExpansionCacheMissesTotal = $vkDescriptorExpansionCacheMissesTotal
        VulkanDescriptorPoolCreatesTotal = $vkDescriptorPoolCreatesTotal
        VulkanLifetimeLiveResourcesMax = $vkLifetimeLiveResourcesMax
        VulkanTrackedDescriptorSetsMax = $vkTrackedDescriptorSetsMax
        VulkanLifetimeLockContentionsTotal = $vkLifetimeLockContentionsTotal
        VulkanLayoutLockContentionsTotal = $vkLayoutLockContentionsTotal
        VulkanCommandChainsScheduledP50 = $vkCommandChainsScheduled.P50
        VulkanCommandChainsScheduledTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_command_chains_scheduled'
        VulkanCommandChainsRecordedP50 = $vkCommandChainsRecorded.P50
        VulkanCommandChainsRecordedTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_command_chains_recorded'
        VulkanCommandChainsReusedP50 = $vkCommandChainsReused.P50
        VulkanCommandChainsReusedTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_command_chains_reused'
        VulkanCommandChainsFrameDataRefreshedTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_command_chains_frame_data_refreshed'
        VulkanVolatileCommandChainsRecordedTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_volatile_command_chains_recorded'
        VulkanIndirectParallelSecondaryRecordOpsTotal = $vkIndirectParallelSecondaryRecordOpsTotal
        VulkanPrimaryCommandBuffersReusedTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_primary_command_buffers_reused'
        VulkanPrimaryCommandBuffersRecordedTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_primary_command_buffers_recorded'
        VulkanVisibilityPacketsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_visibility_packet_count'
        VulkanRenderPacketsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_render_packet_count'
        VulkanSecondaryCommandBuffersTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_secondary_command_buffer_count'
        VulkanIndirectApiCallsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_indirect_api_calls'
        VulkanIndirectSubmittedDrawsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_indirect_submitted_draws'
        VulkanRequestedDrawsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_requested_draws'
        VulkanConsumedDrawsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_consumed_draws'
        AllVulkanIndirectApiCallsTotal = Sum-NumericProperty -Samples $allSamples -Property 'vulkan_indirect_api_calls'
        AllVulkanIndirectSubmittedDrawsTotal = Sum-NumericProperty -Samples $allSamples -Property 'vulkan_indirect_submitted_draws'
        AllVulkanRequestedDrawsTotal = Sum-NumericProperty -Samples $allSamples -Property 'vulkan_requested_draws'
        AllVulkanConsumedDrawsTotal = Sum-NumericProperty -Samples $allSamples -Property 'vulkan_consumed_draws'
        VulkanCommandChainWorkerRecordP50Ms = $vkCommandChainWorkerRecord.P50
        VulkanCommandChainWorkerRecordP95Ms = $vkCommandChainWorkerRecord.P95
        VulkanRenderThreadWaitForChainWorkersP50Ms = $vkRenderThreadWaitForChainWorkers.P50
        VulkanRenderThreadWaitForChainWorkersP95Ms = $vkRenderThreadWaitForChainWorkers.P95
        VulkanResourcePlanReplacementsTotal = $vkResourcePlanReplacementsTotal
        VulkanResourcePlanImagesTotal = $vkResourcePlanImagesTotal
        VulkanResourcePlanBuffersTotal = $vkResourcePlanBuffersTotal
        VulkanRetiredDescriptorPoolsTotal = $vkRetiredDescriptorPoolsTotal
        VulkanRetiredDescriptorSetsTotal = $vkRetiredDescriptorSetsTotal
        VulkanRetiredCommandBuffersTotal = $vkRetiredCommandBuffersTotal
        VulkanRetiredQueryPoolsTotal = $vkRetiredQueryPoolsTotal
        VulkanRetiredBufferViewsTotal = $vkRetiredBufferViewsTotal
        VulkanRetiredPipelinesTotal = $vkRetiredPipelinesTotal
        VulkanRetiredFramebuffersTotal = $vkRetiredFramebuffersTotal
        VulkanRetiredBuffersTotal = $vkRetiredBuffersTotal
        VulkanRetiredBufferMemoriesTotal = $vkRetiredBufferMemoriesTotal
        VulkanRetiredImagesTotal = $vkRetiredImagesTotal
        VulkanRetiredImageViewsTotal = $vkRetiredImageViewsTotal
        VulkanRetiredSamplersTotal = $vkRetiredSamplersTotal
        VulkanRetiredImageMemoriesTotal = $vkRetiredImageMemoriesTotal
        VulkanRetiredResourceCountTotal = $vkRetiredResourceCountTotal
        VulkanRetiredImageBytesTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_retired_image_bytes'
        VulkanPlannerPrunesTotal = $plannerPruneTotal
        VulkanGlobalInFlightWaitsTotal = $globalInFlightWaitTotal
        VulkanForceFlushesTotal = $forceFlushTotal
        VulkanSubmissionRejectionsTotal = $submissionRejectionTotal
        UnapprovedOutputPolicyEventsTotal = $unapprovedPolicyEventTotal
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
        ShaderVariantsWarmingTotal = Sum-NumericProperty -Samples $allSamples -Property 'shader_variants_warming'
        ShaderVariantsLoadedFromDiskCacheTotal = Sum-NumericProperty -Samples $allSamples -Property 'shader_variants_loaded_from_disk_cache'
        ShaderVariantsGeneratedThisRunTotal = Sum-NumericProperty -Samples $allSamples -Property 'shader_variants_generated_this_run'
        ValidationLayersEnabledSamples = $validationLayersEnabledSamples
        VulkanValidationVuidCount = $vulkanValidationVuidCount
        VulkanValidationUniqueVuids = $vulkanValidationUniqueVuids
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
$results | Format-Table -AutoSize Strategy, Repetition, CacheMode, Samples, AllSamples, RenderP50Ms, RenderP95Ms, RenderP99Ms, GpuP50Ms, GpuP95Ms, VulkanFrameP50Ms, VulkanFrameP95Ms, VulkanRecordCommandBufferP95Ms, VulkanRecordCommandBufferAllocatedBytesTotal, VulkanDrainRetiredResourcesP95Ms, VulkanSubmitP95Ms, VulkanQueuePresentP95Ms, VulkanCommandBufferRecordsTotal, VulkanResourcePlanReplacementsTotal, VulkanRetiredImagesTotal, DrawCallsP50, VisibleRenderersP50, SkinnedRenderersP50, TextureBindsTotal, GpuReadbackBytesTotal, AllGpuReadbackBytesTotal, GpuDrivenFullBucketScansTotal, FallbackEventsTotal, AllFallbackEventsTotal, LastRenderMs, GpuTimingDumpFiles, Note

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
    "OcclusionCullingMode: $OcclusionCullingMode"
    "VulkanCommandChains: $VulkanCommandChains"
    "VulkanParallelCommandChainRecording: $VulkanParallelCommandChainRecording"
    "VulkanParallelSecondaryRecording: $VulkanParallelSecondaryRecording"
    "VulkanDiagnosticPreset: $VulkanDiagnosticPreset"
    "VulkanCommandBufferLabels: $([bool]$VulkanCommandBufferLabels)"
    "Lights: $ProfileLights"
    "Viewport: $ProfileViewport"
    "RenderScale: $RenderScale"
    "GpuClockPolicy: $GpuClockPolicy"
    "TargetRefreshHz: $TargetRefreshHz"
    "GpuTimestampDense: $([bool]$GpuTimestampDense)"
    "WarmupSec: $WarmupSec"
    "StabilityGate: enabled=$(-not [bool]$NoStabilityGate) windowSec=$StabilityWindowSec timeoutSec=$StabilityTimeoutSec"
    "CaptureSec: $CaptureSec"
    "Phases: startup=process launch to first sample; warmup=$WarmupSec sec minimum; stability=measured quiet window; steady-state capture=$CaptureSec sec."
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

if ($FailOnSteadyStateResourceChurn) {
    $churnFailures = @($results | Where-Object {
        [double]$_.VulkanRetiredResourceCountTotal -gt 0.0 -or
        [double]$_.VulkanPlannerPrunesTotal -gt 0.0 -or
        [double]$_.VulkanGlobalInFlightWaitsTotal -gt 0.0 -or
        [double]$_.VulkanForceFlushesTotal -gt 0.0 -or
        [double]$_.VulkanDescriptorPoolCreatesTotal -gt 0.0 -or
        [double]$_.VulkanLifetimeLiveResourcesMax -gt $MaxSteadyStateVulkanLiveResources -or
        [double]$_.VulkanTrackedDescriptorSetsMax -gt $MaxSteadyStateVulkanDescriptorSets
    })

    if ($churnFailures.Count -gt 0) {
        $details = $churnFailures | ForEach-Object {
            "$($_.Strategy) r$($_.Repetition): retired=$($_.VulkanRetiredResourceCountTotal) replacements=$($_.VulkanResourcePlanReplacementsTotal) imageViews=$($_.VulkanRetiredImageViewsTotal) plannerPrunes=$($_.VulkanPlannerPrunesTotal) globalWaits=$($_.VulkanGlobalInFlightWaitsTotal) forceFlushes=$($_.VulkanForceFlushesTotal) descriptorPoolCreates=$($_.VulkanDescriptorPoolCreatesTotal) liveResourcesMax=$($_.VulkanLifetimeLiveResourcesMax)/$MaxSteadyStateVulkanLiveResources descriptorSetsMax=$($_.VulkanTrackedDescriptorSetsMax)/$MaxSteadyStateVulkanDescriptorSets"
        }
        throw "Steady-state Vulkan resource churn detected: $($details -join '; ')"
    }
}

if ($FailOnSteadyStateCommandBufferChurn) {
    $commandChurnFailures = @($results | Where-Object {
        [double]$_.VulkanCommandBufferForcedDirtyTotal -gt 0.0 -or
        @($_.VulkanCommandBufferDirtySummaries).Count -gt 0 -or
        [double]$_.VulkanCommandBufferCleanReuseRatio -lt $MinSteadyStateCommandBufferCleanReuseRatio -or
        [double]$_.VulkanGlobalFallbackInvalidationsTotal -gt 0.0
    })

    if ($commandChurnFailures.Count -gt 0) {
        $details = $commandChurnFailures | ForEach-Object {
            "$($_.Strategy) r$($_.Repetition): records=$($_.VulkanCommandBufferRecordsTotal) reuse=$($_.VulkanCommandBufferCleanReuseTotal) forcedDirty=$($_.VulkanCommandBufferForcedDirtyTotal) reuseRatio=$($_.VulkanCommandBufferCleanReuseRatio) exactVariants=$($_.VulkanExactVariantsDirtiedTotal) exactChains=$($_.VulkanExactCommandChainsDirtiedTotal) unrelatedPreserved=$($_.VulkanUnrelatedVariantsPreservedTotal) globalFallbacks=$($_.VulkanGlobalFallbackInvalidationsTotal) dirty=$(@($_.VulkanCommandBufferDirtySummaries) -join '|')"
        }
        throw "Steady-state Vulkan command-buffer churn detected: $($details -join '; ')"
    }
}

if ($FailOnSteadyStateCommandBufferAllocations) {
    $allocationFailures = @($results | Where-Object {
        $value = $_.VulkanRecordCommandBufferAllocatedBytesTotal
        $null -ne $value -and [long]$value -gt $MaxSteadyStateRecordCommandBufferAllocatedBytes
    })

    if ($allocationFailures.Count -gt 0) {
        $details = $allocationFailures | ForEach-Object {
            "$($_.Strategy) r$($_.Repetition): recordCommandBufferAllocatedBytes=$($_.VulkanRecordCommandBufferAllocatedBytesTotal) threshold=$MaxSteadyStateRecordCommandBufferAllocatedBytes"
        }
        throw "Steady-state Vulkan command-buffer allocations exceeded threshold: $($details -join '; ')"
    }
}

$invalidCaptureFailures = @($results | Where-Object {
    -not $_.StabilityReady -or
    [int]$_.CaptureWorkloadIdentityCount -ne 1 -or
    [double]$_.UnapprovedOutputPolicyEventsTotal -gt 0.0 -or
    [double]$_.VulkanSubmissionRejectionsTotal -gt 0.0
})
if ($invalidCaptureFailures.Count -gt 0) {
    $details = $invalidCaptureFailures | ForEach-Object {
        "$($_.Strategy) r$($_.Repetition): stable=$($_.StabilityReady) identities=$($_.CaptureWorkloadIdentityCount) unapprovedPolicy=$($_.UnapprovedOutputPolicyEventsTotal) rejectedSubmissions=$($_.VulkanSubmissionRejectionsTotal) reason=$($_.StabilityReason)"
    }
    throw "Invalid Vulkan performance capture: $($details -join '; ')"
}
