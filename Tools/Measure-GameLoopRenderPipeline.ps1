# Measures the game loop and default render pipeline across mesh-submission strategies.
param(
    [int]$WarmupSec = 25,
    [int]$CaptureSec = 60,
    [int]$Repetitions = 1,
    [string[]]$Strategies = @('CpuDirect', 'GpuIndirectInstrumented', 'GpuIndirectZeroReadback', 'GpuMeshletInstrumented', 'GpuMeshletZeroReadback'),
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('Configured', 'OpenGL', 'Vulkan')]
    [string]$RenderBackend = 'Configured',
    [string]$UnitTestingWorldSettingsPath = '',
    [ValidateSet('Cold', 'Warm')]
    [string]$CacheMode = 'Cold',
    # An explicit cross-process cache root for a standalone cooked XRMesh warm load.
    # This does not claim coverage of the still-inactive broad model cache.
    [string]$MeshletStandaloneCookedCacheRoot = '',
    [ValidateSet('FullBucketScanDiagnostic', 'ActiveBucketListReadbackDiagnostic', 'MaterialTable', 'BindlessMaterialTable', 'FullBucketScan', 'ActiveBucketList')]
    [string]$ZeroReadbackMaterialDrawPath = 'BindlessMaterialTable',
    # Capture permits initialization readbacks while retaining their All* totals.
    # All keeps the strict whole-run gate for callers that require it.
    [ValidateSet('All', 'Capture')]
    [string]$ZeroReadbackValidationScope = 'All',
    [string]$ProfileScene = '',
    [string]$ProfileCamera = '',
    [double]$CameraPositionX = [double]::NaN,
    [double]$CameraPositionY = [double]::NaN,
    [double]$CameraPositionZ = [double]::NaN,
    [double]$CameraLookAtX = [double]::NaN,
    [double]$CameraLookAtY = [double]::NaN,
    [double]$CameraLookAtZ = [double]::NaN,
    [int]$MotionCaptureSec = 0,
    [double]$MotionCameraPositionX = [double]::NaN,
    [double]$MotionCameraPositionY = [double]::NaN,
    [double]$MotionCameraPositionZ = [double]::NaN,
    [double]$MotionCameraLookAtX = [double]::NaN,
    [double]$MotionCameraLookAtY = [double]::NaN,
    [double]$MotionCameraLookAtZ = [double]::NaN,
    [string]$ProfileLights = '',
    [string]$ProfileViewport = '',
    [string]$RenderScale = '',
    [int]$WindowWidth = 0,
    [int]$WindowHeight = 0,
    [ValidateRange(1, 10000)]
    [int]$SampleIntervalFrames = 10,
    [ValidateSet('Configured', 'ShippingFast', 'DevParity', 'Diagnostics')]
    [string]$VulkanGpuDrivenProfile = 'Configured',
    [string]$GpuClockPolicy = 'Unspecified',
    [double]$TargetRefreshHz = 0,
    [ValidateSet('Configured', 'Stable', 'LowLatency', 'Uncapped', 'FrameGeneration')]
    [string]$VulkanPresentationProfile = 'Configured',
    [switch]$GpuTimestampDense,
    [switch]$NoClearCachesBetweenVariants,
    [switch]$NoP3Logging,
    [switch]$FailOnSteadyStateResourceChurn,
    [int]$MaxSteadyStateVulkanLiveResources = 50000,
    [int]$MaxSteadyStateVulkanDescriptorSets = 25000,
    [switch]$FailOnSteadyStateCommandBufferChurn,
    [switch]$UseEligiblePrimaryReuseRatio,
    [switch]$FailOnSteadyStateCommandBufferAllocations,
    [switch]$FailOnSteadyStateBindingFallback,
    [double]$MinSteadyStateCommandBufferCleanReuseRatio = 0,
    [long]$MaxSteadyStateRecordCommandBufferAllocatedBytes = 0,
    [int]$StabilityWindowSec = 5,
    [int]$StabilityTimeoutSec = 120,
    [ValidateSet('FullResourceQuiet', 'OutputScheduling')]
    [string]$StabilityProfile = 'FullResourceQuiet',
    [int]$MinSteadyStateGpuSceneCommandCount = 0,
    [ValidateRange(2, 4096)]
    [int]$MinAdmissionScreenshotColorBuckets = 16,
    [switch]$NoStabilityGate,
    [switch]$AllowWorkloadIdentityChanges,
    [int]$ShutdownGraceSec = 20,
    [int]$NoSampleHangSec = 15,
    [int]$RetainedRunCount = 3,
    [string]$RunLabel = '',
    [ValidateSet('Diagnostics', 'DevelopmentProfile', 'CleanProfile', 'ReleaseBenchmark')]
    [string]$ProfileMode = 'DevelopmentProfile',
    [ValidateSet('Enabled', 'Disabled')]
    [string]$CodeProfiler = 'Enabled',
    [string]$OutputDirectory = '',
    [string]$EditorExecutablePath = '',
    [switch]$DisableMcpDiagnostics,
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
    [switch]$VulkanCommandChainBenchmarkForceRerecord,
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
$exe = if ([string]::IsNullOrWhiteSpace($EditorExecutablePath)) {
    Join-Path $repoRoot "Build\Editor\$Configuration\AnyCPU\$Configuration\net10.0-windows7.0\XREngine.Editor.exe"
} else {
    [System.IO.Path]::GetFullPath(
        $(if ([System.IO.Path]::IsPathRooted($EditorExecutablePath)) {
            $EditorExecutablePath
        } else {
            Join-Path $repoRoot $EditorExecutablePath
        }))
}
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

if ($WarmupSec -lt 0 -or $CaptureSec -le 0 -or $MotionCaptureSec -lt 0 -or $Repetitions -le 0 -or $ShutdownGraceSec -lt 1 -or $NoSampleHangSec -lt 0 -or $RetainedRunCount -lt 1 -or $StabilityWindowSec -lt 1 -or $StabilityTimeoutSec -lt 1 -or $MinSteadyStateGpuSceneCommandCount -lt 0 -or $MinSteadyStateCommandBufferCleanReuseRatio -lt 0 -or $MinSteadyStateCommandBufferCleanReuseRatio -gt 1 -or $MaxSteadyStateVulkanLiveResources -lt 1 -or $MaxSteadyStateVulkanDescriptorSets -lt 1 -or $WindowWidth -lt 0 -or $WindowHeight -lt 0) {
    throw 'WarmupSec and MotionCaptureSec must be >= 0, CaptureSec/Repetitions must be > 0, ShutdownGraceSec/StabilityWindowSec/StabilityTimeoutSec must be >= 1, NoSampleHangSec and MinSteadyStateGpuSceneCommandCount must be >= 0, RetainedRunCount must be >= 1, and MinSteadyStateCommandBufferCleanReuseRatio must be between 0 and 1.'
}
$usesMeshletStrategy = @($Strategies | Where-Object { $_ -in @('GpuMeshletInstrumented', 'GpuMeshletZeroReadback') }).Count -gt 0
if ($usesMeshletStrategy -and $CacheMode -eq 'Warm' -and [string]::IsNullOrWhiteSpace($MeshletStandaloneCookedCacheRoot)) {
    throw 'Meshlet warm measurement requires -MeshletStandaloneCookedCacheRoot from its preceding cold publish run. This is a standalone cooked-XRMesh cache, not a broad model-cache claim.'
}
if (-not [string]::IsNullOrWhiteSpace($MeshletStandaloneCookedCacheRoot)) {
    $MeshletStandaloneCookedCacheRoot = [System.IO.Path]::GetFullPath($(if ([System.IO.Path]::IsPathRooted($MeshletStandaloneCookedCacheRoot)) { $MeshletStandaloneCookedCacheRoot } else { Join-Path $repoRoot $MeshletStandaloneCookedCacheRoot }))
    $repoWithSeparator = $repoRoot.TrimEnd('\') + '\'
    if (-not $MeshletStandaloneCookedCacheRoot.StartsWith($repoWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MeshletStandaloneCookedCacheRoot must remain inside the repository: $MeshletStandaloneCookedCacheRoot"
    }
}
if (($WindowWidth -eq 0) -ne ($WindowHeight -eq 0)) {
    throw 'Specify both WindowWidth and WindowHeight as positive values, or leave both at zero.'
}
$parsedRenderScale = 0.0
if (-not [string]::IsNullOrWhiteSpace($RenderScale) -and
    (-not [double]::TryParse(
        $RenderScale,
        [System.Globalization.NumberStyles]::Float,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [ref]$parsedRenderScale) -or
     -not [double]::IsFinite($parsedRenderScale) -or
     $parsedRenderScale -lt 0.5 -or
     $parsedRenderScale -gt 1.0)) {
    throw 'RenderScale must be empty or a finite value in [0.5, 1.0].'
}

$cameraPoseValues = @(
    $CameraPositionX,
    $CameraPositionY,
    $CameraPositionZ,
    $CameraLookAtX,
    $CameraLookAtY,
    $CameraLookAtZ)
$specifiedCameraPoseValueCount = @($cameraPoseValues | Where-Object { -not [double]::IsNaN($_) }).Count
$hasFixedCameraPose = $specifiedCameraPoseValueCount -eq $cameraPoseValues.Count
if ($specifiedCameraPoseValueCount -ne 0 -and -not $hasFixedCameraPose) {
    throw 'Specify all six CameraPosition* and CameraLookAt* values together, or omit all of them.'
}
if ($hasFixedCameraPose -and $DisableMcpDiagnostics) {
    throw 'A fixed camera pose requires MCP; do not combine CameraPosition*/CameraLookAt* with DisableMcpDiagnostics.'
}
if ($hasFixedCameraPose -and
    $CameraPositionX -eq $CameraLookAtX -and
    $CameraPositionY -eq $CameraLookAtY -and
    $CameraPositionZ -eq $CameraLookAtZ) {
    throw 'The fixed camera position and look-at target must differ.'
}
$motionCameraPoseValues = @(
    $MotionCameraPositionX,
    $MotionCameraPositionY,
    $MotionCameraPositionZ,
    $MotionCameraLookAtX,
    $MotionCameraLookAtY,
    $MotionCameraLookAtZ)
$specifiedMotionCameraPoseValueCount = @($motionCameraPoseValues | Where-Object { -not [double]::IsNaN($_) }).Count
$hasCameraMotion = $MotionCaptureSec -gt 0
if ($specifiedMotionCameraPoseValueCount -ne 0 -and $specifiedMotionCameraPoseValueCount -ne $motionCameraPoseValues.Count) {
    throw 'Specify all six MotionCameraPosition* and MotionCameraLookAt* values together, or omit all of them.'
}
if ($hasCameraMotion -and $specifiedMotionCameraPoseValueCount -ne $motionCameraPoseValues.Count) {
    throw 'MotionCaptureSec requires all six MotionCameraPosition* and MotionCameraLookAt* values.'
}
if (-not $hasCameraMotion -and $specifiedMotionCameraPoseValueCount -gt 0) {
    throw 'Motion camera endpoint values require MotionCaptureSec greater than zero.'
}
if ($hasCameraMotion -and -not $hasFixedCameraPose) {
    throw 'Controlled motion requires a fixed starting pose; specify all CameraPosition* and CameraLookAt* values.'
}
if ($hasCameraMotion -and $DisableMcpDiagnostics) {
    throw 'Controlled camera motion requires MCP; do not combine MotionCaptureSec with DisableMcpDiagnostics.'
}
if ($hasCameraMotion -and
    $MotionCameraPositionX -eq $MotionCameraLookAtX -and
    $MotionCameraPositionY -eq $MotionCameraLookAtY -and
    $MotionCameraPositionZ -eq $MotionCameraLookAtZ) {
    throw 'The motion camera endpoint and look-at target must differ.'
}
$requiresMcpMutation = $hasFixedCameraPose -or $hasCameraMotion

function Get-SpeedProfileRoot {
    Join-Path (Join-Path $repoRoot 'Build\Logs') 'speed-profiles\game-loop-render-pipeline'
}

function Get-FreeMcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Invoke-ProfileMcpTool {
    param(
        [int]$Port,
        [string]$Name,
        [hashtable]$Arguments = @{},
        [int]$ReadyTimeoutSec = 10,
        [switch]$RetryUnavailableCapabilities
    )

    $endpoint = "http://localhost:$Port/mcp/"
    $statusUri = $endpoint.TrimEnd('/') + '/status'
    $deadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSec)
    do {
        try {
            $status = Invoke-RestMethod -Method Get -Uri $statusUri -TimeoutSec 2
            if ([bool]$status.isRunning) {
                break
            }
        }
        catch {
            # The MCP listener can become ready after the first rendered frame.
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    if ([DateTime]::UtcNow -ge $deadline) {
        throw "MCP did not become ready on port $Port within $ReadyTimeoutSec seconds."
    }

    do {
        $body = @{
            jsonrpc = '2.0'
            id = [guid]::NewGuid().ToString()
            method = 'tools/call'
            params = @{
                name = $Name
                arguments = $Arguments
            }
        } | ConvertTo-Json -Depth 12 -Compress

        $response = Invoke-RestMethod `
            -Uri $endpoint `
            -Method Post `
            -Body $body `
            -ContentType 'application/json' `
            -TimeoutSec 60
        if ($null -ne $response.error) {
            $errorJson = $response.error | ConvertTo-Json -Compress
            if ($RetryUnavailableCapabilities -and
                $errorJson.Contains('requires unavailable capabilities', [StringComparison]::OrdinalIgnoreCase) -and
                [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 500
                continue
            }
            throw "MCP tool '$Name' returned JSON-RPC error: $errorJson"
        }
        if ($null -ne $response.result -and [bool]$response.result.isError) {
            $resultJson = $response.result | ConvertTo-Json -Depth 6 -Compress
            throw "MCP tool '$Name' reported isError=true: $resultJson"
        }
        return $response
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "MCP tool '$Name' remained unavailable for $ReadyTimeoutSec seconds."
}

function Get-ProfileCameraPoseReadback {
    param(
        [int]$Port,
        [hashtable]$Arguments
    )

    $renderResponse = Invoke-ProfileMcpTool `
        -Port $Port `
        -Name 'get_render_state' `
        -Arguments @{} `
        -ReadyTimeoutSec 5 `
        -RetryUnavailableCapabilities
    $renderState = $renderResponse.result.structuredContent
    $position = $renderState.viewportCameraWorldPosition
    $forward = $renderState.viewportCameraWorldForward
    if ($null -eq $position -or $null -eq $forward) {
        throw 'The viewport camera pose is unavailable for verification.'
    }

    $positionError = [Math]::Sqrt(
        [Math]::Pow([double]$position.x - [double]$Arguments.position_x, 2) +
        [Math]::Pow([double]$position.y - [double]$Arguments.position_y, 2) +
        [Math]::Pow([double]$position.z - [double]$Arguments.position_z, 2))
    $lookX = [double]$Arguments.look_at_x - [double]$Arguments.position_x
    $lookY = [double]$Arguments.look_at_y - [double]$Arguments.position_y
    $lookZ = [double]$Arguments.look_at_z - [double]$Arguments.position_z
    $lookLength = [Math]::Sqrt($lookX * $lookX + $lookY * $lookY + $lookZ * $lookZ)
    $forwardDot =
        ([double]$forward.x * $lookX +
         [double]$forward.y * $lookY +
         [double]$forward.z * $lookZ) / $lookLength
    if ($positionError -gt 0.01 -or $forwardDot -lt 0.9999) {
        throw "Viewport camera readback does not match the requested pose (positionError=$positionError, forwardDot=$forwardDot)."
    }

    return [pscustomobject]@{
        Position = $position
        Forward = $forward
        PositionError = $positionError
        ForwardDot = $forwardDot
    }
}

function Set-ProfileFixedCameraWhenReady {
    param(
        [int]$Port,
        [System.Diagnostics.Process]$Process,
        [hashtable]$Arguments,
        [int]$TimeoutSec = 120
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $attempt = 0
    $lastError = 'camera operation was not attempted'
    do {
        if ($Process.HasExited) {
            throw "Editor exited before its fixed camera became ready (exit=0x$([Convert]::ToString($Process.ExitCode, 16))). Last camera error: $lastError"
        }

        $attempt++
        try {
            # A live MCP listener can precede creation of the world viewport.
            # Keep probing readiness rather than treating its first transient
            # camera failure as a failed measurement run.
            Invoke-ProfileMcpTool `
                -Port $Port `
                -Name 'set_editor_camera_view' `
                -Arguments $Arguments `
                -ReadyTimeoutSec 5 `
                -RetryUnavailableCapabilities | Out-Null
            $readback = Get-ProfileCameraPoseReadback -Port $Port -Arguments $Arguments
            Write-Host "[measure] fixed camera verified after $attempt MCP attempt(s)." -ForegroundColor DarkGray
            return $readback
        }
        catch {
            $lastError = $_.Exception.Message
            if ([DateTime]::UtcNow -ge $deadline) {
                break
            }
            Start-Sleep -Milliseconds 500
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Fixed camera did not become ready within ${TimeoutSec}s after $attempt MCP attempt(s). Last error: $lastError"
}

function Wait-ProfileCameraPose {
    param(
        [int]$Port,
        [System.Diagnostics.Process]$Process,
        [hashtable]$Arguments,
        [int]$TimeoutSec = 10
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    $lastError = 'camera endpoint verification was not attempted'
    do {
        if ($Process.HasExited) {
            throw "Editor exited before the camera reached its motion endpoint. Last camera error: $lastError"
        }

        try {
            return Get-ProfileCameraPoseReadback -Port $Port -Arguments $Arguments
        }
        catch {
            $lastError = $_.Exception.Message
            if ([DateTime]::UtcNow -ge $deadline) {
                break
            }
            Start-Sleep -Milliseconds 100
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Camera did not reach its motion endpoint within ${TimeoutSec}s. Last error: $lastError"
}

function Test-AdmissionScreenshotContent {
    param(
        [string]$Path,
        [int]$MinimumColorBuckets
    )

    # MCP screenshots can contain PNG metadata that GDI+ rejects, even though
    # Windows Imaging Component and image viewers decode the pixels correctly.
    Add-Type -AssemblyName PresentationCore
    $bitmap = [System.Windows.Media.Imaging.BitmapImage]::new([uri]::new($Path))
    $converted = [System.Windows.Media.Imaging.FormatConvertedBitmap]::new(
        $bitmap, [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $stride = $converted.PixelWidth * 4
    $pixels = [byte[]]::new($stride * $converted.PixelHeight)
    $converted.CopyPixels($pixels, $stride, 0)

    $colorBuckets = [System.Collections.Generic.HashSet[int]]::new()
    $minimumLuminance = 255
    $maximumLuminance = 0
    $stepX = [Math]::Max(1, [int]($converted.PixelWidth / 64))
    $stepY = [Math]::Max(1, [int]($converted.PixelHeight / 36))
    for ($y = 0; $y -lt $converted.PixelHeight; $y += $stepY) {
        for ($x = 0; $x -lt $converted.PixelWidth; $x += $stepX) {
            $offset = $y * $stride + $x * 4
            $blue = [int]$pixels[$offset]
            $green = [int]$pixels[$offset + 1]
            $red = [int]$pixels[$offset + 2]
            $bucket = (($red -shr 4) -shl 8) -bor
                (($green -shr 4) -shl 4) -bor
                ($blue -shr 4)
            $null = $colorBuckets.Add($bucket)
            $luminance = [int](0.2126 * $red + 0.7152 * $green + 0.0722 * $blue)
            $minimumLuminance = [Math]::Min($minimumLuminance, $luminance)
            $maximumLuminance = [Math]::Max($maximumLuminance, $luminance)
        }
    }

    if ($colorBuckets.Count -lt $MinimumColorBuckets) {
        throw "Viewport screenshot is visually empty (colorBuckets=$($colorBuckets.Count)/$MinimumColorBuckets, luminanceRange=$($maximumLuminance - $minimumLuminance))."
    }

    return [pscustomobject]@{
        ColorBuckets = $colorBuckets.Count
        LuminanceRange = $maximumLuminance - $minimumLuminance
    }
}

function New-ProfilePublicationReadinessState {
    return [pscustomobject]@{
        Identity = ''
        StableSinceUtc = [datetime]::MinValue
    }
}

function Test-ProfilePublicationReadiness {
    param(
        [int]$Port,
        [int]$WindowSec,
        [object]$State
    )

    try {
        $renderResponse = Invoke-ProfileMcpTool `
            -Port $Port `
            -Name 'get_render_state' `
            -Arguments @{} `
            -ReadyTimeoutSec 5
    }
    catch {
        $State.Identity = ''
        $State.StableSinceUtc = [datetime]::MinValue
        return [pscustomobject]@{
            Ready = $false
            Reason = "MCP readiness query failed: $($_.Exception.Message)"
            ContentGeneration = 0
            ResourceGeneration = ''
        }
    }

    $renderState = $renderResponse.result.structuredContent
    $framePackage = $renderState.canonicalFramePackage
    $scenePublication = $framePackage.scenePublication
    $preparation = $renderState.advancedPreparation

    $notReady = New-Object System.Collections.Generic.List[string]
    if ($null -eq $framePackage -or [string]$framePackage.state -ne 'Published') {
        $notReady.Add("canonical frame package state=$($framePackage.state)") | Out-Null
    }
    if ($null -eq $scenePublication -or
        [uint64]$scenePublication.databaseEpoch -eq 0 -or
        [uint64]$scenePublication.sequence -eq 0) {
        $notReady.Add('canonical scene publication is invalid') | Out-Null
    }
    $activeResourceGeneration = [string]$renderState.activeViewportResourceGeneration
    if ([string]::IsNullOrWhiteSpace($activeResourceGeneration)) {
        $notReady.Add('active render resource generation is unavailable') | Out-Null
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$renderState.pendingViewportResourceGeneration)) {
        $notReady.Add("render resource generation is pending: $($renderState.pendingViewportResourceGeneration)") | Out-Null
    }
    if ($null -eq $preparation -or -not [bool]$preparation.publication.gpuResourcesPublished) {
        $notReady.Add('Advanced GPU resources are not published') | Out-Null
    }
    elseif ([uint64]$preparation.publication.scenePublication.contentGeneration -ne
            [uint64]$scenePublication.contentGeneration) {
        $notReady.Add('Advanced preparation does not match the canonical frame package') | Out-Null
    }
    if ($null -ne $preparation -and
        -not [string]::IsNullOrWhiteSpace([string]$preparation.deferralReason) -and
        [string]$preparation.deferralReason -ne 'Ready') {
        $notReady.Add("Advanced preparation deferred: $($preparation.deferralReason)") | Out-Null
    }
    if ($notReady.Count -gt 0) {
        $State.Identity = ''
        $State.StableSinceUtc = [datetime]::MinValue
        return [pscustomobject]@{
            Ready = $false
            Reason = $notReady -join '; '
            ContentGeneration = if ($null -eq $scenePublication) { 0 } else { [uint64]$scenePublication.contentGeneration }
            ResourceGeneration = $activeResourceGeneration
        }
    }

    $identity = "$($scenePublication.databaseEpoch):$($scenePublication.sequence):$($scenePublication.frameGeneration):$($scenePublication.topologyGeneration):$($scenePublication.contentGeneration):$($scenePublication.lookupGeneration):$activeResourceGeneration"
    $now = [datetime]::UtcNow
    if ($State.Identity -ne $identity) {
        $State.Identity = $identity
        $State.StableSinceUtc = $now
    }
    $stableSeconds = ($now - $State.StableSinceUtc).TotalSeconds
    return [pscustomobject]@{
        Ready = $stableSeconds -ge $WindowSec
        Reason = "publication stable for $([Math]::Round($stableSeconds, 1))/${WindowSec}s"
        ContentGeneration = [uint64]$scenePublication.contentGeneration
        ResourceGeneration = $activeResourceGeneration
    }
}

function New-SpeedProfileRunDirectory {
    param([string]$Stamp)

    if (-not [string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $outputPath = [System.IO.Path]::GetFullPath(
            $(if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
                $OutputDirectory
            } else {
                Join-Path $repoRoot $OutputDirectory
            }))
        $repoWithSeparator = $repoRoot.TrimEnd('\') + '\'
        if (-not $outputPath.StartsWith($repoWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "OutputDirectory must remain inside the repository: $outputPath"
        }
        New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
        return $outputPath
    }

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

    $logsRoots = [System.Collections.Generic.List[string]]::new()
    $editorSessionRoot = [Environment]::GetEnvironmentVariable('XRE_EDITOR_SESSION_ROOT', 'Process')
    if (-not [string]::IsNullOrWhiteSpace($editorSessionRoot)) {
        $logsRoots.Add((Join-Path ([System.IO.Path]::GetFullPath($editorSessionRoot)) 'logs'))
    }
    $logsRoots.Add((Join-Path $repoRoot 'Build\Logs'))

    $existingLogsRoots = @($logsRoots | Where-Object { Test-Path -LiteralPath $_ })
    if ($existingLogsRoots.Count -eq 0) {
        return $null
    }

    $match = $existingLogsRoots |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -Directory -ErrorAction SilentlyContinue } |
        Where-Object { $_.Name -match "pid$EditorProcessId$" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($match) {
        return $match.FullName
    }

    if ($AllowFallback) {
        $latest = $existingLogsRoots |
            ForEach-Object { Get-ChildItem -LiteralPath $_ -Recurse -Directory -ErrorAction SilentlyContinue } |
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

function Read-McpRenderStatsPayload {
    param([string]$LogDir)

    if ([string]::IsNullOrWhiteSpace($LogDir)) {
        return $null
    }

    $path = Join-Path $LogDir 'profiler-mcp-render-stats.json'
    if (-not (Test-Path -LiteralPath $path)) {
        return $null
    }

    try {
        $response = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        foreach ($content in @($response.result.content)) {
            if ($content.type -ne 'text' -or [string]::IsNullOrWhiteSpace([string]$content.text)) {
                continue
            }

            $text = ([string]$content.text).Trim()
            if ($text.StartsWith('{')) {
                return $text | ConvertFrom-Json
            }
        }
    }
    catch {
        Write-Warning "Unable to parse MCP render-profiler payload '$path': $($_.Exception.Message)"
    }

    return $null
}

function Get-RenderStatsSampleTimestampUtc {
    param([object]$Sample)

    # Recent PowerShell versions deserialize ISO timestamps as DateTime values.
    # Casting them back to string drops fractional seconds and shifts window edges.
    $value = $Sample.ts_utc
    if ($value -is [datetimeoffset]) { return $value.UtcDateTime }
    if ($value -is [datetime]) { return $value.ToUniversalTime() }
    return [datetimeoffset]::Parse([string]$value, [System.Globalization.CultureInfo]::InvariantCulture).UtcDateTime
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
            $utc = Get-RenderStatsSampleTimestampUtc -Sample $sample
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
        if ($null -eq $sample) {
            continue
        }

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

function Get-CoarseGpuTimingSummary {
    param([System.Collections.IEnumerable]$Samples)

    $sampleArray = @($Samples)
    $activeBackends = @($sampleArray | ForEach-Object {
        $value = Get-SamplePropertyValue -Sample $_ -Property 'active_render_backend'
        if (-not [string]::IsNullOrWhiteSpace([string]$value)) { [string]$value }
    } | Select-Object -Unique)
    if ($activeBackends.Count -ne 1 -or $activeBackends[0] -ine 'Vulkan') {
        $stats = Get-NumericStats -Samples $sampleArray -Property 'gpu_pipeline_frame_ms' -PositiveOnly
        $readyCount = @($sampleArray | Where-Object { $_.gpu_pipeline_timings_ready -eq $true }).Count
        return [pscustomobject]@{
            Source = 'GpuPipelineProfiler'
            Stats = $stats
            ReadyCount = $readyCount
            CoverageDenominator = $sampleArray.Count
            CoveragePercent = if ($sampleArray.Count -gt 0) { [Math]::Round(100.0 * $readyCount / $sampleArray.Count, 3) } else { 0.0 }
            DuplicateSourceCount = 0
            OutOfWindowSourceCount = 0
            MaxAgeFrames = $null
        }
    }

    $capturedFrameIds = [System.Collections.Generic.HashSet[System.UInt64]]::new()
    foreach ($sample in $sampleArray) {
        $frameId = Get-SamplePropertyValue -Sample $sample -Property 'vulkan_frame_render_frame_number'
        if ($null -ne $frameId) {
            try { $capturedFrameIds.Add([System.UInt64]$frameId) | Out-Null } catch { }
        }
    }

    $timingsBySourceFrame = @{}
    $duplicateSourceCount = 0
    $outOfWindowSourceCount = 0
    [System.UInt64]$maxAgeFrames = 0
    foreach ($sample in $sampleArray) {
        if ((Get-SamplePropertyValue -Sample $sample -Property 'vulkan_gpu_timing_completed') -ne $true) {
            continue
        }

        try {
            [System.UInt64]$sourceFrameId = Get-SamplePropertyValue -Sample $sample -Property 'vulkan_gpu_timing_source_frame_id'
            [System.UInt64]$sequence = Get-SamplePropertyValue -Sample $sample -Property 'vulkan_gpu_timing_sequence'
            [System.UInt64]$elapsedNanoseconds = Get-SamplePropertyValue -Sample $sample -Property 'vulkan_gpu_timing_elapsed_nanoseconds'
            [System.UInt64]$ageFrames = Get-SamplePropertyValue -Sample $sample -Property 'vulkan_gpu_timing_age_frames'
        } catch {
            continue
        }
        if ($sourceFrameId -eq 0 -or $sequence -eq 0 -or $elapsedNanoseconds -eq 0) {
            continue
        }
        if (-not $capturedFrameIds.Contains($sourceFrameId)) {
            $outOfWindowSourceCount++
            continue
        }

        $key = [string]$sourceFrameId
        if ($timingsBySourceFrame.ContainsKey($key)) {
            $duplicateSourceCount++
            continue
        }

        $timingsBySourceFrame[$key] = [pscustomobject]@{
            Value = [double]$elapsedNanoseconds / 1000000.0
            Sequence = $sequence
        }
        $maxAgeFrames = [Math]::Max($maxAgeFrames, $ageFrames)
    }

    $timingSamples = @($timingsBySourceFrame.Values)
    $stats = Get-NumericStats -Samples $timingSamples -Property 'Value' -PositiveOnly
    $denominator = $capturedFrameIds.Count
    return [pscustomobject]@{
        Source = 'VulkanCommandBufferTimestampQuery'
        Stats = $stats
        ReadyCount = $timingSamples.Count
        CoverageDenominator = $denominator
        CoveragePercent = if ($denominator -gt 0) { [Math]::Round(100.0 * $timingSamples.Count / $denominator, 3) } else { 0.0 }
        DuplicateSourceCount = $duplicateSourceCount
        OutOfWindowSourceCount = $outOfWindowSourceCount
        MaxAgeFrames = $maxAgeFrames
    }
}

function Sum-NumericProperty {
    param([System.Collections.IEnumerable]$Samples, [string]$Property)

    [double]$sum = 0
    foreach ($sample in $Samples) {
        if ($null -eq $sample) {
            continue
        }

        $prop = $sample.PSObject.Properties[$Property]
        if ($prop -and $null -ne $prop.Value) {
            try { $sum += [double]$prop.Value } catch { }
        }
    }
    return [Math]::Round($sum, 3)
}

function Get-MonotonicCounterDelta {
    param([object[]]$Samples, [string]$Property)

    if ($Samples.Count -lt 2) {
        return 0
    }

    try {
        [double]$first = Get-SamplePropertyValue -Sample $Samples[0] -Property $Property
        [double]$last = Get-SamplePropertyValue -Sample $Samples[$Samples.Count - 1] -Property $Property
        return [Math]::Round([Math]::Max(0.0, $last - $first), 3)
    }
    catch {
        return 0
    }
}

function Get-EndpointComparison {
    param(
        [object[]]$Samples,
        [string]$Property,
        [double]$AbsoluteTolerance = 0.0,
        [double]$RelativeTolerance = 0.0,
        [switch]$GrowthOnly
    )

    if ($Samples.Count -eq 0) {
        return [pscustomobject]@{ Available = $false; Start = $null; End = $null; Delta = $null; AllowedDelta = $null; Passed = $false }
    }

    $startValue = Get-SamplePropertyValue -Sample $Samples[0] -Property $Property
    $endValue = Get-SamplePropertyValue -Sample $Samples[$Samples.Count - 1] -Property $Property
    if ($null -eq $startValue -or $null -eq $endValue) {
        return [pscustomobject]@{ Available = $false; Start = $null; End = $null; Delta = $null; AllowedDelta = $null; Passed = $false }
    }

    try {
        [double]$start = $startValue
        [double]$end = $endValue
    } catch {
        return [pscustomobject]@{ Available = $false; Start = $null; End = $null; Delta = $null; AllowedDelta = $null; Passed = $false }
    }

    $delta = $end - $start
    $allowedDelta = [Math]::Max($AbsoluteTolerance, [Math]::Abs($start) * $RelativeTolerance)
    $comparisonDelta = if ($GrowthOnly) { $delta } else { [Math]::Abs($delta) }
    return [pscustomobject]@{
        Available = $true
        Start = $start
        End = $end
        Delta = $delta
        AllowedDelta = $allowedDelta
        Passed = $comparisonDelta -le $allowedDelta
    }
}

function Get-ValueEndpointComparison {
    param(
        [object]$StartValue,
        [object]$EndValue,
        [double]$AbsoluteTolerance = 0.0,
        [double]$RelativeTolerance = 0.0,
        [switch]$GrowthOnly
    )

    if ($null -eq $StartValue -or $null -eq $EndValue) {
        return [pscustomobject]@{ Available = $false; Start = $null; End = $null; Delta = $null; AllowedDelta = $null; Passed = $false }
    }

    try {
        [double]$start = $StartValue
        [double]$end = $EndValue
    } catch {
        return [pscustomobject]@{ Available = $false; Start = $null; End = $null; Delta = $null; AllowedDelta = $null; Passed = $false }
    }

    $delta = $end - $start
    $allowedDelta = [Math]::Max($AbsoluteTolerance, [Math]::Abs($start) * $RelativeTolerance)
    $comparisonDelta = if ($GrowthOnly) { $delta } else { [Math]::Abs($delta) }
    return [pscustomobject]@{
        Available = $true
        Start = $start
        End = $end
        Delta = $delta
        AllowedDelta = $allowedDelta
        Passed = $comparisonDelta -le $allowedDelta
    }
}

function Get-BacklogEndpointComparison {
    param([object[]]$Samples, [string]$Property)

    $comparison = Get-EndpointComparison -Samples $Samples -Property $Property -GrowthOnly
    if (-not $comparison.Available) {
        return $comparison
    }

    $comparison.Passed = $comparison.End -le $comparison.Start
    return $comparison
}

function Get-ProcessPrivateMemoryBytes {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process -or $Process.HasExited) {
        return $null
    }

    try {
        $Process.Refresh()
        return [long]$Process.PrivateMemorySize64
    } catch {
        return $null
    }
}

function New-RenderStatsStabilityState {
    return [pscustomobject]@{
        Path = ''
        Offset = [long]0
        PendingText = ''
        Samples = [System.Collections.Generic.List[object]]::new()
    }
}

function Initialize-RenderStatsStabilityState {
    param(
        [string]$LogDir,
        [object]$State
    )

    $State.Samples.Clear()
    $State.PendingText = ''
    $State.Path = if ([string]::IsNullOrWhiteSpace($LogDir)) {
        ''
    } else {
        Join-Path $LogDir 'profiler-render-stats.ndjson'
    }
    $item = if ([string]::IsNullOrWhiteSpace($State.Path)) {
        $null
    } else {
        Get-Item -LiteralPath $State.Path -ErrorAction SilentlyContinue
    }
    $State.Offset = if ($null -eq $item) { [long]0 } else { [long]$item.Length }
}

function Update-RenderStatsStabilityState {
    param(
        [string]$LogDir,
        [int]$WindowSec,
        [object]$State
    )

    if ([string]::IsNullOrWhiteSpace($LogDir)) {
        return
    }

    $path = Join-Path $LogDir 'profiler-render-stats.ndjson'
    $item = Get-Item -LiteralPath $path -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        return
    }

    if ($State.Path -ne $path -or [long]$item.Length -lt [long]$State.Offset) {
        $State.Path = $path
        $State.Offset = [long]$item.Length
        $State.PendingText = ''
        $State.Samples.Clear()
        return
    }
    if ([long]$item.Length -eq [long]$State.Offset) {
        return
    }

    $stream = [System.IO.FileStream]::new(
        $path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite)
    try {
        $null = $stream.Seek([long]$State.Offset, [System.IO.SeekOrigin]::Begin)
        $reader = [System.IO.StreamReader]::new(
            $stream,
            [System.Text.UTF8Encoding]::new($false, $false),
            $false,
            65536,
            $true)
        try {
            $newText = $reader.ReadToEnd()
            $State.Offset = [long]$stream.Position
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    $segments = [System.Text.RegularExpressions.Regex]::Split(
        "$($State.PendingText)$newText",
        "\r?\n")
    if ($segments.Count -eq 0) {
        return
    }

    $State.PendingText = $segments[$segments.Count - 1]
    for ($index = 0; $index -lt $segments.Count - 1; $index++) {
        $trimmed = $segments[$index].Trim()
        if (-not $trimmed.StartsWith('{')) {
            continue
        }

        try {
            $sample = $trimmed | ConvertFrom-Json -ErrorAction Stop
            $timestampUtc = Get-RenderStatsSampleTimestampUtc -Sample $sample
            $State.Samples.Add(
                [pscustomobject]@{
                    Sample = $sample
                    Utc = $timestampUtc
                }) | Out-Null
        }
        catch {
            continue
        }
    }

    if ($State.Samples.Count -eq 0) {
        return
    }

    $latestUtc = $State.Samples[$State.Samples.Count - 1].Utc
    # Validation and cold-pipeline frames can space profiler samples several
    # seconds apart. Retain enough history to keep the sample immediately before
    # the requested boundary instead of continually discarding the only anchor.
    $retainAfterUtc = $latestUtc.AddSeconds(-($WindowSec + 60))
    while ($State.Samples.Count -gt 0 -and $State.Samples[0].Utc -lt $retainAfterUtc) {
        $State.Samples.RemoveAt(0)
    }
}

function Test-RenderStatsStability {
    param(
        [string]$LogDir,
        [int]$WindowSec,
        [string]$Strategy,
        [string]$Profile,
        [int]$MinimumGpuSceneCommandCount,
        [object]$State
    )

    Update-RenderStatsStabilityState -LogDir $LogDir -WindowSec $WindowSec -State $State
    $timestamped = @($State.Samples)
    if ($timestamped.Count -lt 2) {
        return [pscustomobject]@{ Stable = $false; Reason = 'waiting for profiler samples'; WorkloadIdentityHash = ''; Samples = 0 }
    }

    $latestUtc = $timestamped[$timestamped.Count - 1].Utc
    $requestedStartUtc = $latestUtc.AddSeconds(-$WindowSec)
    $anchor = @($timestamped | Where-Object { $_.Utc -le $requestedStartUtc } | Select-Object -Last 1)
    $windowStartUtc = if ($anchor.Count -gt 0) { $anchor[0].Utc } else { $timestamped[0].Utc }
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
    if ($MinimumGpuSceneCommandCount -gt 0) {
        $sceneCommandCounts = @($samples | ForEach-Object {
            $value = Get-SamplePropertyValue -Sample $_ -Property 'gpu_scene_command_count'
            if ($null -ne $value) { [long]$value }
        })
        if ($sceneCommandCounts.Count -ne $samples.Count) {
            return [pscustomobject]@{ Stable = $false; Reason = "GPU-scene command topology telemetry incomplete"; WorkloadIdentityHash = $hashes[0]; Samples = $samples.Count }
        }

        $minimumObserved = ($sceneCommandCounts | Measure-Object -Minimum).Minimum
        if ($minimumObserved -lt $MinimumGpuSceneCommandCount) {
            return [pscustomobject]@{ Stable = $false; Reason = "GPU-scene command topology not ready ($minimumObserved/$MinimumGpuSceneCommandCount)"; WorkloadIdentityHash = $hashes[0]; Samples = $samples.Count }
        }

        $uniqueSceneCommandCounts = @($sceneCommandCounts | Select-Object -Unique)
        if ($uniqueSceneCommandCounts.Count -ne 1) {
            return [pscustomobject]@{ Stable = $false; Reason = "GPU-scene command topology changed ($($uniqueSceneCommandCounts -join ','))"; WorkloadIdentityHash = $hashes[0]; Samples = $samples.Count }
        }
    }

    if ($Strategy -in @('GpuIndirectZeroReadback', 'GpuMeshletZeroReadback')) {
        for ($sampleIndex = 0; $sampleIndex -lt $samples.Count; $sampleIndex++) {
            $sample = $samples[$sampleIndex]
            $requiredRows = Get-SamplePropertyValue -Sample $sample -Property 'gpu_driven_required_material_rows'
            $readyRows = Get-SamplePropertyValue -Sample $sample -Property 'gpu_driven_ready_material_rows'
            $nonReadyReferences = Get-SamplePropertyValue -Sample $sample -Property 'gpu_driven_non_ready_material_texture_references'
            $invalidMaterialIds = Get-SamplePropertyValue -Sample $sample -Property 'gpu_driven_invalid_material_ids'
            $fallbackSubmittedRows = Get-SamplePropertyValue -Sample $sample -Property 'gpu_driven_fallback_submitted_material_rows'
            $materialTableGeneration = Get-SamplePropertyValue -Sample $sample -Property 'gpu_driven_material_table_publication_generation'
            $descriptorGeneration = Get-SamplePropertyValue -Sample $sample -Property 'gpu_driven_material_descriptor_publication_generation'

            if ($null -eq $requiredRows -or $null -eq $readyRows -or
                $null -eq $nonReadyReferences -or $null -eq $invalidMaterialIds -or
                $null -eq $fallbackSubmittedRows -or $null -eq $materialTableGeneration -or
                $null -eq $descriptorGeneration) {
                return [pscustomobject]@{ Stable = $false; Reason = 'material readiness telemetry incomplete'; WorkloadIdentityHash = $hashes[0]; Samples = $samples.Count }
            }

            if ([long]$requiredRows -le 0 -or [long]$readyRows -ne [long]$requiredRows -or
                [long]$nonReadyReferences -ne 0 -or [long]$invalidMaterialIds -ne 0 -or
                [long]$fallbackSubmittedRows -ne 0 -or [long]$materialTableGeneration -le 0) {
                return [pscustomobject]@{
                    Stable = $false
                    Reason = "material table not ready (ready=$readyRows/$requiredRows pendingRefs=$nonReadyReferences invalidIds=$invalidMaterialIds fallbackRows=$fallbackSubmittedRows tableGen=$materialTableGeneration)"
                    WorkloadIdentityHash = $hashes[0]
                    Samples = $samples.Count
                }
            }

            # A material table containing only untextured rows legitimately reports
            # descriptor generation zero. Complete ready rows, zero pending texture
            # references and zero fallback rows above establish material readiness.
        }
    }
    $quietProperties = @(
        'texture_upload_jobs',
        'texture_upload_bytes',
        'shader_variants_requested',
        'shader_variants_warming',
        'shader_variants_linked',
        'vulkan_required_pipeline_pending_count',
        'vulkan_retired_resource_plan_replacements',
        'vulkan_retired_resource_plan_images',
        'vulkan_retired_resource_plan_buffers',
        'frame_output_planner_prune_count',
        'frame_output_global_in_flight_wait_count',
        'frame_output_force_flush_count'
    )
    if ($Profile -eq 'FullResourceQuiet') {
        $quietProperties += @(
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
            'vulkan_retired_image_memory_count'
        )
    }
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
    $failedFrames = @($samples | Where-Object {
        $_.active_render_backend -eq 'Vulkan' -and $_.vulkan_frame_outcome -in @('Rejected', 'Failed')
    }).Count
    if ($unapproved -gt 0 -or $rejections -gt 0 -or $failedFrames -gt 0) {
        return [pscustomobject]@{ Stable = $false; Reason = "invalid output policy/submission state (unapproved=$unapproved rejections=$rejections failedFrames=$failedFrames)"; WorkloadIdentityHash = $hashes[0]; Samples = $samples.Count }
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

function Get-ProfileIntervalSummary {
    param(
        [System.Collections.IEnumerable]$Samples,
        [datetime]$StartUtc,
        [datetime]$EndUtc
    )

    $intervalSamples = @($Samples)
    $identityHashes = @($intervalSamples | ForEach-Object {
        $value = Get-SamplePropertyValue -Sample $_ -Property 'frame_output_workload_identity_hash'
        if ($null -ne $value -and [string]$value -ne '0') { [string]$value }
    } | Select-Object -Unique)

    $gpuTiming = Get-CoarseGpuTimingSummary -Samples $intervalSamples

    return [pscustomobject]@{
        StartUtc = $StartUtc.ToString('O')
        EndUtc = $EndUtc.ToString('O')
        DurationSec = [Math]::Round(($EndUtc - $StartUtc).TotalSeconds, 3)
        Samples = $intervalSamples.Count
        WorkloadIdentityHash = if ($identityHashes.Count -eq 1) { $identityHashes[0] } else { $identityHashes -join ',' }
        WorkloadIdentityCount = $identityHashes.Count
        Render = Get-NumericStats -Samples $intervalSamples -Property 'render_dispatch_ms' -PositiveOnly
        RenderOutsideVulkan = Get-NumericStats -Samples $intervalSamples -Property 'render_outside_vulkan_frame_ms'
        Update = Get-NumericStats -Samples $intervalSamples -Property 'update_ms' -PositiveOnly
        CollectVisible = Get-NumericStats -Samples $intervalSamples -Property 'collect_visible_ms' -PositiveOnly
        CollectWaitForRender = Get-NumericStats -Samples $intervalSamples -Property 'collect_wait_for_render_ms' -PositiveOnly
        RenderWaitForCollect = Get-NumericStats -Samples $intervalSamples -Property 'render_wait_for_collect_ms' -PositiveOnly
        Gpu = $gpuTiming.Stats
        GpuTimingSource = $gpuTiming.Source
        GpuReadySamples = $gpuTiming.ReadyCount
        GpuCoverageDenominator = $gpuTiming.CoverageDenominator
        GpuCoveragePercent = $gpuTiming.CoveragePercent
        GpuDuplicateSourceCount = $gpuTiming.DuplicateSourceCount
        GpuOutOfWindowSourceCount = $gpuTiming.OutOfWindowSourceCount
        GpuMaxAgeFrames = $gpuTiming.MaxAgeFrames
        VulkanFrame = Get-NumericStats -Samples $intervalSamples -Property 'vulkan_frame_total_ms' -PositiveOnly
        VulkanWaitFrameSlot = Get-NumericStats -Samples $intervalSamples -Property 'vulkan_frame_wait_fence_ms' -PositiveOnly
        VulkanWaitSwapchainImage = Get-NumericStats -Samples $intervalSamples -Property 'vulkan_frame_wait_swapchain_image_ms' -PositiveOnly
        VulkanRecordCommandBuffer = Get-NumericStats -Samples $intervalSamples -Property 'vulkan_frame_record_command_buffer_ms' -PositiveOnly
        VulkanPreparedMeshHoleMaterialization = Get-NumericStats -Samples $intervalSamples -Property 'vulkan_cpu_prepared_mesh_hole_materialization_ms'
        VulkanResourcePlanning = Get-NumericStats -Samples $intervalSamples -Property 'vulkan_cpu_resource_planning_ms'
        VulkanFrameDataRefresh = Get-NumericStats -Samples $intervalSamples -Property 'vulkan_cpu_frame_data_refresh_ms'
        VulkanPrimaryCommandEncoding = Get-NumericStats -Samples $intervalSamples -Property 'vulkan_cpu_primary_command_encoding_ms'
        VulkanSubmit = Get-NumericStats -Samples $intervalSamples -Property 'vulkan_frame_submit_ms' -PositiveOnly
        VulkanQueuePresent = Get-NumericStats -Samples $intervalSamples -Property 'vulkan_frame_present_ms' -PositiveOnly
        VulkanDescriptorPublicationAllocatedBytesTotal = Sum-NumericProperty -Samples $intervalSamples -Property 'vulkan_cpu_descriptor_publication_allocated_bytes'
        VulkanSubmissionAllocatedBytesTotal = Sum-NumericProperty -Samples $intervalSamples -Property 'vulkan_cpu_submission_allocated_bytes'
        VulkanSubmissionRejectionsTotal = Sum-NumericProperty -Samples $intervalSamples -Property 'frame_output_submission_rejection_count'
        UnapprovedOutputPolicyEventsTotal = Sum-NumericProperty -Samples $intervalSamples -Property 'frame_output_unapproved_policy_event_count'
        VulkanFailedFrameSamples = @($intervalSamples | Where-Object {
            $_.active_render_backend -eq 'Vulkan' -and $_.vulkan_frame_outcome -in @('Rejected', 'Failed')
        }).Count
    }
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

    if (-not $requested) {
        try {
            $requested = [XREngineMeasurementNativeWindow]::PostCloseToProcess($Process.Id)
        } catch {
            # Keep the normal Process API result when native window enumeration is unavailable.
        }
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
        'XRE_UNIT_TEST_WORLD_SETTINGS_PATH',
        'XRE_UNIT_TEST_RENDER_API',
        'XRE_UNIT_TEST_VR_MODE',
        'XRE_VK_RENDER_TARGET_MODE',
        'XRE_VULKAN_PRIMARY_COMMAND_BUFFER_REUSE',
        'XRE_VULKAN_COMMAND_CHAINS',
        'XRE_VULKAN_DISABLE_PARALLEL_CHAIN_RECORDING',
        'XRE_VULKAN_COMMAND_CHAIN_BENCHMARK_FORCE_RERECORD',
        'XRE_VULKAN_DISABLE_PARALLEL_SECONDARY_RECORDING',
        'XRE_VULKAN_VALIDATION',
        'XRE_VULKAN_DIAGNOSTIC_PRESET',
        'XRE_VULKAN_COMMAND_BUFFER_LABELS',
        'XRE_OCCLUSION_CULLING_MODE',
        'XRE_PROFILER_ENABLED',
        'XRE_PROFILE_CAPTURE',
        'XRE_PROFILE_AUTO_DUMP',
        'XRE_PROFILE_CODE_PROFILER',
        'XRE_PROFILE_RUN_LABEL',
        'XRE_FORCE_MESH_SUBMISSION_STRATEGY',
        'XRE_ZERO_READBACK_MATERIAL_DRAW_PATH',
        'XRE_PROFILE_CACHE_MODE',
        'XRE_MESHLET_STANDALONE_COOKED_CACHE_MODE',
        'XRE_MESHLET_STANDALONE_COOKED_CACHE_ROOT',
        'XRE_SHADER_CACHE_MODE',
        'XRE_TEXTURE_CACHE_MODE',
        'XRE_PROFILE_SCENE',
        'XRE_PROFILE_CAMERA',
        'XRE_PROFILE_LIGHTS',
        'XRE_PROFILE_VIEWPORT',
        'XRE_PROFILE_RENDER_SCALE',
        'XRE_PROFILE_WINDOW_WIDTH',
        'XRE_PROFILE_WINDOW_HEIGHT',
        'XRE_PROFILE_SAMPLE_INTERVAL_FRAMES',
        'XRE_PROFILE_VULKAN_GPU_DRIVEN_PROFILE',
        'XRE_PROFILE_WARMUP_SEC',
        'XRE_PROFILE_CAPTURE_SEC',
        'XRE_PROFILE_MODE',
        'XRE_PROFILE_PHASE',
        'XRE_GPU_CLOCK_POLICY',
        'XRE_TARGET_REFRESH_HZ',
        'XRE_VULKAN_PRESENTATION_PROFILE',
        'XRE_GPU_TIMESTAMP_DENSE',
        'XRE_P3_LOGGING',
        'XRE_SKIP_IMGUI',
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
    $processStartUtc = $null
    $captureStartUtc = [datetime]::UtcNow
    $captureEndUtc = $captureStartUtc
    $motionCaptureStartUtc = $captureStartUtc
    $motionCaptureEndUtc = $captureStartUtc
    $privateMemoryStartBytes = $null
    $privateMemoryEndBytes = $null
    $retentionStartMcpStats = $null
    $retentionEndMcpStats = $null
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
    $stabilityStatsState = New-RenderStatsStabilityState
    $publicationReadinessState = New-ProfilePublicationReadinessState
    $stabilityReady = [bool]$NoStabilityGate
    $stabilityTimedOut = $false
    $stabilityWaitSec = 0
    $stabilityReason = if ($NoStabilityGate) { 'disabled by NoStabilityGate' } else { 'not evaluated' }
    $stableWorkloadIdentityHash = ''
    $stableContentGeneration = 0
    $stableResourceGeneration = ''
    $mcpPort = Get-FreeMcpPort
    $mcpDiagnosticsSucceeded = $false
    $mcpDiagnosticError = ''
    $admissionScreenshotCaptured = $false
    $admissionScreenshotPath = ''
    $admissionScreenshotColorBuckets = 0
    $admissionScreenshotLuminanceRange = 0
    $admissionScreenshotError = if ($DisableMcpDiagnostics) {
        'Disabled with MCP diagnostics.'
    } else {
        'Not attempted.'
    }
    $fixedCameraReadback = $null
    $motionCameraReadback = $null
    $postScreenshotStabilityWaitSec = 0

    try {
        Set-BenchmarkEnvValue 'XRE_WORLD_MODE' 'UnitTesting' -AllowedValues @('UnitTesting')
        if ([string]::IsNullOrWhiteSpace($UnitTestingWorldSettingsPath)) {
            Clear-EnvValue 'XRE_UNIT_TEST_WORLD_SETTINGS_PATH'
        }
        else {
            $resolvedSettingsPath = if ([System.IO.Path]::IsPathRooted($UnitTestingWorldSettingsPath)) {
                [System.IO.Path]::GetFullPath($UnitTestingWorldSettingsPath)
            }
            else {
                [System.IO.Path]::GetFullPath((Join-Path $repoRoot $UnitTestingWorldSettingsPath))
            }
            if (-not (Test-Path -LiteralPath $resolvedSettingsPath -PathType Leaf)) {
                throw "Unit Testing World settings file not found: $resolvedSettingsPath"
            }
            Set-EnvValue 'XRE_UNIT_TEST_WORLD_SETTINGS_PATH' $resolvedSettingsPath
        }
        if ($RenderBackend -eq 'Configured') {
            Clear-EnvValue 'XRE_UNIT_TEST_RENDER_API'
        }
        else {
            Set-BenchmarkEnvValue 'XRE_UNIT_TEST_RENDER_API' $RenderBackend -AllowedValues @('OpenGL', 'Vulkan')
        }
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
        if ($VulkanCommandChainBenchmarkForceRerecord) {
            Set-BenchmarkEnvValue 'XRE_VULKAN_COMMAND_CHAIN_BENCHMARK_FORCE_RERECORD' '1' -Boolean
        } else {
            Clear-EnvValue 'XRE_VULKAN_COMMAND_CHAIN_BENCHMARK_FORCE_RERECORD'
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
        Set-BenchmarkEnvValue 'XRE_PROFILE_CODE_PROFILER' $(if ($CodeProfiler -eq 'Enabled') { '1' } else { '0' }) -Boolean
        Set-BenchmarkEnvValue 'XRE_PROFILE_MODE' $ProfileMode -AllowedValues @('Diagnostics', 'DevelopmentProfile', 'CleanProfile', 'ReleaseBenchmark')
        Set-EnvValue 'XRE_PROFILE_RUN_LABEL' $runName
        Set-BenchmarkEnvValue 'XRE_FORCE_MESH_SUBMISSION_STRATEGY' $Strategy -AllowedValues $validStrategies
        Set-BenchmarkEnvValue 'XRE_ZERO_READBACK_MATERIAL_DRAW_PATH' $ZeroReadbackMaterialDrawPath -AllowedValues @('FullBucketScanDiagnostic', 'ActiveBucketListReadbackDiagnostic', 'MaterialTable', 'BindlessMaterialTable', 'FullBucketScan', 'ActiveBucketList')
        Set-BenchmarkEnvValue 'XRE_PROFILE_CACHE_MODE' $CacheMode -AllowedValues @('Cold', 'Warm')
        if (-not [string]::IsNullOrWhiteSpace($MeshletStandaloneCookedCacheRoot)) {
            Set-BenchmarkEnvValue 'XRE_MESHLET_STANDALONE_COOKED_CACHE_ROOT' $MeshletStandaloneCookedCacheRoot
            Set-BenchmarkEnvValue 'XRE_MESHLET_STANDALONE_COOKED_CACHE_MODE' $(if ($CacheMode -eq 'Warm') { 'Load' } else { 'Publish' }) -AllowedValues @('Publish', 'Load')
        } else {
            Clear-EnvValue 'XRE_MESHLET_STANDALONE_COOKED_CACHE_ROOT'
            Clear-EnvValue 'XRE_MESHLET_STANDALONE_COOKED_CACHE_MODE'
        }
        Set-BenchmarkEnvValue 'XRE_SHADER_CACHE_MODE' $CacheMode -AllowedValues @('Cold', 'Warm')
        Set-BenchmarkEnvValue 'XRE_TEXTURE_CACHE_MODE' $CacheMode -AllowedValues @('Cold', 'Warm')
        if ($WarmupSec -gt 0) {
            Set-BenchmarkEnvValue 'XRE_PROFILE_WARMUP_SEC' ([string]$WarmupSec) -PositiveNumber
        } else {
            Clear-EnvValue 'XRE_PROFILE_WARMUP_SEC'
        }
        Set-BenchmarkEnvValue 'XRE_PROFILE_CAPTURE_SEC' ([string]($CaptureSec + $MotionCaptureSec)) -PositiveNumber
        Set-EnvValue 'XRE_PROFILE_PHASE' 'startup-warmup-steady-state'
        Set-EnvValue 'XRE_GPU_CLOCK_POLICY' $GpuClockPolicy
        if ($TargetRefreshHz -gt 0) {
            Set-BenchmarkEnvValue 'XRE_TARGET_REFRESH_HZ' ($TargetRefreshHz.ToString([System.Globalization.CultureInfo]::InvariantCulture)) -PositiveNumber
        } else {
            Clear-EnvValue 'XRE_TARGET_REFRESH_HZ'
        }
        if ($VulkanPresentationProfile -eq 'Configured') {
            Clear-EnvValue 'XRE_VULKAN_PRESENTATION_PROFILE'
        } else {
            Set-BenchmarkEnvValue `
                'XRE_VULKAN_PRESENTATION_PROFILE' `
                $VulkanPresentationProfile `
                -AllowedValues @('Stable', 'LowLatency', 'Uncapped', 'FrameGeneration')
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
        if ($WindowWidth -gt 0) {
            Set-BenchmarkEnvValue 'XRE_PROFILE_WINDOW_WIDTH' ([string]$WindowWidth) -PositiveNumber
            Set-BenchmarkEnvValue 'XRE_PROFILE_WINDOW_HEIGHT' ([string]$WindowHeight) -PositiveNumber
        } else {
            Clear-EnvValue 'XRE_PROFILE_WINDOW_WIDTH'
            Clear-EnvValue 'XRE_PROFILE_WINDOW_HEIGHT'
        }
        Set-BenchmarkEnvValue 'XRE_PROFILE_SAMPLE_INTERVAL_FRAMES' ([string]$SampleIntervalFrames) -PositiveNumber
        if ($VulkanGpuDrivenProfile -eq 'Configured') {
            Clear-EnvValue 'XRE_PROFILE_VULKAN_GPU_DRIVEN_PROFILE'
        } else {
            Set-BenchmarkEnvValue 'XRE_PROFILE_VULKAN_GPU_DRIVEN_PROFILE' $VulkanGpuDrivenProfile -AllowedValues @('ShippingFast', 'DevParity', 'Diagnostics')
        }

        Set-EnvValue 'XRE_WINDOW_TITLE' "XRE Editor (Profile $Strategy r$Repetition)"

        $cleanProfile = $ProfileMode -in @('CleanProfile', 'ReleaseBenchmark')
        if ($cleanProfile) {
            if ($GpuTimestampDense -or $VulkanCommandBufferLabels -or $VulkanValidation -or
                $VulkanDiagnosticPreset -notin @('Configured', 'Off')) {
                throw "$ProfileMode does not permit dense GPU timestamps, Vulkan validation, debug labels, or diagnostic presets."
            }
            Set-BenchmarkEnvValue 'XRE_SKIP_IMGUI' '1' -Boolean
            Clear-EnvValue 'XRE_P3_LOGGING'
        } elseif ($NoP3Logging) {
            Clear-EnvValue 'XRE_P3_LOGGING'
        } else {
            Set-BenchmarkEnvValue 'XRE_P3_LOGGING' '1' -Boolean
            Clear-EnvValue 'XRE_SKIP_IMGUI'
        }

        Clear-EnvValue 'XRE_BUCKET_LOOP_DRY_RUN'
        Clear-EnvValue 'XRE_SKIP_COMMAND_SWAP_IF_CLEAN'
        Clear-EnvValue 'XRE_BUCKET_LOOP_SKIP_EMPTY'
        Clear-EnvValue 'XRE_FORCE_SINGLE_BUCKET'
        Clear-EnvValue 'XRE_HIZ_CULL_TRACE'

        Write-Host "[measure] $runName launching..." -ForegroundColor Cyan
        $editorArguments = if ($DisableMcpDiagnostics) {
            @('--no-mcp')
        } else {
            @(
                '--mcp',
                '--mcp-permission-policy',
                $(if ($requiresMcpMutation) { 'AllowMutate' } else { 'AllowReadOnly' }),
                '--mcp-port',
                [string]$mcpPort
            )
        }
        $startProcessArguments = @{
            FilePath = $exe
            WorkingDirectory = $repoRoot
            PassThru = $true
            WindowStyle = 'Hidden'
        }
        if ($editorArguments.Count -gt 0) {
            $startProcessArguments.ArgumentList = $editorArguments
        }
        $proc = Start-Process @startProcessArguments
        $processStartUtc = $proc.StartTime.ToUniversalTime()
        if ($hasFixedCameraPose) {
            Write-Host "[measure] $runName positioning fixed camera via MCP..."
            try {
                $fixedCameraReadback = Set-ProfileFixedCameraWhenReady `
                    -Port $mcpPort `
                    -Process $proc `
                    -Arguments @{
                        position_x = $CameraPositionX
                        position_y = $CameraPositionY
                        position_z = $CameraPositionZ
                        look_at_x = $CameraLookAtX
                        look_at_y = $CameraLookAtY
                        look_at_z = $CameraLookAtZ
                        duration = 0.0
                    } `
                    -TimeoutSec 120
            }
            catch {
                if (-not $proc.HasExited) {
                    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                }
                throw
            }
        }
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

        }

        $logDir = Get-RunLogDir -EditorProcessId $proc.Id

        if (-not $exitedEarly -and -not $hangDetected -and -not $NoStabilityGate) {
            Initialize-RenderStatsStabilityState -LogDir $logDir -State $stabilityStatsState
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

                $publicationReadiness = Test-ProfilePublicationReadiness `
                    -Port $mcpPort `
                    -WindowSec $StabilityWindowSec `
                    -State $publicationReadinessState
                $stability = Test-RenderStatsStability -LogDir $logDir -WindowSec $StabilityWindowSec -Strategy $strategy -Profile $StabilityProfile -MinimumGpuSceneCommandCount $MinSteadyStateGpuSceneCommandCount -State $stabilityStatsState
                $stabilityReason = if ($publicationReadiness.Ready) {
                    $stability.Reason
                } else {
                    $publicationReadiness.Reason
                }
                $stableWorkloadIdentityHash = $stability.WorkloadIdentityHash
                $stableContentGeneration = $publicationReadiness.ContentGeneration
                $stableResourceGeneration = $publicationReadiness.ResourceGeneration
                if ($publicationReadiness.Ready -and $stability.Stable) {
                    $stabilityReady = $true
                    Write-Host "[measure] $runName stability gate passed after ${stabilityWaitSec}s identity=$stableWorkloadIdentityHash contentGeneration=$stableContentGeneration"
                    break
                }
            }

            if (-not $stabilityReady -and -not $exitedEarly -and -not $hangDetected) {
                $stabilityTimedOut = $true
                Write-Host "[measure] $runName stability gate timed out: $stabilityReason" -ForegroundColor Yellow
            }
        }

        if (-not $exitedEarly -and -not $hangDetected -and
            $stabilityReady -and -not $DisableMcpDiagnostics) {
            try {
                if ($hasFixedCameraPose) {
                    $fixedCameraReadback = Set-ProfileFixedCameraWhenReady `
                        -Port $mcpPort `
                        -Process $proc `
                        -Arguments @{
                            position_x = $CameraPositionX
                            position_y = $CameraPositionY
                            position_z = $CameraPositionZ
                            look_at_x = $CameraLookAtX
                            look_at_y = $CameraLookAtY
                            look_at_z = $CameraLookAtZ
                            duration = 0.0
                        } `
                        -TimeoutSec 10
                }

                $admissionCaptureDir = Join-Path $profileRunDir "mcp-captures\$runName\admission"
                New-Item -ItemType Directory -Path $admissionCaptureDir -Force | Out-Null
                $admissionCapture = Invoke-ProfileMcpTool `
                    -Port $mcpPort `
                    -Name 'capture_viewport_screenshot' `
                    -Arguments @{ output_dir = $admissionCaptureDir } `
                    -ReadyTimeoutSec 30
                $admissionScreenshotPath = [string]$admissionCapture.result.structuredContent.path
                if ([string]::IsNullOrWhiteSpace($admissionScreenshotPath) -or
                    -not (Test-Path -LiteralPath $admissionScreenshotPath -PathType Leaf) -or
                    (Get-Item -LiteralPath $admissionScreenshotPath).Length -eq 0) {
                    throw 'MCP returned no nonempty viewport screenshot.'
                }
                $admissionScreenshotContent = Test-AdmissionScreenshotContent `
                    -Path $admissionScreenshotPath `
                    -MinimumColorBuckets $MinAdmissionScreenshotColorBuckets
                $admissionScreenshotColorBuckets = $admissionScreenshotContent.ColorBuckets
                $admissionScreenshotLuminanceRange = $admissionScreenshotContent.LuminanceRange
                $admissionScreenshotCaptured = $true
                $admissionScreenshotError = ''
                Write-Host "[measure] $runName admission image=$admissionScreenshotPath"
            }
            catch {
                $stabilityReady = $false
                $admissionScreenshotError = $_.Exception.Message
                $stabilityReason = "admission screenshot failed: $admissionScreenshotError"
                Write-Host "[measure] $runName $stabilityReason" -ForegroundColor Yellow
            }

            if ($admissionScreenshotCaptured -and -not $NoStabilityGate) {
                $stabilityReady = $false
                $stabilityStatsState = New-RenderStatsStabilityState
                $publicationReadinessState = New-ProfilePublicationReadinessState
                Initialize-RenderStatsStabilityState -LogDir $logDir -State $stabilityStatsState
                Write-Host "[measure] $runName re-establishing the ${StabilityWindowSec}s quiet window after image capture..."
                for ($second = 0; $second -lt $StabilityTimeoutSec; $second++) {
                    Start-Sleep -Seconds 1
                    $postScreenshotStabilityWaitSec = $second + 1
                    if ($proc.HasExited) {
                        $exitedEarly = $true
                        $exitAt = $second
                        $exitPhase = 'post-screenshot-stability'
                        $exitCode = $proc.ExitCode
                        break
                    }

                    $publicationReadiness = Test-ProfilePublicationReadiness `
                        -Port $mcpPort `
                        -WindowSec $StabilityWindowSec `
                        -State $publicationReadinessState
                    $stability = Test-RenderStatsStability -LogDir $logDir -WindowSec $StabilityWindowSec -Strategy $strategy -Profile $StabilityProfile -MinimumGpuSceneCommandCount $MinSteadyStateGpuSceneCommandCount -State $stabilityStatsState
                    $stabilityReason = if ($publicationReadiness.Ready) {
                        $stability.Reason
                    } else {
                        $publicationReadiness.Reason
                    }
                    $stableWorkloadIdentityHash = $stability.WorkloadIdentityHash
                    $stableContentGeneration = $publicationReadiness.ContentGeneration
                    $stableResourceGeneration = $publicationReadiness.ResourceGeneration
                    if ($publicationReadiness.Ready -and $stability.Stable) {
                        $stabilityReady = $true
                        Write-Host "[measure] $runName post-image stability gate passed after ${postScreenshotStabilityWaitSec}s identity=$stableWorkloadIdentityHash contentGeneration=$stableContentGeneration"
                        break
                    }
                }

                if (-not $stabilityReady -and -not $exitedEarly -and -not $hangDetected) {
                    $stabilityTimedOut = $true
                    Write-Host "[measure] $runName post-image stability gate timed out: $stabilityReason" -ForegroundColor Yellow
                }
            }
        }

        if (-not $exitedEarly -and -not $hangDetected -and $stabilityReady) {
            Write-Host "[measure] $runName capture ${CaptureSec}s log=$logDir"
            if (-not $DisableMcpDiagnostics) {
                try {
                    $retentionStartResponse = Invoke-ProfileMcpTool `
                        -Port $mcpPort `
                        -Name 'get_render_profiler_stats' `
                        -Arguments @{}
                    $retentionStartMcpStats = $retentionStartResponse.result.structuredContent
                } catch {
                    Write-Host "[measure] $runName start retention snapshot failed: $($_.Exception.Message)" -ForegroundColor Yellow
                }
            }
            $privateMemoryStartBytes = Get-ProcessPrivateMemoryBytes -Process $proc
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

        if (-not $exitedEarly -and -not $hangDetected -and $stabilityReady -and $hasCameraMotion) {
            $motionArguments = @{
                position_x = $MotionCameraPositionX
                position_y = $MotionCameraPositionY
                position_z = $MotionCameraPositionZ
                look_at_x = $MotionCameraLookAtX
                look_at_y = $MotionCameraLookAtY
                look_at_z = $MotionCameraLookAtZ
                duration = [double]$MotionCaptureSec
            }
            Write-Host "[measure] $runName controlled camera motion ${MotionCaptureSec}s..."
            try {
                $motionCaptureStartUtc = [datetime]::UtcNow
                Invoke-ProfileMcpTool `
                    -Port $mcpPort `
                    -Name 'set_editor_camera_view' `
                    -Arguments $motionArguments `
                    -ReadyTimeoutSec 10 `
                    -RetryUnavailableCapabilities | Out-Null

                for ($second = 0; $second -lt $MotionCaptureSec; $second++) {
                    Start-Sleep -Seconds 1
                    if ($proc.HasExited) {
                        $exitedEarly = $true
                        $exitAt = $second
                        $exitPhase = 'motion-capture'
                        $exitCode = $proc.ExitCode
                        Write-Host "[measure] $runName exited during controlled motion at +${second}s exitCode=0x$([Convert]::ToString($exitCode, 16))" -ForegroundColor Yellow
                        break
                    }

                    $logDir = Get-RunLogDir -EditorProcessId $proc.Id
                    if (Test-RenderStatsHung -LogDir $logDir -LastStatsState ([ref]$lastStatsState) -LastStatsProgressUtc ([ref]$lastStatsProgressUtc) -NoSampleHangSec $NoSampleHangSec) {
                        $hangDetected = $true
                        $hangPhase = 'motion-capture'
                        $hangAt = $second
                        Write-Host "[measure] $runName no render-stats progress for ${NoSampleHangSec}s during controlled motion; forcing process stop" -ForegroundColor Yellow
                        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                        $proc.WaitForExit(5000) | Out-Null
                        $forcedStop = $true
                        break
                    }
                }

                $motionCaptureEndUtc = [datetime]::UtcNow
                if (-not $exitedEarly -and -not $hangDetected) {
                    $motionCameraReadback = Wait-ProfileCameraPose `
                        -Port $mcpPort `
                        -Process $proc `
                        -Arguments $motionArguments `
                        -TimeoutSec 10
                    Write-Host '[measure] controlled camera endpoint verified.' -ForegroundColor DarkGray
                }
            }
            catch {
                if ($motionCaptureEndUtc -le $motionCaptureStartUtc) {
                    $motionCaptureEndUtc = [datetime]::UtcNow
                }
                if (-not $proc.HasExited) {
                    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                }
                throw
            }
        } else {
            $motionCaptureStartUtc = $captureEndUtc
            $motionCaptureEndUtc = $captureEndUtc
        }

        if (-not $exitedEarly -and -not $hangDetected -and $stabilityReady) {
            $privateMemoryEndBytes = Get-ProcessPrivateMemoryBytes -Process $proc
        }

        if (-not $exitedEarly -and -not $hangDetected) {
            if (-not $DisableMcpDiagnostics) {
                $logDir = Get-RunLogDir -EditorProcessId $proc.Id
                $mcpErrors = [System.Collections.Generic.List[string]]::new()
                $mcpRequests = [System.Collections.Generic.List[object]]::new()
                $mcpRequests.Add([pscustomobject]@{
                    Name = 'dump_cpu_frame_profile'
                    Arguments = @{}
                    ResponseFile = 'profiler-mcp-cpu-dump-response.json'
                })
                if (-not $cleanProfile) {
                    $mcpRequests.Add([pscustomobject]@{
                        Name = 'dump_gpu_render_pipeline_profile'
                        Arguments = @{ all_pipelines = $true }
                        ResponseFile = 'profiler-mcp-gpu-dump-response.json'
                    })
                }
                $mcpRequests.Add([pscustomobject]@{
                    Name = 'get_render_profiler_stats'
                    Arguments = @{}
                    ResponseFile = 'profiler-mcp-render-stats.json'
                })
                foreach ($request in $mcpRequests) {
                    try {
                        $response = Invoke-ProfileMcpTool `
                            -Port $mcpPort `
                            -Name $request.Name `
                            -Arguments $request.Arguments
                        if (-not [string]::IsNullOrWhiteSpace($logDir)) {
                            $response | ConvertTo-Json -Depth 30 | Set-Content `
                                -LiteralPath (Join-Path $logDir $request.ResponseFile) `
                                -Encoding UTF8
                        }
                        if ($request.Name -eq 'get_render_profiler_stats') {
                            $retentionEndMcpStats = $response.result.structuredContent
                        }
                    }
                    catch {
                        $mcpErrors.Add("$($request.Name): $($_.Exception.Message)") | Out-Null
                    }
                }

                $mcpDiagnosticsSucceeded = $mcpErrors.Count -eq 0
                if (-not $mcpDiagnosticsSucceeded) {
                    $mcpDiagnosticError = $mcpErrors -join '; '
                    Write-Host "[measure] $runName MCP diagnostic dump failed: $mcpDiagnosticError" -ForegroundColor Yellow
                }
            } else {
                $mcpDiagnosticError = 'Disabled for clean performance capture.'
            }

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

    $allSamples = @(Read-AllRenderStatsSamples -LogDir $logDir)
    $samples = @(Select-RenderStatsSamples -Samples $allSamples -CaptureStartUtc $captureStartUtc -CaptureEndUtc $captureEndUtc)
    $motionSamples = if ($hasCameraMotion) {
        @(Select-RenderStatsSamples -Samples $allSamples -CaptureStartUtc $motionCaptureStartUtc -CaptureEndUtc $motionCaptureEndUtc)
    } else {
        @()
    }
    $retentionSamples = if ($hasCameraMotion) { @($samples + $motionSamples) } else { @($samples) }
    $managedHeapEndpoint = Get-EndpointComparison `
        -Samples $retentionSamples `
        -Property 'managed_heap_bytes' `
        -AbsoluteTolerance (16MB) `
        -RelativeTolerance 0.05 `
        -GrowthOnly
    $privateMemoryEndpoint = if ($null -ne $privateMemoryStartBytes -and $null -ne $privateMemoryEndBytes) {
        [double]$privateStart = $privateMemoryStartBytes
        [double]$privateEnd = $privateMemoryEndBytes
        [double]$privateDelta = $privateEnd - $privateStart
        [double]$privateAllowedDelta = [Math]::Max(16MB, [Math]::Abs($privateStart) * 0.05)
        [pscustomobject]@{
            Available = $true
            Start = $privateStart
            End = $privateEnd
            Delta = $privateDelta
            AllowedDelta = $privateAllowedDelta
            Passed = $privateDelta -le $privateAllowedDelta
        }
    } else {
        [pscustomobject]@{ Available = $false; Start = $null; End = $null; Delta = $null; AllowedDelta = $null; Passed = $false }
    }
    $retentionStartMetering = if ($null -ne $retentionStartMcpStats) { $retentionStartMcpStats.vulkan.retired_resources.metering } else { $null }
    $retentionEndMetering = if ($null -ne $retentionEndMcpStats) { $retentionEndMcpStats.vulkan.retired_resources.metering } else { $null }
    $vulkanLiveResourceEndpoint = Get-ValueEndpointComparison `
        -StartValue $(if ($null -ne $retentionStartMetering) { $retentionStartMetering.liveResourceCount } else { $null }) `
        -EndValue $(if ($null -ne $retentionEndMetering) { $retentionEndMetering.liveResourceCount } else { $null }) `
        -RelativeTolerance 0.01
    $vulkanDescriptorSetEndpoint = Get-ValueEndpointComparison `
        -StartValue $(if ($null -ne $retentionStartMetering) { $retentionStartMetering.trackedDescriptorSetCount } else { $null }) `
        -EndValue $(if ($null -ne $retentionEndMetering) { $retentionEndMetering.trackedDescriptorSetCount } else { $null }) `
        -RelativeTolerance 0.01
    $requiredBacklogEndpoints = [ordered]@{
        CodeProfilerPendingCompleted = Get-BacklogEndpointComparison -Samples $retentionSamples -Property 'code_profiler_pending_completed_count'
        TextureUploadJobs = Get-BacklogEndpointComparison -Samples $retentionSamples -Property 'texture_upload_jobs'
        ShaderVariantsWarming = Get-BacklogEndpointComparison -Samples $retentionSamples -Property 'shader_variants_warming'
        VulkanRequiredPipelines = Get-BacklogEndpointComparison -Samples $retentionSamples -Property 'vulkan_required_pipeline_pending_count'
        VulkanLifetimeRetirements = Get-BacklogEndpointComparison -Samples $retentionSamples -Property 'vulkan_lifetime_pending_retirement_count'
        VulkanSwapchainRetirements = Get-BacklogEndpointComparison -Samples $retentionSamples -Property 'vulkan_swapchain_retirement_pending_count'
        VulkanMaterialAllocations = Get-BacklogEndpointComparison -Samples $retentionSamples -Property 'vulkan_material_table_pending_allocations'
    }
    $requiredBacklogEvidenceComplete = @($requiredBacklogEndpoints.Values | Where-Object { -not $_.Available }).Count -eq 0
    $requiredBacklogsReturnedToBaseline = $requiredBacklogEvidenceComplete -and @($requiredBacklogEndpoints.Values | Where-Object { -not $_.Passed }).Count -eq 0
    $profilerOverflowDiscardedEvents = Max-NumericProperty -Samples $allSamples -Property 'code_profiler_overflow_discarded_events'
    $profilerPendingCompletedDiscardedEvents = Max-NumericProperty -Samples $allSamples -Property 'code_profiler_pending_completed_discarded_events'
    $diagnosticLossEvidenceComplete = $allSamples.Count -gt 0 -and
        $null -ne (Get-SamplePropertyValue -Sample $allSamples[0] -Property 'code_profiler_overflow_discarded_events') -and
        $null -ne (Get-SamplePropertyValue -Sample $allSamples[0] -Property 'code_profiler_pending_completed_discarded_events')
    $diagnosticLossPassed = $diagnosticLossEvidenceComplete -and
        $profilerOverflowDiscardedEvents -eq 0 -and
        $profilerPendingCompletedDiscardedEvents -eq 0
    $retentionEvidenceComplete = $managedHeapEndpoint.Available -and
        $privateMemoryEndpoint.Available -and
        $vulkanLiveResourceEndpoint.Available -and
        $vulkanDescriptorSetEndpoint.Available -and
        $requiredBacklogEvidenceComplete
    $retentionPassed = $retentionEvidenceComplete -and
        $managedHeapEndpoint.Passed -and
        $privateMemoryEndpoint.Passed -and
        $vulkanLiveResourceEndpoint.Passed -and
        $vulkanDescriptorSetEndpoint.Passed -and
        $requiredBacklogsReturnedToBaseline
    $stationaryInterval = Get-ProfileIntervalSummary -Samples $samples -StartUtc $captureStartUtc -EndUtc $captureEndUtc
    $motionInterval = if ($hasCameraMotion) {
        Get-ProfileIntervalSummary -Samples $motionSamples -StartUtc $motionCaptureStartUtc -EndUtc $motionCaptureEndUtc
    } else {
        $null
    }
    $mcpRenderStats = Read-McpRenderStatsPayload -LogDir $logDir
    $mcpMeshletStats = if ($null -ne $mcpRenderStats) { $mcpRenderStats.meshlets } else { $null }
    $mcpVulkanFrameOps = if ($null -ne $mcpRenderStats) { $mcpRenderStats.vulkan.frame_ops } else { $null }
    $render = Get-NumericStats -Samples $samples -Property 'render_dispatch_ms' -PositiveOnly
    $renderOutsideVulkan = Get-NumericStats -Samples $samples -Property 'render_outside_vulkan_frame_ms'
    $update = Get-NumericStats -Samples $samples -Property 'update_ms' -PositiveOnly
    $collect = Get-NumericStats -Samples $samples -Property 'collect_visible_ms' -PositiveOnly
    $collectWaitForRender = Get-NumericStats -Samples $samples -Property 'collect_wait_for_render_ms' -PositiveOnly
    $renderWaitForCollect = Get-NumericStats -Samples $samples -Property 'render_wait_for_collect_ms' -PositiveOnly
    $gpuTiming = Get-CoarseGpuTimingSummary -Samples $samples
    $gpu = $gpuTiming.Stats
    $vulkanGpuCommandBuffer = Get-NumericStats -Samples $samples -Property 'vulkan_frame_gpu_command_buffer_ms' -PositiveOnly
    $gap = Get-NumericStats -Samples $samples -Property 'render_thread_minus_gpu_ms' -PositiveOnly
    $gpuReadyCount = $gpuTiming.ReadyCount
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
    $lastMaterialBindingRung = Get-SamplePropertyValue -Sample $lastSample -Property 'gpu_material_binding_rung'
    $lastMaterialBindingRungReason = Get-SamplePropertyValue -Sample $lastSample -Property 'gpu_material_binding_rung_reason'
    $lastGpuCompactionRung = Get-SamplePropertyValue -Sample $lastSample -Property 'gpu_compaction_rung'
    $lastGpuCompactionRungReason = Get-SamplePropertyValue -Sample $lastSample -Property 'gpu_compaction_rung_reason'
    $captureReadbackTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_readback_bytes'
    $captureMappedTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_mapped_buffers'
    $allReadbackTotal = Sum-NumericProperty -Samples $allSamples -Property 'gpu_readback_bytes'
    $allMappedTotal = Sum-NumericProperty -Samples $allSamples -Property 'gpu_mapped_buffers'
    $validatedReadbackTotal = if ($ZeroReadbackValidationScope -eq 'Capture') { $captureReadbackTotal } else { $allReadbackTotal }
    $validatedMappedTotal = if ($ZeroReadbackValidationScope -eq 'Capture') { $captureMappedTotal } else { $allMappedTotal }
    $allFallbackTotal = Sum-NumericProperty -Samples $allSamples -Property 'gpu_cpu_fallback_events'
    $allForbiddenFallbackTotal = Sum-NumericProperty -Samples $allSamples -Property 'forbidden_gpu_fallback_events'
    # Meshlet cook and render-path guards are lifetime counters, so use the
    # highest observed value rather than summing every repeated profile sample.
    $meshletColdImportBuilderCalls = Max-NumericProperty -Samples $allSamples -Property 'meshlet_cold_import_builder_calls'
    $meshletColdImportBuildMs = Max-NumericProperty -Samples $allSamples -Property 'meshlet_cold_import_build_ms'
    $meshletColdImportAllocatedBytes = Max-NumericProperty -Samples $allSamples -Property 'meshlet_cold_import_allocated_bytes'
    $meshletGeneratedLodCount = Max-NumericProperty -Samples $allSamples -Property 'meshlet_generated_lod_count'
    $meshletCookedPayloadCount = Max-NumericProperty -Samples $allSamples -Property 'meshlet_cooked_payload_count'
    $meshletCookedMeshletCount = Max-NumericProperty -Samples $allSamples -Property 'meshlet_cooked_meshlet_count'
    $meshletSourceParserCalls = Max-NumericProperty -Samples $allSamples -Property 'meshlet_source_parser_calls'
    $meshletWarmPayloadHydrations = Max-NumericProperty -Samples $allSamples -Property 'meshlet_warm_payload_hydrations'
    $meshletRenderPathSourceHashCalls = Max-NumericProperty -Samples $allSamples -Property 'meshlet_render_path_source_hash_calls'
    $meshletRenderPathDiskCalls = Max-NumericProperty -Samples $allSamples -Property 'meshlet_render_path_disk_calls'
    $meshletRenderPathCookerCalls = Max-NumericProperty -Samples $allSamples -Property 'meshlet_render_path_cooker_calls'
    $meshletDispatchCalls = Max-NumericProperty -Samples $samples -Property 'meshlet_dispatch_calls'
    $meshletDispatchGroups = Max-NumericProperty -Samples $samples -Property 'meshlet_dispatch_groups'
    $meshletMappedBytes = Max-NumericProperty -Samples $allSamples -Property 'meshlet_mapped_bytes'
    $meshletCapabilityFailedRung = Get-SamplePropertyValue -Sample $lastSample -Property 'meshlet_vulkan_capability_failed_rung'
    $meshletResolvedPass = Get-SamplePropertyValue -Sample $lastSample -Property 'meshlet_resolved_pass'
    $meshletResolvedRoute = Get-SamplePropertyValue -Sample $lastSample -Property 'meshlet_resolved_route'
    $meshletPrimaryRouteReason = Get-SamplePropertyValue -Sample $lastSample -Property 'meshlet_primary_route_reason'
    $meshletResolvedRows = Max-NumericProperty -Samples $samples -Property 'meshlet_resolved_meshlet_rows'
    $meshletResolvedTaskGroups = Max-NumericProperty -Samples $samples -Property 'meshlet_resolved_task_groups'
    $meshletTaskRecordsEmitted = Max-NumericProperty -Samples $samples -Property 'gpu_meshlet_task_records_emitted'
    $meshletTaskRecordsFrustumCulled = Max-NumericProperty -Samples $samples -Property 'gpu_meshlet_task_records_frustum_culled'
    $meshletTaskRecordsConeCulled = Max-NumericProperty -Samples $samples -Property 'gpu_meshlet_task_records_cone_culled'
    $meshletTaskRecordsHiZCulled = Max-NumericProperty -Samples $samples -Property 'gpu_meshlet_task_records_hiz_culled'
    $meshletDelayedDispatchGroups = Max-NumericProperty -Samples $samples -Property 'gpu_meshlet_delayed_dispatch_group_count'
    $meshletDiagnosticReadbackBytes = Max-NumericProperty -Samples $samples -Property 'gpu_meshlet_diagnostic_readback_bytes'
    $meshletRequestedFrames = Max-NumericProperty -Samples $samples -Property 'gpu_meshlet_requested_frames'
    $meshletProductionFrames = Max-NumericProperty -Samples $samples -Property 'gpu_meshlet_production_frames'
    $vulkanFrameOpMeshTaskDispatchCount = Max-NumericProperty -Samples $samples -Property 'vulkan_frame_op_mesh_task_dispatch_count'
    $meshletBufferBytesResident = Max-NumericProperty -Samples $samples -Property 'gpu_meshlet_buffer_bytes_resident'
    $meshletBufferLiveBytes = Max-NumericProperty -Samples $samples -Property 'meshlet_buffer_live_bytes'
    $meshletBufferRetiredBytes = Max-NumericProperty -Samples $samples -Property 'meshlet_buffer_retired_bytes'
    $meshletBufferRebuildCount = Max-NumericProperty -Samples $allSamples -Property 'meshlet_buffer_rebuild_count'
    $meshletBufferRetireCount = Max-NumericProperty -Samples $allSamples -Property 'meshlet_buffer_retire_count'
    $meshletBufferRebuildsDuringCapture = Get-MonotonicCounterDelta -Samples $samples -Property 'meshlet_buffer_rebuild_count'
    $meshletBufferRetiresDuringCapture = Get-MonotonicCounterDelta -Samples $samples -Property 'meshlet_buffer_retire_count'
    if ($null -ne $mcpMeshletStats) {
        $meshletTaskRecordsEmitted = [Math]::Max([double]$meshletTaskRecordsEmitted, [double]$mcpMeshletStats.latest_task_records_emitted)
        $meshletTaskRecordsFrustumCulled = [Math]::Max([double]$meshletTaskRecordsFrustumCulled, [double]$mcpMeshletStats.latest_task_records_frustum_culled)
        $meshletTaskRecordsConeCulled = [Math]::Max([double]$meshletTaskRecordsConeCulled, [double]$mcpMeshletStats.latest_task_records_cone_culled)
        $meshletTaskRecordsHiZCulled = [Math]::Max([double]$meshletTaskRecordsHiZCulled, [double]$mcpMeshletStats.latest_task_records_hiz_culled)
        $meshletDelayedDispatchGroups = [Math]::Max([double]$meshletDelayedDispatchGroups, [double]$mcpMeshletStats.delayed_dispatch_group_count)
        $meshletDiagnosticReadbackBytes = [Math]::Max([double]$meshletDiagnosticReadbackBytes, [double]$mcpMeshletStats.diagnostic_readback_bytes)
        $meshletBufferLiveBytes = [Math]::Max([double]$meshletBufferLiveBytes, [double]$mcpMeshletStats.buffer_live_bytes)
        $meshletBufferRetiredBytes = [Math]::Max([double]$meshletBufferRetiredBytes, [double]$mcpMeshletStats.buffer_retired_bytes)
        $meshletBufferRebuildCount = [Math]::Max([double]$meshletBufferRebuildCount, [double]$mcpMeshletStats.buffer_rebuild_count)
        $meshletBufferRetireCount = [Math]::Max([double]$meshletBufferRetireCount, [double]$mcpMeshletStats.buffer_retire_count)
    }
    if ($null -ne $mcpVulkanFrameOps) {
        $vulkanFrameOpMeshTaskDispatchCount = [Math]::Max(
            [double]$vulkanFrameOpMeshTaskDispatchCount,
            [double]$mcpVulkanFrameOps.mesh_task_dispatch_count)
    }
    $vkFrame = Get-NumericStats -Samples $samples -Property 'vulkan_frame_total_ms' -PositiveOnly
    $vkWaitFrameSlot = Get-NumericStats -Samples $samples -Property 'vulkan_frame_wait_fence_ms' -PositiveOnly
    $vkSampleTimingQueries = Get-NumericStats -Samples $samples -Property 'vulkan_frame_sample_timing_queries_ms' -PositiveOnly
    $vkDrainRetiredResources = Get-NumericStats -Samples $samples -Property 'vulkan_frame_drain_retired_resources_ms' -PositiveOnly
    $vkAcquireNextImage = Get-NumericStats -Samples $samples -Property 'vulkan_frame_acquire_image_ms' -PositiveOnly
    $vkAcquireBridgeSubmit = Get-NumericStats -Samples $samples -Property 'vulkan_frame_acquire_bridge_submit_ms' -PositiveOnly
    $vkWaitSwapchainImage = Get-NumericStats -Samples $samples -Property 'vulkan_frame_wait_swapchain_image_ms' -PositiveOnly
    $vkResetDynamicUniformRing = Get-NumericStats -Samples $samples -Property 'vulkan_frame_reset_dynamic_uniform_ring_ms' -PositiveOnly
    $vkRecordCommandBuffer = Get-NumericStats -Samples $samples -Property 'vulkan_frame_record_command_buffer_ms' -PositiveOnly
    $vkFrameOpPreparation = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_frame_op_preparation_ms'
    $vkRawMeshRequestDrain = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_raw_mesh_request_drain_ms'
    $vkFrameOpCohort = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_frame_op_cohort_ms'
    $vkPreparedMeshBindingValidation = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_prepared_mesh_binding_validation_ms'
    $vkPreparedMeshHoleMaterialization = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_prepared_mesh_hole_materialization_ms'
    $vkFrameOpResourceUseLowering = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_frame_op_resource_use_lowering_ms'
    $vkFrameOpPlan = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_frame_op_plan_ms'
    $vkWorkerWait = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_worker_wait_ms'
    $gpuDrivenIndirectConstruction = Get-NumericStats -Samples $samples -Property 'gpu_driven_indirect_command_generation_ms'
    $vkResourcePlanning = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_resource_planning_ms'
    $vkFrameDataRefresh = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_frame_data_refresh_ms'
    $vkPrimaryCommandEncoding = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_primary_command_encoding_ms'
    $vkSecondaryRecording = Get-NumericStats -Samples $samples -Property 'vulkan_cpu_secondary_recording_ms'
    $vkRawMeshRequestDrainInvocations = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_cpu_raw_mesh_request_drain_process_invocation_count'
    $vkFrameOpCohortInvocations = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_cpu_frame_op_cohort_process_invocation_count'
    $vkPreparedMeshBindingValidationInvocations = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_cpu_prepared_mesh_binding_validation_process_invocation_count'
    $vkPreparedMeshHoleMaterializationInvocations = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_cpu_prepared_mesh_hole_materialization_process_invocation_count'
    $vkFrameOpResourceUseLoweringInvocations = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_cpu_frame_op_resource_use_lowering_process_invocation_count'
    $vkFrameOpPlanInvocations = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_cpu_frame_op_plan_process_invocation_count'
    $vkPrimaryCommandEncodingInvocations = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_cpu_primary_command_encoding_process_invocation_count'
    $vkWorkerWaitInvocations = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_cpu_worker_wait_process_invocation_count'
    $vkPreparedMeshCohortHits = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_prepared_mesh_operation_cohort_hits'
    $vkPreparedMeshCohortBuilds = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_prepared_mesh_operation_cohort_builds'
    $vkPreparedMeshOperationReuses = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_prepared_mesh_operation_reuses'
    $vkPreparedMeshLegacyHoleMaterializations = Get-MonotonicCounterDelta -Samples $samples -Property 'vulkan_prepared_mesh_operation_legacy_hole_materializations'
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
    $vkCommandChainWorkerQueuedChains = Get-NumericStats -Samples $samples -Property 'vulkan_command_chain_worker_queued_chains'
    $vkCommandChainWorkersStarted = Get-NumericStats -Samples $samples -Property 'vulkan_command_chain_workers_started'
    $vkCommandChainPeakConcurrentWorkers = Get-NumericStats -Samples $samples -Property 'vulkan_command_chain_peak_concurrent_workers'
    $vkCommandChainWorkerRecord = Get-NumericStats -Samples $samples -Property 'vulkan_command_chain_worker_record_ms' -PositiveOnly
    $vkCommandChainWorkerActiveSpan = Get-NumericStats -Samples $samples -Property 'vulkan_command_chain_worker_active_span_ms' -PositiveOnly
    $vkCommandChainWorkerOverlap = Get-NumericStats -Samples $samples -Property 'vulkan_command_chain_worker_overlap_ms' -PositiveOnly
    $vkCommandChainWorkerMerge = Get-NumericStats -Samples $samples -Property 'vulkan_command_chain_worker_merge_ms' -PositiveOnly
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
    $vkAutoUniformFallbackDrawsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_auto_uniform_legacy_fallback_draws'
    $vkAutoUniformFallbackReasonNames = @(
        'binding_snapshot_ineligible',
        'program_unavailable',
        'invalid_buffer_size',
        'binding_schema_unavailable',
        'binding_schema_mismatch',
        'invalid_member_name',
        'unsupported_shader_type',
        'invalid_destination_range',
        'invalid_array_layout',
        'struct_snapshot_required',
        'engine_source_type_mismatch',
        'mesh_state_source_type_mismatch',
        'typed_engine_source_unavailable',
        'typed_engine_write_failed',
        'typed_temporal_write_failed',
        'typed_mesh_state_source_unavailable',
        'typed_mesh_state_write_failed',
        'typed_material_or_runtime_write_failed'
    )
    $vkAutoUniformFallbackReasonTotals = [ordered]@{}
    foreach ($reasonName in $vkAutoUniformFallbackReasonNames) {
        $reasonTotal = Sum-NumericProperty `
            -Samples $samples `
            -Property "vulkan_auto_uniform_fallback_$reasonName"
        $vkAutoUniformFallbackReasonTotals[$reasonName] = $reasonTotal
    }
    $vkAutoUniformFallbackReasonSummary = @(
        $vkAutoUniformFallbackReasonTotals.GetEnumerator() |
            Where-Object { [double]$_.Value -gt 0.0 } |
            ForEach-Object { "$($_.Key)=$($_.Value)" }
    ) -join ','
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
    # A primary can only be reused while its recorded structural and binding
    # dependencies remain valid. Records caused by attachment rotation, resource
    # retirement, pipeline publication, swapchain lifecycle, or first-use
    # initialization are therefore not eligible reuse decisions. Frame-data-only
    # misses remain eligible and must still fail the reuse gate.
    $vkPrimaryReuseIneligibleReasonMask =
        4 +       # ForcedDirty
        8 +       # FrameOpSignature
        16 +      # ResourcePlan
        32 +      # ProfilerMode
        128 +     # DynamicOverlay
        256 +     # SwapchainLifecycle
        512 +     # CommandChainPrimary
        1024 +    # PrimaryFrameState
        2048 +    # DescriptorGeneration
        4096 +    # ResourceAllocation
        8192 +    # Evicted
        131072 +  # PipelineGeneration
        262144 +  # SecondaryInvalid
        524288    # VolatileCommand
    [double]$vkEligiblePrimaryRecordsTotal = 0
    [double]$vkIneligiblePrimaryRecordsTotal = 0
    [long]$vkEligiblePrimaryRecordAllocatedBytesTotal = 0
    [long]$vkIneligiblePrimaryRecordAllocatedBytesTotal = 0
    foreach ($sample in $samples) {
        [double]$recordCount = Get-SamplePropertyValue -Sample $sample -Property 'vulkan_command_buffer_record_count'
        if ($recordCount -le 0) {
            continue
        }

        [long]$reasonMask = Get-SamplePropertyValue -Sample $sample -Property 'vulkan_command_buffer_decision_reason_mask'
        [long]$allocatedBytes = Get-SamplePropertyValue -Sample $sample -Property 'vulkan_record_command_buffer_allocated_bytes'
        if (($reasonMask -band $vkPrimaryReuseIneligibleReasonMask) -eq 0) {
            $vkEligiblePrimaryRecordsTotal += $recordCount
            $vkEligiblePrimaryRecordAllocatedBytesTotal += $allocatedBytes
        } else {
            $vkIneligiblePrimaryRecordsTotal += $recordCount
            $vkIneligiblePrimaryRecordAllocatedBytesTotal += $allocatedBytes
        }
    }
    $vkGateRecordCommandBufferAllocatedBytesTotal = if ($UseEligiblePrimaryReuseRatio) {
        $vkEligiblePrimaryRecordAllocatedBytesTotal
    } else {
        $vkRecordCommandBufferAllocatedBytesTotal
    }
    $vkEligiblePrimaryReuseDecisionTotal = $vkCommandBufferCleanReuseTotal + $vkEligiblePrimaryRecordsTotal
    $vkEligiblePrimaryReuseRatio = if ($vkEligiblePrimaryReuseDecisionTotal -gt 0) {
        [Math]::Round($vkCommandBufferCleanReuseTotal / $vkEligiblePrimaryReuseDecisionTotal, 6)
    } else {
        0.0
    }
    $vkGatePrimaryReuseRatio = if ($UseEligiblePrimaryReuseRatio) {
        $vkEligiblePrimaryReuseRatio
    } else {
        $vkCommandBufferCleanReuseRatio
    }
    $plannerPruneTotal = Sum-NumericProperty -Samples $samples -Property 'frame_output_planner_prune_count'
    $globalInFlightWaitTotal = Sum-NumericProperty -Samples $samples -Property 'frame_output_global_in_flight_wait_count'
    $forceFlushTotal = Sum-NumericProperty -Samples $samples -Property 'frame_output_force_flush_count'
    $submissionRejectionTotal = Sum-NumericProperty -Samples $samples -Property 'frame_output_submission_rejection_count'
    # Readiness rejection can happen before any native submission is attempted.
    # Its short CPU duration is a failed frame, never a performance improvement.
    $failedVulkanFrameSamples = @($samples | Where-Object {
        $_.active_render_backend -eq 'Vulkan' -and $_.vulkan_frame_outcome -in @('Rejected', 'Failed')
    }).Count
    $unapprovedPolicyEventTotal = Sum-NumericProperty -Samples $samples -Property 'frame_output_unapproved_policy_event_count'
    $workloadIdentityHashes = @($samples | ForEach-Object {
        $value = Get-SamplePropertyValue -Sample $_ -Property 'frame_output_workload_identity_hash'
        if ($null -ne $value -and [string]$value -ne '0') { [string]$value }
    } | Select-Object -Unique)
    $identitySamples = if ($samples.Count -gt 0) { $samples } else { $allSamples }
    $activeRenderBackends = @($identitySamples | ForEach-Object {
        $value = Get-SamplePropertyValue -Sample $_ -Property 'active_render_backend'
        if (-not [string]::IsNullOrWhiteSpace([string]$value)) { [string]$value }
    } | Select-Object -Unique)
    $effectiveStrategies = @($identitySamples | ForEach-Object {
        $value = Get-SamplePropertyValue -Sample $_ -Property 'effective_strategy'
        if (-not [string]::IsNullOrWhiteSpace([string]$value)) { [string]$value }
    } | Select-Object -Unique)
    [double]$collectGenerationAgeMax = 0
    foreach ($sample in $samples) {
        [double]$requestedGeneration = Get-SamplePropertyValue -Sample $sample -Property 'collect_generation_requested'
        [double]$consumedGeneration = Get-SamplePropertyValue -Sample $sample -Property 'collect_generation_consumed'
        $collectGenerationAgeMax = [Math]::Max($collectGenerationAgeMax, [Math]::Max(0, $requestedGeneration - $consumedGeneration))
    }
    $validationLayersEnabledSamples = @($samples | Where-Object { $_.validation_layers_enabled -eq $true }).Count
    $firstCompletedSample = $null
    foreach ($sample in $allSamples) {
        if (($sample.active_render_backend -eq 'Vulkan' -and $sample.vulkan_frame_outcome -eq 'Completed') -or
            ($sample.active_render_backend -ne 'Vulkan' -and $sample.completed_frame_id -gt 0)) {
            $firstCompletedSample = $sample
            break
        }
    }
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
    $cpuDumpCount = if ($logDir -and (Test-Path -LiteralPath $logDir)) {
        @(Get-ChildItem -LiteralPath $logDir -Filter 'profiler-cpu-frame-*.log' -File -ErrorAction SilentlyContinue).Count
    } else {
        0
    }

    $noteParts = New-Object System.Collections.Generic.List[string]
    if ($forcedStop) { $noteParts.Add('forced stop; GPU timing dump may be missing') | Out-Null }
    if (-not $mcpDiagnosticsSucceeded) {
        $noteParts.Add("MCP CPU/GPU diagnostics unavailable: $mcpDiagnosticError") | Out-Null
    }
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
        if ($validatedReadbackTotal -ne 0 -or $validatedMappedTotal -ne 0) {
            $noteParts.Add("zero-readback violation scope=$ZeroReadbackValidationScope capture(readbackBytes=$captureReadbackTotal mappedBuffers=$captureMappedTotal) all(readbackBytes=$allReadbackTotal mappedBuffers=$allMappedTotal)") | Out-Null
        }
    }
    if ($Strategy -eq 'GpuMeshletZeroReadback') {
        if ($null -eq $meshletRenderPathSourceHashCalls -or $null -eq $meshletRenderPathDiskCalls -or $null -eq $meshletRenderPathCookerCalls -or $null -eq $meshletMappedBytes) {
            $noteParts.Add('meshlet production telemetry incomplete') | Out-Null
        } elseif ($meshletRenderPathSourceHashCalls -ne 0 -or $meshletRenderPathDiskCalls -ne 0 -or $meshletRenderPathCookerCalls -ne 0 -or $meshletMappedBytes -ne 0) {
            $noteParts.Add("meshlet render-path violation hash=$meshletRenderPathSourceHashCalls disk=$meshletRenderPathDiskCalls cooker=$meshletRenderPathCookerCalls mappedBytes=$meshletMappedBytes") | Out-Null
        }
        if ($VulkanGpuDrivenProfile -eq 'Diagnostics') {
            if ($null -eq $meshletTaskRecordsEmitted -or $null -eq $meshletDelayedDispatchGroups -or
                $meshletTaskRecordsEmitted -le 0 -or $meshletDelayedDispatchGroups -le 0) {
                $noteParts.Add("meshlet production work was not observed from delayed GPU diagnostics; taskRecords=$meshletTaskRecordsEmitted dispatchX=$meshletDelayedDispatchGroups diagnosticReadbackBytes=$meshletDiagnosticReadbackBytes failedRung=$meshletCapabilityFailedRung") | Out-Null
            }
        } elseif ($null -eq $meshletProductionFrames -or $null -eq $vulkanFrameOpMeshTaskDispatchCount -or
            $meshletProductionFrames -le 0 -or $vulkanFrameOpMeshTaskDispatchCount -le 0) {
            $noteParts.Add("meshlet production work was not observed from the zero-readback recording path; productionFrames=$meshletProductionFrames frameOpDispatches=$vulkanFrameOpMeshTaskDispatchCount apiEnqueues=$meshletDispatchCalls failedRung=$meshletCapabilityFailedRung") | Out-Null
        }
    }
    if ($FailOnSteadyStateResourceChurn -and ($vkRetiredResourceCountTotal -gt 0 -or $plannerPruneTotal -gt 0 -or $globalInFlightWaitTotal -gt 0 -or $forceFlushTotal -gt 0 -or $vkDescriptorPoolCreatesTotal -gt 0 -or $vkLifetimeLiveResourcesMax -gt $MaxSteadyStateVulkanLiveResources -or $vkTrackedDescriptorSetsMax -gt $MaxSteadyStateVulkanDescriptorSets)) {
        $noteParts.Add("steady-state resource churn failure retired=$vkRetiredResourceCountTotal planReplacements=$vkResourcePlanReplacementsTotal plannerPrunes=$plannerPruneTotal globalWaits=$globalInFlightWaitTotal forceFlushes=$forceFlushTotal descriptorPoolCreates=$vkDescriptorPoolCreatesTotal liveResourcesMax=$vkLifetimeLiveResourcesMax/$MaxSteadyStateVulkanLiveResources descriptorSetsMax=$vkTrackedDescriptorSetsMax/$MaxSteadyStateVulkanDescriptorSets") | Out-Null
    }
    $vkStrictCommandBufferChurn =
        -not $UseEligiblePrimaryReuseRatio -and
        ($vkCommandBufferForcedDirtyTotal -gt 0 -or $vkCommandBufferDirtySummaries.Count -gt 0)
    if ($FailOnSteadyStateCommandBufferChurn -and ($vkStrictCommandBufferChurn -or $vkGatePrimaryReuseRatio -lt $MinSteadyStateCommandBufferCleanReuseRatio -or $vkGlobalFallbackInvalidationsTotal -gt 0)) {
        $noteParts.Add("steady-state command-buffer churn failure records=$vkCommandBufferRecordsTotal reuse=$vkCommandBufferCleanReuseTotal forcedDirty=$vkCommandBufferForcedDirtyTotal rawRatio=$vkCommandBufferCleanReuseRatio eligibleRecords=$vkEligiblePrimaryRecordsTotal ineligibleRecords=$vkIneligiblePrimaryRecordsTotal eligibleRatio=$vkEligiblePrimaryReuseRatio exactVariants=$vkExactVariantsDirtiedTotal exactChains=$vkExactCommandChainsDirtiedTotal unrelatedPreserved=$vkUnrelatedVariantsPreservedTotal globalFallbacks=$vkGlobalFallbackInvalidationsTotal dirty=$($vkCommandBufferDirtySummaries -join '|')") | Out-Null
    }
    if ($FailOnSteadyStateCommandBufferAllocations -and $vkGateRecordCommandBufferAllocatedBytesTotal -gt $MaxSteadyStateRecordCommandBufferAllocatedBytes) {
        $noteParts.Add("steady-state command-buffer allocation failure gateBytes=$vkGateRecordCommandBufferAllocatedBytesTotal rawBytes=$vkRecordCommandBufferAllocatedBytesTotal eligibleBytes=$vkEligiblePrimaryRecordAllocatedBytesTotal ineligibleBytes=$vkIneligiblePrimaryRecordAllocatedBytesTotal threshold=$MaxSteadyStateRecordCommandBufferAllocatedBytes") | Out-Null
    }
    if ($FailOnSteadyStateBindingFallback -and $vkAutoUniformFallbackDrawsTotal -gt 0) {
        $noteParts.Add("steady-state binding fallback failure draws=$vkAutoUniformFallbackDrawsTotal reasons=$vkAutoUniformFallbackReasonSummary") | Out-Null
    }
    if ($workloadIdentityHashes.Count -ne 1) {
        $noteParts.Add("capture workload identity changed or missing: identities=$($workloadIdentityHashes -join ',')") | Out-Null
    }
    if ($RenderBackend -ne 'Configured' -and
        ($activeRenderBackends.Count -ne 1 -or $activeRenderBackends[0] -ine $RenderBackend)) {
        $noteParts.Add("requested backend $RenderBackend but captured backends were $($activeRenderBackends -join ',')") | Out-Null
    }
    if ($effectiveStrategies.Count -ne 1 -or $effectiveStrategies[0] -ine $Strategy) {
        $noteParts.Add("requested strategy $Strategy but captured strategies were $($effectiveStrategies -join ',')") | Out-Null
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
    if (-not $diagnosticLossPassed) {
        $noteParts.Add("diagnostic loss gate failed complete=$diagnosticLossEvidenceComplete overflowDiscarded=$profilerOverflowDiscardedEvents pendingCompletedDiscarded=$profilerPendingCompletedDiscardedEvents") | Out-Null
    }
    if (-not $retentionPassed) {
        $noteParts.Add("retention gate failed complete=$retentionEvidenceComplete managed=$($managedHeapEndpoint.Passed) private=$($privateMemoryEndpoint.Passed) native=$($vulkanLiveResourceEndpoint.Passed) descriptors=$($vulkanDescriptorSetEndpoint.Passed) backlogs=$requiredBacklogsReturnedToBaseline") | Out-Null
    }

    return [pscustomobject]@{
        Strategy = $Strategy
        Repetition = $Repetition
        Configuration = $Configuration
        RequestedRenderBackend = $RenderBackend
        ActiveRenderBackend = $activeRenderBackends -join ','
        EffectiveStrategy = $effectiveStrategies -join ','
        UnitTestingWorldSettingsPath = $UnitTestingWorldSettingsPath
        CodeProfiler = $CodeProfiler
        CacheMode = $CacheMode
        UnitTestVrMode = $UnitTestVrMode
        VulkanRenderTargetMode = $VulkanRenderTargetMode
        VulkanPrimaryReuse = $VulkanPrimaryReuse
        VulkanCommandChains = $VulkanCommandChains
        VulkanParallelCommandChainRecording = $VulkanParallelCommandChainRecording
        VulkanCommandChainBenchmarkForceRerecord = [bool]$VulkanCommandChainBenchmarkForceRerecord
        VulkanParallelSecondaryRecording = $VulkanParallelSecondaryRecording
        VulkanValidation = [bool]$VulkanValidation
        VulkanDiagnosticPreset = $VulkanDiagnosticPreset
        VulkanCommandBufferLabels = [bool]$VulkanCommandBufferLabels
        OcclusionCullingMode = $OcclusionCullingMode
        ZeroReadbackMaterialDrawPath = $ZeroReadbackMaterialDrawPath
        ZeroReadbackValidationScope = $ZeroReadbackValidationScope
        ProfileScene = $ProfileScene
        ProfileCamera = $ProfileCamera
        CameraPositionX = if ($hasFixedCameraPose) { $CameraPositionX } else { $null }
        CameraPositionY = if ($hasFixedCameraPose) { $CameraPositionY } else { $null }
        CameraPositionZ = if ($hasFixedCameraPose) { $CameraPositionZ } else { $null }
        CameraLookAtX = if ($hasFixedCameraPose) { $CameraLookAtX } else { $null }
        CameraLookAtY = if ($hasFixedCameraPose) { $CameraLookAtY } else { $null }
        CameraLookAtZ = if ($hasFixedCameraPose) { $CameraLookAtZ } else { $null }
        CameraPoseVerified = -not $hasFixedCameraPose -or $null -ne $fixedCameraReadback
        CameraPoseReadback = $fixedCameraReadback
        MotionCaptureSec = $MotionCaptureSec
        MotionCameraPositionX = if ($hasCameraMotion) { $MotionCameraPositionX } else { $null }
        MotionCameraPositionY = if ($hasCameraMotion) { $MotionCameraPositionY } else { $null }
        MotionCameraPositionZ = if ($hasCameraMotion) { $MotionCameraPositionZ } else { $null }
        MotionCameraLookAtX = if ($hasCameraMotion) { $MotionCameraLookAtX } else { $null }
        MotionCameraLookAtY = if ($hasCameraMotion) { $MotionCameraLookAtY } else { $null }
        MotionCameraLookAtZ = if ($hasCameraMotion) { $MotionCameraLookAtZ } else { $null }
        MotionCameraPoseVerified = -not $hasCameraMotion -or $null -ne $motionCameraReadback
        MotionCameraPoseReadback = $motionCameraReadback
        ProfileLights = $ProfileLights
        ProfileViewport = $ProfileViewport
        RenderScale = $RenderScale
        WindowWidth = if ($WindowWidth -gt 0) { $WindowWidth } else { $null }
        WindowHeight = if ($WindowHeight -gt 0) { $WindowHeight } else { $null }
        SampleIntervalFrames = $SampleIntervalFrames
        VulkanGpuDrivenProfile = $VulkanGpuDrivenProfile
        GpuClockPolicy = $GpuClockPolicy
        TargetRefreshHz = if ($TargetRefreshHz -gt 0) { $TargetRefreshHz } else { $null }
        VulkanPresentationProfile = $VulkanPresentationProfile
        GpuTimestampDense = [bool]$GpuTimestampDense
        StartupPhase = 'process-launch-to-first-sample'
        ProcessStartUtc = if ($null -ne $processStartUtc) { $processStartUtc.ToString('O') } else { $null }
        FirstObservedCompletedFrameLatencyMs = if ($null -ne $processStartUtc -and $null -ne $firstCompletedSample) {
            ((Get-RenderStatsSampleTimestampUtc -Sample $firstCompletedSample) - $processStartUtc).TotalMilliseconds
        } else { $null }
        WarmupPhaseSec = $WarmupSec
        SteadyStatePhaseSec = $CaptureSec
        StabilityGateEnabled = -not [bool]$NoStabilityGate
        AllowWorkloadIdentityChanges = [bool]$AllowWorkloadIdentityChanges
        StabilityProfile = $StabilityProfile
        MinimumGpuSceneCommandCount = $MinSteadyStateGpuSceneCommandCount
        StabilityReady = $stabilityReady
        StabilityWaitSec = $stabilityWaitSec
        StabilityReason = $stabilityReason
        AdmissionScreenshotCaptured = $admissionScreenshotCaptured
        AdmissionScreenshotPath = $admissionScreenshotPath
        AdmissionScreenshotColorBuckets = $admissionScreenshotColorBuckets
        AdmissionScreenshotLuminanceRange = $admissionScreenshotLuminanceRange
        AdmissionScreenshotError = $admissionScreenshotError
        PostScreenshotStabilityWaitSec = $postScreenshotStabilityWaitSec
        StableWorkloadIdentityHash = $stableWorkloadIdentityHash
        StableContentGeneration = $stableContentGeneration
        StableResourceGeneration = $stableResourceGeneration
        CaptureWorkloadIdentityHash = if ($workloadIdentityHashes.Count -eq 1) { $workloadIdentityHashes[0] } else { $workloadIdentityHashes -join ',' }
        CaptureWorkloadIdentityCount = $workloadIdentityHashes.Count
        StreamingPhase = 'included-in-startup-and-warmup-until asset counters stabilize'
        Samples = $samples.Count
        AllSamples = $allSamples.Count
        DiagnosticLossEvidenceComplete = $diagnosticLossEvidenceComplete
        DiagnosticLossPassed = $diagnosticLossPassed
        CodeProfilerOverflowDiscardedEvents = $profilerOverflowDiscardedEvents
        CodeProfilerPendingCompletedDiscardedEvents = $profilerPendingCompletedDiscardedEvents
        RetentionEvidenceComplete = $retentionEvidenceComplete
        RetentionPassed = $retentionPassed
        ManagedHeapEndpoint = $managedHeapEndpoint
        PrivateMemoryEndpoint = $privateMemoryEndpoint
        VulkanLiveResourceEndpoint = $vulkanLiveResourceEndpoint
        VulkanDescriptorSetEndpoint = $vulkanDescriptorSetEndpoint
        RequiredBacklogEvidenceComplete = $requiredBacklogEvidenceComplete
        RequiredBacklogsReturnedToBaseline = $requiredBacklogsReturnedToBaseline
        RequiredBacklogEndpoints = $requiredBacklogEndpoints
        CaptureStartUtc = $captureStartUtc.ToString('O')
        CaptureEndUtc = $captureEndUtc.ToString('O')
        StationaryInterval = $stationaryInterval
        MotionInterval = $motionInterval
        RenderAvgMs = $render.Avg
        RenderP50Ms = $render.P50
        RenderP90Ms = $render.P90
        RenderP95Ms = $render.P95
        RenderP99Ms = $render.P99
        RenderWorstMs = $render.Max
        RenderOutsideVulkanP50Ms = $renderOutsideVulkan.P50
        RenderOutsideVulkanP90Ms = $renderOutsideVulkan.P90
        RenderOutsideVulkanP95Ms = $renderOutsideVulkan.P95
        RenderOutsideVulkanP99Ms = $renderOutsideVulkan.P99
        UpdateP50Ms = $update.P50
        UpdateP90Ms = $update.P90
        UpdateP95Ms = $update.P95
        UpdateP99Ms = $update.P99
        UpdateWorstMs = $update.Max
        CollectVisibleP50Ms = $collect.P50
        CollectVisibleP90Ms = $collect.P90
        CollectVisibleP95Ms = $collect.P95
        CollectVisibleP99Ms = $collect.P99
        CollectVisibleWorstMs = $collect.Max
        CollectWaitForRenderP50Ms = $collectWaitForRender.P50
        CollectWaitForRenderP90Ms = $collectWaitForRender.P90
        CollectWaitForRenderP95Ms = $collectWaitForRender.P95
        CollectWaitForRenderWorstMs = $collectWaitForRender.Max
        RenderWaitForCollectP50Ms = $renderWaitForCollect.P50
        RenderWaitForCollectP90Ms = $renderWaitForCollect.P90
        RenderWaitForCollectP95Ms = $renderWaitForCollect.P95
        RenderWaitForCollectWorstMs = $renderWaitForCollect.Max
        CollectGenerationAgeMaxFrames = $collectGenerationAgeMax
        StaleCollectReuseFramesTotal = Sum-NumericProperty -Samples $samples -Property 'stale_collect_reuse_frames'
        GpuSamples = $gpu.Count
        GpuReadySamples = $gpuReadyCount
        GpuTimingSource = $gpuTiming.Source
        GpuCoverageDenominator = $gpuTiming.CoverageDenominator
        GpuCoveragePercent = $gpuTiming.CoveragePercent
        GpuDuplicateSourceCount = $gpuTiming.DuplicateSourceCount
        GpuOutOfWindowSourceCount = $gpuTiming.OutOfWindowSourceCount
        GpuMaxAgeFrames = $gpuTiming.MaxAgeFrames
        GpuP50Ms = $gpu.P50
        GpuP90Ms = $gpu.P90
        GpuP95Ms = $gpu.P95
        GpuP99Ms = $gpu.P99
        GpuWorstMs = $gpu.Max
        VulkanGpuCommandBufferP50Ms = $vulkanGpuCommandBuffer.P50
        VulkanGpuCommandBufferP90Ms = $vulkanGpuCommandBuffer.P90
        VulkanGpuCommandBufferP95Ms = $vulkanGpuCommandBuffer.P95
        VulkanGpuCommandBufferP99Ms = $vulkanGpuCommandBuffer.P99
        VulkanGpuCommandBufferWorstMs = $vulkanGpuCommandBuffer.Max
        RenderMinusGpuP95Ms = $gap.P95
        VulkanFrameP50Ms = $vkFrame.P50
        VulkanFrameP90Ms = $vkFrame.P90
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
        VulkanFrameOpPreparationP50Ms = $vkFrameOpPreparation.P50
        VulkanFrameOpPreparationP95Ms = $vkFrameOpPreparation.P95
        VulkanFrameOpPreparationP99Ms = $vkFrameOpPreparation.P99
        VulkanRawMeshRequestDrainP50Ms = $vkRawMeshRequestDrain.P50
        VulkanRawMeshRequestDrainP95Ms = $vkRawMeshRequestDrain.P95
        VulkanRawMeshRequestDrainInvocations = $vkRawMeshRequestDrainInvocations
        VulkanFrameOpCohortP50Ms = $vkFrameOpCohort.P50
        VulkanFrameOpCohortP95Ms = $vkFrameOpCohort.P95
        VulkanFrameOpCohortInvocations = $vkFrameOpCohortInvocations
        VulkanPreparedMeshBindingValidationP50Ms = $vkPreparedMeshBindingValidation.P50
        VulkanPreparedMeshBindingValidationP95Ms = $vkPreparedMeshBindingValidation.P95
        VulkanPreparedMeshBindingValidationInvocations = $vkPreparedMeshBindingValidationInvocations
        VulkanPreparedMeshHoleMaterializationP50Ms = $vkPreparedMeshHoleMaterialization.P50
        VulkanPreparedMeshHoleMaterializationP95Ms = $vkPreparedMeshHoleMaterialization.P95
        VulkanPreparedMeshHoleMaterializationInvocations = $vkPreparedMeshHoleMaterializationInvocations
        VulkanFrameOpResourceUseLoweringP50Ms = $vkFrameOpResourceUseLowering.P50
        VulkanFrameOpResourceUseLoweringP95Ms = $vkFrameOpResourceUseLowering.P95
        VulkanFrameOpResourceUseLoweringInvocations = $vkFrameOpResourceUseLoweringInvocations
        VulkanFrameOpPlanP50Ms = $vkFrameOpPlan.P50
        VulkanFrameOpPlanP95Ms = $vkFrameOpPlan.P95
        VulkanFrameOpPlanInvocations = $vkFrameOpPlanInvocations
        GpuDrivenIndirectConstructionP50Ms = $gpuDrivenIndirectConstruction.P50
        GpuDrivenIndirectConstructionP95Ms = $gpuDrivenIndirectConstruction.P95
        VulkanPrimaryCommandEncodingInvocations = $vkPrimaryCommandEncodingInvocations
        VulkanWorkerWaitP50Ms = $vkWorkerWait.P50
        VulkanWorkerWaitP95Ms = $vkWorkerWait.P95
        VulkanWorkerWaitInvocations = $vkWorkerWaitInvocations
        VulkanPreparedMeshCohortHits = $vkPreparedMeshCohortHits
        VulkanPreparedMeshCohortBuilds = $vkPreparedMeshCohortBuilds
        VulkanPreparedMeshOperationReuses = $vkPreparedMeshOperationReuses
        VulkanPreparedMeshLegacyHoleMaterializations = $vkPreparedMeshLegacyHoleMaterializations
        VulkanResourcePlanningP50Ms = $vkResourcePlanning.P50
        VulkanResourcePlanningP95Ms = $vkResourcePlanning.P95
        VulkanResourcePlanningP99Ms = $vkResourcePlanning.P99
        VulkanFrameDataRefreshP50Ms = $vkFrameDataRefresh.P50
        VulkanFrameDataRefreshP95Ms = $vkFrameDataRefresh.P95
        VulkanFrameDataRefreshP99Ms = $vkFrameDataRefresh.P99
        VulkanPrimaryCommandEncodingP50Ms = $vkPrimaryCommandEncoding.P50
        VulkanPrimaryCommandEncodingP95Ms = $vkPrimaryCommandEncoding.P95
        VulkanPrimaryCommandEncodingP99Ms = $vkPrimaryCommandEncoding.P99
        VulkanSecondaryRecordingP50Ms = $vkSecondaryRecording.P50
        VulkanSecondaryRecordingP95Ms = $vkSecondaryRecording.P95
        VulkanSecondaryRecordingP99Ms = $vkSecondaryRecording.P99
        VulkanRecordCommandBufferAllocatedBytesTotal = $vkRecordCommandBufferAllocatedBytesTotal
        VulkanEligiblePrimaryRecordAllocatedBytesTotal = $vkEligiblePrimaryRecordAllocatedBytesTotal
        VulkanIneligiblePrimaryRecordAllocatedBytesTotal = $vkIneligiblePrimaryRecordAllocatedBytesTotal
        VulkanGateRecordCommandBufferAllocatedBytesTotal = $vkGateRecordCommandBufferAllocatedBytesTotal
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
        VulkanAutoUniformFallbackDrawsTotal = $vkAutoUniformFallbackDrawsTotal
        VulkanAutoUniformFallbackReasonTotals = [pscustomobject]$vkAutoUniformFallbackReasonTotals
        VulkanAutoUniformFallbackReasonSummary = $vkAutoUniformFallbackReasonSummary
        VulkanCommandBufferCleanReuseRatio = $vkCommandBufferCleanReuseRatio
        VulkanEligiblePrimaryCommandBufferRecordsTotal = $vkEligiblePrimaryRecordsTotal
        VulkanIneligiblePrimaryCommandBufferRecordsTotal = $vkIneligiblePrimaryRecordsTotal
        VulkanEligiblePrimaryCommandBuffersReusedTotal = $vkCommandBufferCleanReuseTotal
        VulkanEligiblePrimaryCommandBufferReuseDecisionsTotal = $vkEligiblePrimaryReuseDecisionTotal
        VulkanEligiblePrimaryCommandBufferReuseRatio = $vkEligiblePrimaryReuseRatio
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
        VulkanCommandChainWorkerQueuedChainsP50 = $vkCommandChainWorkerQueuedChains.P50
        VulkanCommandChainWorkerQueuedChainsTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_command_chain_worker_queued_chains'
        VulkanCommandChainWorkersStartedP50 = $vkCommandChainWorkersStarted.P50
        VulkanCommandChainWorkersStartedTotal = Sum-NumericProperty -Samples $samples -Property 'vulkan_command_chain_workers_started'
        VulkanCommandChainPeakConcurrentWorkersMax = $vkCommandChainPeakConcurrentWorkers.Max
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
        VulkanCommandChainWorkerActiveSpanP50Ms = $vkCommandChainWorkerActiveSpan.P50
        VulkanCommandChainWorkerActiveSpanP95Ms = $vkCommandChainWorkerActiveSpan.P95
        VulkanCommandChainWorkerOverlapP50Ms = $vkCommandChainWorkerOverlap.P50
        VulkanCommandChainWorkerOverlapP95Ms = $vkCommandChainWorkerOverlap.P95
        VulkanCommandChainWorkerMergeP50Ms = $vkCommandChainWorkerMerge.P50
        VulkanCommandChainWorkerMergeP95Ms = $vkCommandChainWorkerMerge.P95
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
        VulkanFailedFrameSamples = $failedVulkanFrameSamples
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
        GpuDrivenConfiguredMaterialSlotsP50 = (Get-NumericStats -Samples $samples -Property 'gpu_driven_configured_material_slots').P50
        GpuDrivenMaterialPassGroupsP50 = (Get-NumericStats -Samples $samples -Property 'gpu_driven_material_pass_groups').P50
        GpuDrivenCommandCapacityP50 = (Get-NumericStats -Samples $samples -Property 'gpu_driven_command_capacity').P50
        GpuDrivenCommandCapacityMax = Max-NumericProperty -Samples $samples -Property 'gpu_driven_command_capacity'
        GpuDrivenActiveCommandCountP50 = (Get-NumericStats -Samples $samples -Property 'gpu_driven_active_command_count').P50
        GpuDrivenActiveCommandCountMax = Max-NumericProperty -Samples $samples -Property 'gpu_driven_active_command_count'
        GpuDrivenMaterialLookupCapacityP50 = (Get-NumericStats -Samples $samples -Property 'gpu_driven_material_lookup_capacity').P50
        GpuDrivenMaterialLookupCapacityMax = Max-NumericProperty -Samples $samples -Property 'gpu_driven_material_lookup_capacity'
        GpuDrivenActiveMaterialSlotsP50 = (Get-NumericStats -Samples $samples -Property 'gpu_driven_active_material_slots').P50
        GpuDrivenActiveMaterialSlotsMax = Max-NumericProperty -Samples $samples -Property 'gpu_driven_active_material_slots'
        GpuDrivenRequiredMaterialRowsP50 = (Get-NumericStats -Samples $samples -Property 'gpu_driven_required_material_rows').P50
        GpuDrivenReadyMaterialRowsP50 = (Get-NumericStats -Samples $samples -Property 'gpu_driven_ready_material_rows').P50
        GpuDrivenNonReadyMaterialTextureReferencesTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_driven_non_ready_material_texture_references'
        GpuDrivenInvalidMaterialIdsTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_driven_invalid_material_ids'
        GpuDrivenFallbackSubmittedMaterialRowsTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_driven_fallback_submitted_material_rows'
        GpuDrivenMaterialTablePublicationGenerationMax = Max-NumericProperty -Samples $samples -Property 'gpu_driven_material_table_publication_generation'
        GpuDrivenMaterialDescriptorPublicationGenerationMax = Max-NumericProperty -Samples $samples -Property 'gpu_driven_material_descriptor_publication_generation'
        GpuDrivenValidationCapacityMultiplier = (Get-NumericStats -Samples $samples -Property 'gpu_driven_validation_capacity_multiplier').P50
        GpuDrivenValidationCapacityFloor = (Get-NumericStats -Samples $samples -Property 'gpu_driven_validation_capacity_floor').P50
        GpuDrivenCulledCommandCountP50 = (Get-NumericStats -Samples $samples -Property 'gpu_driven_culled_command_count').P50
        VulkanRequestedDrawsP50 = (Get-NumericStats -Samples $samples -Property 'vulkan_requested_draws').P50
        VulkanConsumedDrawsP50 = (Get-NumericStats -Samples $samples -Property 'vulkan_consumed_draws').P50
        GpuDrivenUnsupportedCompactPassesTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_driven_unsupported_compact_passes'
        GpuSceneCommandCountP50 = (Get-NumericStats -Samples $samples -Property 'gpu_scene_command_count').P50
        GpuSceneCommandCountMax = Max-NumericProperty -Samples $samples -Property 'gpu_scene_command_count'
        GpuDrivenDelayedDiagnosticReadbackBytesTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_driven_delayed_diagnostic_readback_bytes'
        GpuDrivenSubmissionManagedAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_driven_submission_managed_allocated_bytes'
        GpuDrivenSubmissionBackendManagedAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_driven_submission_backend_managed_allocated_bytes'
        GpuDrivenSubmissionOwnedManagedAllocatedBytesTotal = Sum-NumericProperty -Samples $samples -Property 'gpu_driven_submission_owned_managed_allocated_bytes'
        MaterialBindingRung = $lastMaterialBindingRung
        MaterialBindingRungReason = $lastMaterialBindingRungReason
        GpuCompactionRung = $lastGpuCompactionRung
        GpuCompactionRungReason = $lastGpuCompactionRungReason
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
        MeshletColdImportBuilderCalls = $meshletColdImportBuilderCalls
        MeshletColdImportBuildMs = $meshletColdImportBuildMs
        MeshletColdImportAllocatedBytes = $meshletColdImportAllocatedBytes
        MeshletGeneratedLodCount = $meshletGeneratedLodCount
        MeshletCookedPayloadCount = $meshletCookedPayloadCount
        MeshletCookedMeshletCount = $meshletCookedMeshletCount
        MeshletSourceParserCalls = $meshletSourceParserCalls
        MeshletWarmPayloadHydrations = $meshletWarmPayloadHydrations
        MeshletRenderPathSourceHashCalls = $meshletRenderPathSourceHashCalls
        MeshletRenderPathDiskCalls = $meshletRenderPathDiskCalls
        MeshletRenderPathCookerCalls = $meshletRenderPathCookerCalls
        MeshletDispatchCalls = $meshletDispatchCalls
        MeshletDispatchGroups = $meshletDispatchGroups
        MeshletMappedBytes = $meshletMappedBytes
        MeshletVulkanCapabilityFailedRung = $meshletCapabilityFailedRung
        MeshletResolvedPass = $meshletResolvedPass
        MeshletResolvedRoute = $meshletResolvedRoute
        MeshletPrimaryRouteReason = $meshletPrimaryRouteReason
        MeshletResolvedRows = $meshletResolvedRows
        MeshletResolvedTaskGroups = $meshletResolvedTaskGroups
        MeshletRequestedFrames = $meshletRequestedFrames
        MeshletProductionFrames = $meshletProductionFrames
        VulkanFrameOpMeshTaskDispatchCount = $vulkanFrameOpMeshTaskDispatchCount
        MeshletTaskRecordsEmitted = $meshletTaskRecordsEmitted
        MeshletTaskRecordsFrustumCulled = $meshletTaskRecordsFrustumCulled
        MeshletTaskRecordsConeCulled = $meshletTaskRecordsConeCulled
        MeshletTaskRecordsHiZCulled = $meshletTaskRecordsHiZCulled
        MeshletDelayedDispatchGroups = $meshletDelayedDispatchGroups
        MeshletDiagnosticReadbackBytes = $meshletDiagnosticReadbackBytes
        MeshletBufferBytesResident = $meshletBufferBytesResident
        MeshletBufferLiveBytes = $meshletBufferLiveBytes
        MeshletBufferRetiredBytes = $meshletBufferRetiredBytes
        MeshletBufferRebuildCount = $meshletBufferRebuildCount
        MeshletBufferRetireCount = $meshletBufferRetireCount
        MeshletBufferRebuildsDuringCapture = $meshletBufferRebuildsDuringCapture
        MeshletBufferRetiresDuringCapture = $meshletBufferRetiresDuringCapture
        LastSampleUtc = $lastSampleUtc
        LastRenderFrameId = $lastRenderFrameId
        LastCompletedFrameId = $lastCompletedFrameId
        LastRenderMs = $lastRenderMs
        LastGpuMs = $lastGpuMs
        LastGpuReadbackBytes = $lastReadbackBytes
        LastGpuMappedBuffers = $lastMappedBuffers
        LastFallbackEvents = $lastFallbackEvents
        LastForbiddenFallbackEvents = $lastForbiddenFallbackEvents
        McpDiagnosticsSucceeded = $mcpDiagnosticsSucceeded
        McpDiagnosticError = $mcpDiagnosticError
        CpuTimingDumpFiles = $cpuDumpCount
        GpuTimingDumpFiles = $gpuDumpCount
        LogDir = $logDir
        Note = ($noteParts -join '; ')
    }
}

$stamp = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
$profileRunDir = New-SpeedProfileRunDirectory -Stamp $stamp
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

$summaryJson = Join-Path $profileRunDir 'summary.json'
$summaryText = Join-Path $profileRunDir 'summary.txt'
$runLogDirs = Join-Path $profileRunDir 'run-logdirs.txt'
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryJson -Encoding UTF8
@(
    "XRENGINE game loop / default render pipeline profile"
    "Created: $(Get-Date -Format o)"
    "ProfileRunDir: $profileRunDir"
    "Configuration: $Configuration"
    "RequestedRenderBackend: $RenderBackend"
    "UnitTestingWorldSettingsPath: $UnitTestingWorldSettingsPath"
    "ProfileMode: $ProfileMode"
    "CodeProfiler: $CodeProfiler"
    "CacheMode: $CacheMode"
    "ZeroReadbackMaterialDrawPath: $ZeroReadbackMaterialDrawPath"
    "ZeroReadbackValidationScope: $ZeroReadbackValidationScope"
    "Scene: $ProfileScene"
    "Camera: $ProfileCamera"
    "CameraPose: $(if ($hasFixedCameraPose) { 'position=({0},{1},{2}) lookAt=({3},{4},{5})' -f $CameraPositionX,$CameraPositionY,$CameraPositionZ,$CameraLookAtX,$CameraLookAtY,$CameraLookAtZ } else { 'not fixed by harness' })"
    "MotionCameraPose: $(if ($hasCameraMotion) { 'duration={0}s position=({1},{2},{3}) lookAt=({4},{5},{6})' -f $MotionCaptureSec,$MotionCameraPositionX,$MotionCameraPositionY,$MotionCameraPositionZ,$MotionCameraLookAtX,$MotionCameraLookAtY,$MotionCameraLookAtZ } else { 'disabled' })"
    "OcclusionCullingMode: $OcclusionCullingMode"
    "VulkanCommandChains: $VulkanCommandChains"
    "VulkanParallelCommandChainRecording: $VulkanParallelCommandChainRecording"
    "VulkanCommandChainBenchmarkForceRerecord: $([bool]$VulkanCommandChainBenchmarkForceRerecord)"
    "VulkanParallelSecondaryRecording: $VulkanParallelSecondaryRecording"
    "VulkanDiagnosticPreset: $VulkanDiagnosticPreset"
    "VulkanCommandBufferLabels: $([bool]$VulkanCommandBufferLabels)"
    "Lights: $ProfileLights"
    "Viewport: $ProfileViewport"
    "RenderScale: $RenderScale"
    "WindowSize: $(if ($WindowWidth -gt 0) { "${WindowWidth}x${WindowHeight}" } else { 'automatic' })"
    "SampleIntervalFrames: $SampleIntervalFrames"
    "VulkanGpuDrivenProfile: $VulkanGpuDrivenProfile"
    "GpuClockPolicy: $GpuClockPolicy"
    "TargetRefreshHz: $TargetRefreshHz"
    "VulkanPresentationProfile: $VulkanPresentationProfile"
    "GpuTimestampDense: $([bool]$GpuTimestampDense)"
    "WarmupSec: $WarmupSec"
    "StabilityGate: enabled=$(-not [bool]$NoStabilityGate) profile=$StabilityProfile windowSec=$StabilityWindowSec timeoutSec=$StabilityTimeoutSec"
    "BindingFallbackGate: enabled=$([bool]$FailOnSteadyStateBindingFallback)"
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

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Enforce-SpeedProfileRetention -ProfileRoot (Get-SpeedProfileRoot) -RetainedRunCount $RetainedRunCount
}

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
        (
            -not $UseEligiblePrimaryReuseRatio -and
            (
                [double]$_.VulkanCommandBufferForcedDirtyTotal -gt 0.0 -or
                @($_.VulkanCommandBufferDirtySummaries).Count -gt 0
            )
        ) -or
        [double]$(if ($UseEligiblePrimaryReuseRatio) {
            $_.VulkanEligiblePrimaryCommandBufferReuseRatio
        } else {
            $_.VulkanCommandBufferCleanReuseRatio
        }) -lt $MinSteadyStateCommandBufferCleanReuseRatio -or
        [double]$_.VulkanGlobalFallbackInvalidationsTotal -gt 0.0
    })

    if ($commandChurnFailures.Count -gt 0) {
        $details = $commandChurnFailures | ForEach-Object {
            "$($_.Strategy) r$($_.Repetition): records=$($_.VulkanCommandBufferRecordsTotal) reuse=$($_.VulkanCommandBufferCleanReuseTotal) forcedDirty=$($_.VulkanCommandBufferForcedDirtyTotal) rawRatio=$($_.VulkanCommandBufferCleanReuseRatio) eligibleRecords=$($_.VulkanEligiblePrimaryCommandBufferRecordsTotal) ineligibleRecords=$($_.VulkanIneligiblePrimaryCommandBufferRecordsTotal) eligibleRatio=$($_.VulkanEligiblePrimaryCommandBufferReuseRatio) exactVariants=$($_.VulkanExactVariantsDirtiedTotal) exactChains=$($_.VulkanExactCommandChainsDirtiedTotal) unrelatedPreserved=$($_.VulkanUnrelatedVariantsPreservedTotal) globalFallbacks=$($_.VulkanGlobalFallbackInvalidationsTotal) dirty=$(@($_.VulkanCommandBufferDirtySummaries) -join '|')"
        }
        throw "Steady-state Vulkan command-buffer churn detected: $($details -join '; ')"
    }
}

if ($FailOnSteadyStateCommandBufferAllocations) {
    $allocationFailures = @($results | Where-Object {
        $value = $_.VulkanGateRecordCommandBufferAllocatedBytesTotal
        $null -ne $value -and [long]$value -gt $MaxSteadyStateRecordCommandBufferAllocatedBytes
    })

    if ($allocationFailures.Count -gt 0) {
        $details = $allocationFailures | ForEach-Object {
            "$($_.Strategy) r$($_.Repetition): gateBytes=$($_.VulkanGateRecordCommandBufferAllocatedBytesTotal) rawBytes=$($_.VulkanRecordCommandBufferAllocatedBytesTotal) eligibleBytes=$($_.VulkanEligiblePrimaryRecordAllocatedBytesTotal) ineligibleBytes=$($_.VulkanIneligiblePrimaryRecordAllocatedBytesTotal) threshold=$MaxSteadyStateRecordCommandBufferAllocatedBytes"
        }
        throw "Steady-state Vulkan command-buffer allocations exceeded threshold: $($details -join '; ')"
    }
}

if ($FailOnSteadyStateBindingFallback) {
    $bindingFallbackFailures = @($results | Where-Object {
        [double]$_.VulkanAutoUniformFallbackDrawsTotal -gt 0.0
    })

    if ($bindingFallbackFailures.Count -gt 0) {
        $details = $bindingFallbackFailures | ForEach-Object {
            "$($_.Strategy) r$($_.Repetition): fallbackDraws=$($_.VulkanAutoUniformFallbackDrawsTotal) reasons=$($_.VulkanAutoUniformFallbackReasonSummary)"
        }
        throw "Steady-state Vulkan binding fallback detected: $($details -join '; ')"
    }
}

$invalidCaptureFailures = @($results | Where-Object {
    -not $_.StabilityReady -or
    -not $_.CameraPoseVerified -or
    -not $_.MotionCameraPoseVerified -or
    ($hasCameraMotion -and (
        [int]$_.MotionInterval.Samples -lt 1 -or
        [int]$_.MotionInterval.WorkloadIdentityCount -lt 1 -or
        (-not $AllowWorkloadIdentityChanges -and [int]$_.MotionInterval.WorkloadIdentityCount -ne 1) -or
        (-not $AllowWorkloadIdentityChanges -and $_.MotionInterval.WorkloadIdentityHash -ne $_.CaptureWorkloadIdentityHash) -or
        [double]$_.MotionInterval.UnapprovedOutputPolicyEventsTotal -gt 0.0 -or
        [double]$_.MotionInterval.VulkanSubmissionRejectionsTotal -gt 0.0 -or
        [int]$_.MotionInterval.VulkanFailedFrameSamples -gt 0)) -or
    (-not $DisableMcpDiagnostics -and -not $_.AdmissionScreenshotCaptured) -or
    ([int]$_.CaptureWorkloadIdentityCount -lt 1 -or
        (-not $AllowWorkloadIdentityChanges -and [int]$_.CaptureWorkloadIdentityCount -ne 1)) -or
    [double]$_.UnapprovedOutputPolicyEventsTotal -gt 0.0 -or
    [double]$_.VulkanSubmissionRejectionsTotal -gt 0.0 -or
    [int]$_.VulkanFailedFrameSamples -gt 0
})
if ($invalidCaptureFailures.Count -gt 0) {
    $details = $invalidCaptureFailures | ForEach-Object {
        "$($_.Strategy) r$($_.Repetition): stable=$($_.StabilityReady) admissionImage=$($_.AdmissionScreenshotCaptured) stationaryIdentities=$($_.CaptureWorkloadIdentityCount) motionVerified=$($_.MotionCameraPoseVerified) motionSamples=$($_.MotionInterval.Samples) motionIdentities=$($_.MotionInterval.WorkloadIdentityCount) unapprovedPolicy=$($_.UnapprovedOutputPolicyEventsTotal)/$($_.MotionInterval.UnapprovedOutputPolicyEventsTotal) rejectedSubmissions=$($_.VulkanSubmissionRejectionsTotal)/$($_.MotionInterval.VulkanSubmissionRejectionsTotal) failedFrames=$($_.VulkanFailedFrameSamples)/$($_.MotionInterval.VulkanFailedFrameSamples) reason=$($_.StabilityReason)"
    }
    throw "Invalid render-pipeline performance capture: $($details -join '; ')"
}

$meshletProductionFailures = @($results | Where-Object {
    if ($_.Strategy -ne 'GpuMeshletZeroReadback') {
        return $false
    }

    # EffectiveStrategy aggregates all observed pass/bin routes. A valid meshlet
    # frame can therefore include planned traditional GPU routes for unsupported
    # auxiliary or deformation bins. Shipping/DevParity deliberately suppress
    # diagnostics readback, so their proof is the production-frame counter plus
    # the mesh-task operation retained in the Vulkan primary frame plan. The
    # explicit Diagnostics profile additionally requires fence-delayed GPU data.
    $commonFailure =
        $null -eq $_.MeshletRenderPathSourceHashCalls -or
        $null -eq $_.MeshletRenderPathDiskCalls -or
        $null -eq $_.MeshletRenderPathCookerCalls -or
        $null -eq $_.MeshletMappedBytes -or
        $null -eq $_.MeshletDispatchCalls -or
        [double]$_.MeshletRenderPathSourceHashCalls -ne 0.0 -or
        [double]$_.MeshletRenderPathDiskCalls -ne 0.0 -or
        [double]$_.MeshletRenderPathCookerCalls -ne 0.0 -or
        [double]$_.MeshletMappedBytes -ne 0.0 -or
        [double]$_.MeshletDispatchCalls -le 0.0 -or
        [double]$(if ($_.ZeroReadbackValidationScope -eq 'Capture') { $_.GpuReadbackBytesTotal } else { $_.AllGpuReadbackBytesTotal }) -ne 0.0 -or
        [double]$(if ($_.ZeroReadbackValidationScope -eq 'Capture') { $_.GpuMappedBuffersTotal } else { $_.AllGpuMappedBuffersTotal }) -ne 0.0

    if ($commonFailure) {
        return $true
    }

    if ($_.VulkanGpuDrivenProfile -eq 'Diagnostics') {
        return $null -eq $_.MeshletTaskRecordsEmitted -or
            $null -eq $_.MeshletDelayedDispatchGroups -or
            $null -eq $_.MeshletDiagnosticReadbackBytes -or
            [double]$_.MeshletTaskRecordsEmitted -le 0.0 -or
            [double]$_.MeshletDelayedDispatchGroups -le 0.0 -or
            [double]$_.MeshletDiagnosticReadbackBytes -le 0.0
    }

    return $null -eq $_.MeshletProductionFrames -or
        $null -eq $_.VulkanFrameOpMeshTaskDispatchCount -or
        [double]$_.MeshletProductionFrames -le 0.0 -or
        [double]$_.VulkanFrameOpMeshTaskDispatchCount -le 0.0
})
if ($meshletProductionFailures.Count -gt 0) {
    $details = $meshletProductionFailures | ForEach-Object {
        "$($_.Strategy) r$($_.Repetition): profile=$($_.VulkanGpuDrivenProfile) effective=$($_.EffectiveStrategy) lastResolvedPass=$($_.MeshletResolvedPass) lastResolvedRoute=$($_.MeshletResolvedRoute) hash=$($_.MeshletRenderPathSourceHashCalls) disk=$($_.MeshletRenderPathDiskCalls) cooker=$($_.MeshletRenderPathCookerCalls) mapped=$($_.MeshletMappedBytes) apiEnqueues=$($_.MeshletDispatchCalls) productionFrames=$($_.MeshletProductionFrames) frameOpDispatches=$($_.VulkanFrameOpMeshTaskDispatchCount) delayedTaskRecords=$($_.MeshletTaskRecordsEmitted) delayedDispatchX=$($_.MeshletDelayedDispatchGroups) diagnosticReadbackBytes=$($_.MeshletDiagnosticReadbackBytes) readback=$($_.AllGpuReadbackBytesTotal) mappedBuffers=$($_.AllGpuMappedBuffersTotal) failedRung=$($_.MeshletVulkanCapabilityFailedRung)"
    }
    throw "GpuMeshletZeroReadback production gate failed: $($details -join '; ')"
}

$meshletCacheEvidenceFailures = @($results | Where-Object {
    if ($_.Strategy -notin @('GpuMeshletInstrumented', 'GpuMeshletZeroReadback')) { return $false }

    if ($_.CacheMode -eq 'Cold') {
        return $null -eq $_.MeshletSourceParserCalls -or
            $null -eq $_.MeshletColdImportBuilderCalls -or
            $null -eq $_.MeshletCookedPayloadCount -or
            $null -eq $_.MeshletGeneratedLodCount -or
            [double]$_.MeshletSourceParserCalls -le 0.0 -or
            [double]$_.MeshletColdImportBuilderCalls -le 0.0 -or
            [double]$_.MeshletCookedPayloadCount -le 0.0 -or
            [double]$_.MeshletGeneratedLodCount -le 0.0
    }

    return $null -eq $_.MeshletWarmPayloadHydrations -or
        $null -eq $_.MeshletSourceParserCalls -or
        $null -eq $_.MeshletColdImportBuilderCalls -or
        [double]$_.MeshletWarmPayloadHydrations -le 0.0 -or
        [double]$_.MeshletSourceParserCalls -ne 0.0 -or
        [double]$_.MeshletColdImportBuilderCalls -ne 0.0
})
if ($meshletCacheEvidenceFailures.Count -gt 0) {
    $details = $meshletCacheEvidenceFailures | ForEach-Object {
        "$($_.Strategy) r$($_.Repetition) $($_.CacheMode): parser=$($_.MeshletSourceParserCalls) nativeBuilder=$($_.MeshletColdImportBuilderCalls) generatedLods=$($_.MeshletGeneratedLodCount) payloads=$($_.MeshletCookedPayloadCount) warmHydrations=$($_.MeshletWarmPayloadHydrations)"
    }
    throw "Meshlet import-cache evidence gate failed: $($details -join '; '). Cold requires source parsing plus nonzero cook/LOD evidence; warm requires standalone cooked-mesh hydration with zero source parsing and builder calls."
}
