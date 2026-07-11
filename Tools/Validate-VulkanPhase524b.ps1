[CmdletBinding()]
param(
    [Parameter()]
    [string]$RuntimeJson,

    [Parameter()]
    [ValidateRange(0, 10000)]
    [int]$WarmupFrames = 60,

    [Parameter()]
    [ValidateRange(1, 10000)]
    [int]$RetainedFrames = 300,

    [Parameter()]
    [ValidateRange(30, 3600)]
    [int]$TimeoutSeconds = 900,

    [Parameter()]
    [ValidateRange(1.0, 1000.0)]
    [double]$MinimumObservedFramesPerSecond = 30.0,

    [Parameter()]
    [ValidateRange(1.0, 1000.0)]
    [double]$MaximumCpuFrameP95Milliseconds = 33.34,

    [Parameter()]
    [ValidateRange(1, 120)]
    [int]$MaximumOcclusionRecoveryAgeFrames = 8,

    [Parameter()]
    [ValidateSet("Off", "Fixed", "EyeTracked", "RuntimePreferred")]
    [string]$FoveationMode = "Off",

    [Parameter()]
    [string]$RunRoot,

    [Parameter()]
    [switch]$NoBuild,

    [Parameter()]
    [switch]$StartService
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$runner = Join-Path $repoRoot "Tools\OpenXR\Run-OpenXrMonadoSmoke.ps1"
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw "OpenXR Monado smoke runner was not found: $runner"
}

if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $RunRoot = Join-Path $repoRoot "Build\_AgentValidation\$stamp-vulkan-phase524b"
}
elseif (-not [System.IO.Path]::IsPathRooted($RunRoot)) {
    $RunRoot = Join-Path $repoRoot $RunRoot
}
$RunRoot = [System.IO.Path]::GetFullPath($RunRoot)

function Get-JsonArray {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return @()
    }
    if ($Value -is [System.Array]) {
        return @($Value)
    }
    $values = $Value.PSObject.Properties['$values']
    if ($null -ne $values) {
        return @($values.Value)
    }
    return @($Value)
}

function Get-Percentile {
    param(
        [Parameter(Mandatory)][double[]]$Sorted,
        [Parameter(Mandatory)][double]$Percentile
    )

    if ($Sorted.Count -eq 0) {
        return 0.0
    }
    if ($Sorted.Count -eq 1) {
        return $Sorted[0]
    }

    $position = [Math]::Clamp($Percentile, 0.0, 1.0) * ($Sorted.Count - 1)
    $lower = [Math]::Floor($position)
    $upper = [Math]::Ceiling($position)
    if ($lower -eq $upper) {
        return $Sorted[$lower]
    }
    $weight = $position - $lower
    return $Sorted[$lower] + (($Sorted[$upper] - $Sorted[$lower]) * $weight)
}

$environment = [ordered]@{
    XRE_UNIT_TEST_VR_VIEW_RENDER_MODE       = "SinglePassStereo"
    XRE_UNIT_TEST_VR_FOVEATION_MODE        = $FoveationMode
    XRE_UNIT_TEST_RENDER_WINDOWS_WHILE_IN_VR = "1"
    XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS  = "1"
    XRE_OCCLUSION_CULLING_MODE             = "CpuQueryAsync"
    XRE_VK_RENDER_TARGET_MODE              = "DynamicRendering"
    XRE_VULKAN_DIAGNOSTIC_PRESET           = "SyncValidation"
    XRE_VULKAN_VALIDATION                  = "1"
    XRE_VULKAN_SYNC_VALIDATION             = "1"
    XRE_VULKAN_COMMAND_CHAIN_VALIDATE      = "1"
    XRE_VULKAN_FRAMEOP_TRACE               = "1"
    XRE_VULKAN_TARGET_TRACE                = "1"
    XRE_VULKAN_CAPTURE_EYE_OUTPUTS         = "1"
    XRE_FIRST_CHANCE_EXCEPTIONS            = "1"
}

$previousEnvironment = @{}
foreach ($entry in $environment.GetEnumerator()) {
    $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
    [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, "Process")
}

$runnerExitCode = 1
try {
    $runnerArguments = @{
        Renderer = "Vulkan"
        SmokeFrames = $RetainedFrames
        WarmupFrames = $WarmupFrames
        TimeoutSeconds = $TimeoutSeconds
        RunRoot = $RunRoot
        SkipAllocationAudit = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($RuntimeJson)) {
        $runnerArguments.RuntimeJson = $RuntimeJson
    }
    if ($NoBuild) {
        $runnerArguments.NoBuild = $true
    }
    if ($StartService) {
        $runnerArguments.StartService = $true
    }

    & $runner @runnerArguments
    $runnerExitCode = $LASTEXITCODE
}
finally {
    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }
}

$reports = Join-Path $RunRoot "reports"
$logs = Join-Path $RunRoot "logs"
[System.IO.Directory]::CreateDirectory($reports) | Out-Null
[System.IO.Directory]::CreateDirectory($logs) | Out-Null
$summaryPath = Join-Path $reports "openxr-smoke-summary.json"
if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
    throw "Phase 5.2.4b smoke summary was not written: $summaryPath"
}

$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$frameLedger = @(Get-JsonArray $summary.frameLedger)
$occlusionViewLedger = @(Get-JsonArray $summary.occlusionViewLedger)
$outputLedger = @(Get-JsonArray $summary.outputLedger)
$failures = [System.Collections.Generic.List[string]]::new()

if ($runnerExitCode -ne 0) {
    $failures.Add("Monado smoke runner exited with code $runnerExitCode.")
}
if ([int]$summary.schemaVersion -lt 3) {
    $failures.Add("OpenXR smoke schemaVersion=$($summary.schemaVersion); Phase 5.2.4b requires schema 3 or newer.")
}
if ($RetainedFrames -ne 300) {
    $failures.Add("Phase 5.2.4b requires exactly 300 retained frames; requested $RetainedFrames.")
}
if ([string]$summary.viewRenderModeRequested -ne "SinglePassStereo" -or
    [string]$summary.viewRenderModeEffective -ne "SinglePassStereo" -or
    [string]$summary.viewRenderImplementationPath -ne "TrueSinglePassStereo" -or
    -not [bool]$summary.viewRenderModeSupported) {
    $failures.Add("Strict SPS was not effective: requested=$($summary.viewRenderModeRequested) effective=$($summary.viewRenderModeEffective) path=$($summary.viewRenderImplementationPath) supported=$($summary.viewRenderModeSupported).")
}
if ([int]$summary.warmupFrameCount -ne $WarmupFrames) {
    $failures.Add("Warmup frame count was $($summary.warmupFrameCount), expected $WarmupFrames.")
}
if ([int]$summary.retainedFrameCount -ne $RetainedFrames -or $frameLedger.Count -ne $RetainedFrames) {
    $failures.Add("Retained frame ledger contained $($frameLedger.Count) frames, expected exactly $RetainedFrames.")
}
if ([long]$summary.endFrameFailureCount -ne 0) {
    $failures.Add("xrEndFrame failure count was $($summary.endFrameFailureCount).")
}
if ([long]$summary.strictSinglePassStereoSequentialFallbackAttemptCount -ne 0) {
    $failures.Add("Strict SPS attempted sequential fallback $($summary.strictSinglePassStereoSequentialFallbackAttemptCount) time(s).")
}
if (@(Get-JsonArray $summary.failures).Count -ne 0) {
    $failures.Add("OpenXR smoke summary reported failure(s): $(@(Get-JsonArray $summary.failures) -join ' | ')")
}
if ([bool]$summary.occlusionViewLedgerOverflow) {
    $failures.Add("The per-frame keyed occlusion ledger overflowed its bounded capacity.")
}
if ([bool]$summary.outputLedgerOverflow) {
    $failures.Add("The per-frame output ledger overflowed its bounded capacity.")
}
if (-not [bool]$summary.teardownCompleted) {
    $failures.Add("OpenXR teardown did not complete.")
}

$frameTimes = [System.Collections.Generic.List[double]]::new($frameLedger.Count)
$gpuTimes = [System.Collections.Generic.List[double]]::new($frameLedger.Count)
$occlusionTestedTotal = 0L
$occlusionCulledTotal = 0L
$occlusionSubmittedTotal = 0L
$occlusionResolvedTotal = 0L
$maxOcclusionAge = 0
$allocationFrames = 0
$descriptorChurnFrames = 0
$retirementFrames = 0
$planReplacementFrames = 0
$recordFrames = 0
$reuseFrames = 0

for ($i = 0; $i -lt $frameLedger.Count; $i++) {
    $frame = $frameLedger[$i]
    if (-not [bool]$frame.projectionLayerSubmitted) {
        $failures.Add("Retained frame $i did not submit valid projection layers.")
    }
    if ([int]$frame.endFrameResult -ne 0 -or [uint32]$frame.endFrameLayerCount -ne 1) {
        $failures.Add("Retained frame $i did not complete xrEndFrame with one projection layer: result=$($frame.endFrameResult) layers=$($frame.endFrameLayerCount).")
    }
    if ([long]$frame.leftAcquireDelta -ne 1 -or [long]$frame.rightAcquireDelta -ne 1 -or
        [long]$frame.leftWaitDelta -ne 1 -or [long]$frame.rightWaitDelta -ne 1 -or
        [long]$frame.leftPublishDelta -ne 1 -or [long]$frame.rightPublishDelta -ne 1 -or
        [long]$frame.leftReleaseDelta -ne 1 -or [long]$frame.rightReleaseDelta -ne 1) {
        $failures.Add("Retained frame $i did not complete exactly one acquire/wait/publish/release per eye.")
    }
    if ([int]$frame.leftExternalImageSlot -lt 0 -or [int]$frame.rightExternalImageSlot -lt 0) {
        $failures.Add("Retained frame $i did not record both acquired external image slots.")
    }
    if ([long]$frame.strictSequentialFallbackAttemptCount -ne 0 -or [long]$frame.strictSequentialFallbackAttemptDelta -ne 0) {
        $failures.Add("Retained frame $i recorded a strict-SPS sequential fallback attempt.")
    }
    if ([int]$frame.submissionRejectionCount -ne 0 -or [int]$frame.globalInFlightWaitCount -ne 0 -or
        [int]$frame.forceFlushCount -ne 0 -or [int]$frame.unapprovedPolicyEventCount -ne 0) {
        $failures.Add("Retained frame $i violated submit/wait/flush policy: rejected=$($frame.submissionRejectionCount) waits=$($frame.globalInFlightWaitCount) flushes=$($frame.forceFlushCount) policy=$($frame.unapprovedPolicyEventCount).")
    }
    if ([int]$frame.queueSubmitCount -le 0) {
        $failures.Add("Retained frame $i recorded no Vulkan queue submit.")
    }
    if (-not [bool]$frame.vrActive -or [string]$frame.mirrorMode -ne "FullIndependentRender" -or
        [uint64]$frame.outputManifestFrameId -eq 0 -or [int]$frame.outputRequestCount -le 0) {
        $failures.Add("Retained frame $i has an invalid output manifest or mirror mode.")
    }
    if ([int]$frame.validationErrorCount -ne 0) {
        $failures.Add("Retained frame $i recorded $($frame.validationErrorCount) Vulkan validation errors.")
    }
    if ([int]$frame.globalFallbackInvalidationCount -ne 0) {
        $failures.Add("Retained frame $i used $($frame.globalFallbackInvalidationCount) global invalidation fallbacks.")
    }
    if ([int]$frame.resourcePlanReplacementCount -ne 0) {
        $planReplacementFrames++
    }
    if ([int]$frame.deviceLocalAllocationCount -ne 0 -or [int]$frame.uploadAllocationCount -ne 0) {
        $allocationFrames++
    }
    if ([int]$frame.descriptorPoolCreateCount -ne 0 -or
        [int]$frame.descriptorPoolDestroyCount -ne 0 -or
        [int]$frame.descriptorPoolResetCount -ne 0) {
        $descriptorChurnFrames++
    }
    if ([int]$frame.retiredResourceCount -ne 0) {
        $retirementFrames++
    }
    if ([int]$frame.commandBufferRecordCount -gt 0 -or [int]$frame.primaryCommandBufferRecordCount -gt 0) {
        $recordFrames++
    }
    if ([int]$frame.commandBufferCleanReuseCount -gt 0 -or [int]$frame.primaryCommandBufferReuseCount -gt 0) {
        $reuseFrames++
    }
    if ([int]$frame.sceneSwapchainWriterCount -le 0 -and [int]$frame.swapchainWriteCount -le 0) {
        $failures.Add("Retained frame $i recorded no desktop swapchain writer.")
    }
    if ([int]$frame.missingSceneSwapchainWriteCount -ne 0) {
        $failures.Add("Retained frame $i recorded a missing desktop scene swapchain write.")
    }

    $frameTimes.Add([double]$frame.frameTotalMilliseconds)
    $gpuTimes.Add([double]$frame.frameGpuMilliseconds)
    $occlusionTestedTotal += [long]$frame.cpuOcclusionTested
    $occlusionCulledTotal += [long]$frame.cpuOcclusionCulled
    $occlusionSubmittedTotal += [long]$frame.cpuOcclusionQueriesSubmitted
    $occlusionResolvedTotal += [long]$frame.cpuOcclusionQueriesResolved
    $maxOcclusionAge = [Math]::Max($maxOcclusionAge, [int]$frame.cpuOcclusionMaxQueryAge)
}

for ($i = 1; $i -lt $frameLedger.Count; $i++) {
    if ([long]$frameLedger[$i].completedFrameCount -ne [long]$frameLedger[$i - 1].completedFrameCount + 1 -or
        [long]$frameLedger[$i].submittedFrameCount -ne [long]$frameLedger[$i - 1].submittedFrameCount + 1 -or
        [long]$frameLedger[$i].noLayerFrameCount -ne [long]$frameLedger[$i - 1].noLayerFrameCount -or
        [uint64]$frameLedger[$i].renderFrameId -le [uint64]$frameLedger[$i - 1].renderFrameId -or
        [double]$frameLedger[$i].elapsedMilliseconds -le [double]$frameLedger[$i - 1].elapsedMilliseconds) {
        $failures.Add("Retained frame ledger is not a contiguous, monotonically increasing submitted-frame cohort at index $i.")
    }
}

$invalidOutputs = @($outputLedger | Where-Object {
    [int]$_.retainedIndex -lt 0 -or [int]$_.retainedIndex -ge $RetainedFrames -or
    [uint64]$_.outputId -eq 0 -or [string]::IsNullOrWhiteSpace([string]$_.pipelineName) -or
    [uint32]$_.displayWidth -eq 0 -or [uint32]$_.displayHeight -eq 0 -or
    [uint32]$_.internalWidth -eq 0 -or [uint32]$_.internalHeight -eq 0 -or
    [uint32]$_.layerCount -eq 0
})
if ($invalidOutputs.Count -gt 0) {
    $failures.Add("$($invalidOutputs.Count) output-ledger entries have invalid identity, pipeline, extent, or layer metadata.")
}
for ($i = 0; $i -lt $RetainedFrames; $i++) {
    $frameOutputs = @($outputLedger | Where-Object { [int]$_.retainedIndex -eq $i })
    $desktopOutputs = @($frameOutputs | Where-Object { [string]$_.targetClass -eq "DesktopSwapchain" -and [bool]$_.rendered })
    $strictSpsOutputs = @($frameOutputs | Where-Object {
        [string]$_.targetClass -eq "RuntimeExternalImage" -and [uint32]$_.viewMask -eq 3 -and
        [uint32]$_.layerCount -eq 2 -and [int]$_.externalImageSlot -ge 0 -and [bool]$_.rendered
    })
    if ($desktopOutputs.Count -eq 0 -or $strictSpsOutputs.Count -eq 0) {
        $failures.Add("Retained frame $i lacks a rendered desktop output or true-multiview external output in the output ledger.")
    }
}

if ($planReplacementFrames -ne 0) {
    $failures.Add("Resource-plan replacement occurred in $planReplacementFrames retained frames.")
}
if ($allocationFrames -ne 0) {
    $failures.Add("Vulkan device-local/upload allocation occurred in $allocationFrames retained frames.")
}
if ($descriptorChurnFrames -ne 0) {
    $failures.Add("Descriptor-pool create/destroy/reset churn occurred in $descriptorChurnFrames retained frames.")
}
if ($retirementFrames -ne 0) {
    $failures.Add("Vulkan resource retirement occurred in $retirementFrames retained frames.")
}
if ($occlusionTestedTotal -le 0 -or $occlusionSubmittedTotal -le 0 -or $occlusionResolvedTotal -le 0) {
    $failures.Add("CpuQueryAsync did not perform valid work: tested=$occlusionTestedTotal submitted=$occlusionSubmittedTotal resolved=$occlusionResolvedTotal.")
}
if ($occlusionCulledTotal -le 0) {
    $failures.Add("CpuQueryAsync produced no occlusion culls in the retained window.")
}

$desktopScopes = @("MonoDesktop", "EditorDesktopWhileVr", "MirrorOnly")
$vrScopes = @("VrSinglePassStereo", "VrFoveatedView", "VrStereoPair", "VrLeftEye", "VrRightEye")
$desktopOcclusion = @($occlusionViewLedger | Where-Object { $desktopScopes -contains [string]$_.scope })
$vrOcclusion = @($occlusionViewLedger | Where-Object { $vrScopes -contains [string]$_.scope })
$desktopPovIds = @($desktopOcclusion | ForEach-Object { [int]$_.povId } | Sort-Object -Unique)
$vrPovIds = @($vrOcclusion | ForEach-Object { [int]$_.povId } | Sort-Object -Unique)
$desktopViewSubmissions = ($desktopOcclusion | Measure-Object -Property submissions -Sum).Sum
$desktopViewResolutions = ($desktopOcclusion | Measure-Object -Property resolutions -Sum).Sum
$desktopViewCulls = ($desktopOcclusion | Measure-Object -Property skips -Sum).Sum
$vrViewSubmissions = ($vrOcclusion | Measure-Object -Property submissions -Sum).Sum
$vrViewResolutions = ($vrOcclusion | Measure-Object -Property resolutions -Sum).Sum
$vrViewCulls = ($vrOcclusion | Measure-Object -Property skips -Sum).Sum
if ($desktopOcclusion.Count -eq 0 -or $desktopViewSubmissions -le 0 -or $desktopViewResolutions -le 0 -or $desktopViewCulls -le 0) {
    $failures.Add("Desktop POV occlusion was not independently active: keys=$($desktopOcclusion.Count) submissions=$desktopViewSubmissions resolutions=$desktopViewResolutions culls=$desktopViewCulls.")
}
if ($vrOcclusion.Count -eq 0 -or $vrViewSubmissions -le 0 -or $vrViewResolutions -le 0 -or $vrViewCulls -le 0) {
    $failures.Add("VR POV occlusion was not independently active: keys=$($vrOcclusion.Count) submissions=$vrViewSubmissions resolutions=$vrViewResolutions culls=$vrViewCulls.")
}
foreach ($desktopPovId in $desktopPovIds) {
    if ($vrPovIds -contains $desktopPovId) {
        $failures.Add("Desktop and VR occlusion unexpectedly shared POV identity $desktopPovId.")
    }
}
$expectedVrCoverageMask = if ([int]$summary.locatedViewCount -ge 4) { [uint32]0xF } else { [uint32]0x3 }
$invalidVrCoverage = @($vrOcclusion | Where-Object {
    ([uint32]$_.requiredCoverageMask -band $expectedVrCoverageMask) -ne $expectedVrCoverageMask
})
if ($invalidVrCoverage.Count -gt 0) {
    $failures.Add("$($invalidVrCoverage.Count) VR occlusion ledger entries did not cover the required POV mask 0x$($expectedVrCoverageMask.ToString('X')).")
}
$staleOcclusion = @($occlusionViewLedger | Where-Object {
    [int]$_.currentResultAgeFrames -gt $MaximumOcclusionRecoveryAgeFrames -or
    [int]$_.maxResultAgeFrames -gt $MaximumOcclusionRecoveryAgeFrames -or
    [int]$_.recoveryLatencyFrames -gt $MaximumOcclusionRecoveryAgeFrames
})
if ($staleOcclusion.Count -gt 0) {
    $failures.Add("$($staleOcclusion.Count) occlusion entries exceeded the bounded recovery age of $MaximumOcclusionRecoveryAgeFrames frames.")
}

$engineLogDirectory = [string]$summary.logDirectory
$copiedLogFiles = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($engineLogDirectory) -and (Test-Path -LiteralPath $engineLogDirectory -PathType Container)) {
    foreach ($name in @("log_vulkan.log", "log_rendering.log", "log_general.log", "editor_bootstrap.log")) {
        $source = Join-Path $engineLogDirectory $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            $destination = Join-Path $logs $name
            Copy-Item -LiteralPath $source -Destination $destination -Force
            $copiedLogFiles.Add($destination)
        }
    }
}

$forbiddenLogPatterns = [ordered]@{
    "VUID-" = "Vulkan VUID validation error"
    "SYNC-HAZARD" = "Vulkan synchronization hazard"
    "UNASSIGNED" = "unassigned Vulkan validation error"
    "ErrorValidationFailed" = "engine submission validation rejection"
    "Rejected queue submission" = "rejected Vulkan queue submission"
    "SequentialFallback" = "strict-SPS sequential fallback"
    "falling back to sequential" = "strict-SPS sequential fallback"
    "reason=ResourcePlanReplacement" = "blocking resource-plan replacement wait"
    "device lost" = "device loss"
}

$allLogText = ""
foreach ($path in $copiedLogFiles) {
    $allLogText += [Environment]::NewLine + (Get-Content -LiteralPath $path -Raw)
}
foreach ($pattern in $forbiddenLogPatterns.GetEnumerator()) {
    if ($allLogText.IndexOf($pattern.Key, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $failures.Add("Logs contain $($pattern.Value) ('$($pattern.Key)').")
    }
}
if ($allLogText -notmatch '\[VulkanDiag\].*Preset=SyncValidation.*ValidationLayers=True') {
    $failures.Add("Effective Vulkan SyncValidation preset/layer activation was not recorded.")
}
if ($allLogText -notmatch 'ValidationFeatures=.*SynchronizationValidation') {
    $failures.Add("Synchronization validation was not effectively enabled.")
}

$sortedFrameTimes = @($frameTimes | Sort-Object)
$sortedGpuTimes = @($gpuTimes | Sort-Object)
$elapsedRetainedSeconds = if ($frameLedger.Count -gt 1) {
    ([double]$frameLedger[-1].elapsedMilliseconds - [double]$frameLedger[0].elapsedMilliseconds) / 1000.0
}
else {
    0.0
}
$throughput = if ($elapsedRetainedSeconds -gt 0.0) { ($frameLedger.Count - 1) / $elapsedRetainedSeconds } else { 0.0 }
$cpuFrameP95 = Get-Percentile -Sorted $sortedFrameTimes -Percentile 0.95
if ($throughput -lt $MinimumObservedFramesPerSecond) {
    $failures.Add("Observed retained throughput was $($throughput.ToString('F2')) frames/s, below the Phase 5.2.4b floor of $($MinimumObservedFramesPerSecond.ToString('F2')) frames/s.")
}
if ($cpuFrameP95 -gt $MaximumCpuFrameP95Milliseconds) {
    $failures.Add("Retained CPU frame p95 was $($cpuFrameP95.ToString('F2')) ms, above the Phase 5.2.4b ceiling of $($MaximumCpuFrameP95Milliseconds.ToString('F2')) ms.")
}

$result = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTimeOffset]::UtcNow
    passed = $failures.Count -eq 0
    runRoot = $RunRoot
    engineLogDirectory = $engineLogDirectory
    configuration = [ordered]@{
        renderer = "Vulkan"
        renderTargetMode = "DynamicRendering"
        viewRenderMode = "SinglePassStereo"
        implementationPath = [string]$summary.viewRenderImplementationPath
        mirrorMode = "FullIndependentRender"
        foveationMode = $FoveationMode
        antiAliasing = "Tsr"
        occlusion = "CpuQueryAsync"
        diagnosticPreset = "SyncValidation"
        warmupFrames = $WarmupFrames
        retainedFrames = $RetainedFrames
        maximumOcclusionRecoveryAgeFrames = $MaximumOcclusionRecoveryAgeFrames
        externallyOwnedValidationAllowlist = @()
    }
    performance = [ordered]@{
        retainedDurationSeconds = $elapsedRetainedSeconds
        observedFramesPerSecond = $throughput
        minimumObservedFramesPerSecond = $MinimumObservedFramesPerSecond
        cpuFrameP50Milliseconds = Get-Percentile -Sorted $sortedFrameTimes -Percentile 0.50
        cpuFrameP95Milliseconds = $cpuFrameP95
        maximumCpuFrameP95Milliseconds = $MaximumCpuFrameP95Milliseconds
        cpuFrameP99Milliseconds = Get-Percentile -Sorted $sortedFrameTimes -Percentile 0.99
        cpuFrameWorstMilliseconds = if ($sortedFrameTimes.Count -gt 0) { $sortedFrameTimes[-1] } else { 0.0 }
        gpuFrameP50Milliseconds = Get-Percentile -Sorted $sortedGpuTimes -Percentile 0.50
        gpuFrameP95Milliseconds = Get-Percentile -Sorted $sortedGpuTimes -Percentile 0.95
        recordFrames = $recordFrames
        reuseFrames = $reuseFrames
    }
    occlusion = [ordered]@{
        tested = $occlusionTestedTotal
        culled = $occlusionCulledTotal
        submitted = $occlusionSubmittedTotal
        resolved = $occlusionResolvedTotal
        maxResultAgeFrames = $maxOcclusionAge
        desktopPovIds = $desktopPovIds
        desktopSubmissions = $desktopViewSubmissions
        desktopResolutions = $desktopViewResolutions
        desktopCulls = $desktopViewCulls
        vrPovIds = $vrPovIds
        vrRequiredCoverageMask = "0x$($expectedVrCoverageMask.ToString('X'))"
        vrSubmissions = $vrViewSubmissions
        vrResolutions = $vrViewResolutions
        vrCulls = $vrViewCulls
    }
    churn = [ordered]@{
        allocationFrames = $allocationFrames
        descriptorChurnFrames = $descriptorChurnFrames
        retirementFrames = $retirementFrames
        resourcePlanReplacementFrames = $planReplacementFrames
    }
    smokeSummary = $summaryPath
    copiedLogs = @($copiedLogFiles)
    failures = @($failures)
}

$resultPath = Join-Path $reports "vulkan-phase524b-validation.json"
$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resultPath -Encoding UTF8

if ($failures.Count -gt 0) {
    Write-Host "Vulkan Phase 5.2.4b validation failed. Report=$resultPath"
    foreach ($failure in $failures) {
        Write-Host " - $failure"
    }
    exit 1
}

Write-Host "Vulkan Phase 5.2.4b validation passed. Report=$resultPath"
exit 0
