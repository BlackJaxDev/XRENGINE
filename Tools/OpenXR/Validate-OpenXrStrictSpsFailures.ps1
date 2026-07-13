[CmdletBinding()]
param(
    [Parameter()]
    [string]$RuntimeJson,

    [Parameter()]
    [ValidateRange(30, 600)]
    [int]$TimeoutSeconds = 120,

    [Parameter()]
    [string]$RunRoot,

    [Parameter()]
    [switch]$NoBuild,

    [Parameter()]
    [switch]$StartService
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$LASTEXITCODE = 0

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$runner = Join-Path $PSScriptRoot "Run-OpenXrMonadoSmoke.ps1"
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw "OpenXR smoke runner was not found: $runner"
}

if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path $repoRoot "Build\_AgentValidation\$(Get-Date -Format yyyyMMdd-HHmmss)-strict-sps-failures"
}
elseif (-not [System.IO.Path]::IsPathRooted($RunRoot)) {
    $RunRoot = Join-Path $repoRoot $RunRoot
}
$RunRoot = [System.IO.Path]::GetFullPath($RunRoot)
$reports = Join-Path $RunRoot "reports"
[System.IO.Directory]::CreateDirectory($reports) | Out-Null

$stages = @("Capability", "Target", "Recording", "LifetimeValidation", "Submit", "Publish")
$warmupFrames = 4
$retainedFrames = 8
$totalFrames = $warmupFrames + $retainedFrames
$environment = [ordered]@{
    XRE_UNIT_TEST_VR_VIEW_RENDER_MODE = "SinglePassStereo"
    XRE_UNIT_TEST_RENDER_WINDOWS_WHILE_IN_VR = "1"
    XRE_VK_RENDER_TARGET_MODE = "DynamicRendering"
    XRE_VULKAN_DIAGNOSTIC_PRESET = "SyncValidation"
    XRE_VULKAN_VALIDATION = "1"
    XRE_VULKAN_SYNC_VALIDATION = "1"
    XRE_VULKAN_COMMAND_CHAIN_VALIDATE = "1"
    XRE_OPENXR_VULKAN_SERIAL_EYE_SUBMIT = "0"
    XRE_VULKAN_PHASE524B_VALIDATION = "0"
    XRE_VULKAN_PHASE524B_INJECT_DESKTOP_REJECTION = "0"
    XRE_CAPTURE_DEFAULT_PIPELINE_FBO = "0"
}
$previousEnvironment = @{}
$entries = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[string]]::new()

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

foreach ($entry in $environment.GetEnumerator()) {
    $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
    [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, "Process")
}
$previousInjection = [Environment]::GetEnvironmentVariable("XRE_OPENXR_STRICT_SPS_FAILURE_STAGE", "Process")

try {
    for ($stageIndex = 0; $stageIndex -lt $stages.Count; $stageIndex++) {
        $stage = $stages[$stageIndex]
        [Environment]::SetEnvironmentVariable("XRE_OPENXR_STRICT_SPS_FAILURE_STAGE", $stage, "Process")
        $stageRoot = Join-Path $RunRoot $stage.ToLowerInvariant()
        $runnerArguments = @{
            Renderer = "Vulkan"
            SmokeFrames = $retainedFrames
            WarmupFrames = $warmupFrames
            TimeoutSeconds = $TimeoutSeconds
            RunRoot = $stageRoot
            SkipAllocationAudit = $true
        }
        if (-not [string]::IsNullOrWhiteSpace($RuntimeJson)) {
            $runnerArguments.RuntimeJson = $RuntimeJson
        }
        if ($NoBuild -or $stageIndex -gt 0) {
            $runnerArguments.NoBuild = $true
        }
        if ($StartService) {
            $runnerArguments.StartService = $true
        }

        $runnerExitCode = 1
        try {
            $LASTEXITCODE = 0
            & $runner @runnerArguments
            $runnerExitCode = [int]$LASTEXITCODE
        }
        catch {
            $failures.Add("Stage $stage runner invocation failed: $($_.Exception.Message)")
        }
        $summaryPath = Join-Path $stageRoot "reports\openxr-smoke-summary.json"
        if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
            $failures.Add("Stage $stage did not write a smoke summary (runnerExitCode=$runnerExitCode).")
            continue
        }

        $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
        $frameLedger = @(Get-JsonArray $summary.frameLedger)
        $summaryFailures = @(Get-JsonArray $summary.failures)
        $injectedCompletedFrameCount = [long]$summary.strictSpsInjectedCompletedFrameCount
        $injectedFrames = @($frameLedger | Where-Object {
            [long]$_.completedFrameCount -eq $injectedCompletedFrameCount
        })
        $retainedNoLayerFrames = @($frameLedger | Where-Object {
            -not [bool]$_.projectionLayerSubmitted
        })
        $completed = [long]$summary.submittedFrameCount + [long]$summary.noLayerFrameCount
        $stageFailures = [System.Collections.Generic.List[string]]::new()
        if ($runnerExitCode -ne 0) { $stageFailures.Add("runnerExitCode=$runnerExitCode") }
        if ($completed -ne $totalFrames) { $stageFailures.Add("completed=$completed expected=$totalFrames") }
        if ([long]$summary.submittedFrameCount -lt $warmupFrames) { $stageFailures.Add("submitted=$($summary.submittedFrameCount) requiredWarmup=$warmupFrames") }
        if ([long]$summary.strictSpsSuccessfulSubmissionCount -lt $warmupFrames) { $stageFailures.Add("successfulSpsSubmissions=$($summary.strictSpsSuccessfulSubmissionCount) requiredWarmup=$warmupFrames") }
        if ([int]$summary.strictSpsInjectedFailureCount -ne 1) { $stageFailures.Add("injectedCount=$($summary.strictSpsInjectedFailureCount)") }
        if ([string]$summary.strictSpsInjectedFailureStage -cne $stage) { $stageFailures.Add("injectedStage=$($summary.strictSpsInjectedFailureStage)") }
        if (-not [bool]$summary.strictSpsInjectedFailureHandled) { $stageFailures.Add("handled=false") }
        if ([uint32]$summary.strictSpsInjectedProjectionLayerCount -ne 0) { $stageFailures.Add("injectedLayers=$($summary.strictSpsInjectedProjectionLayerCount)") }
        if ([bool]$summary.strictSpsInjectedSequentialFallbackRequested) { $stageFailures.Add("sequentialFallbackRequested=true") }
        if ([long]$summary.strictSpsInjectedSequentialFallbackAttemptDelta -ne 0) { $stageFailures.Add("injectedFallbackDelta=$($summary.strictSpsInjectedSequentialFallbackAttemptDelta)") }
        if ([long]$summary.strictSinglePassStereoSequentialFallbackAttemptCount -ne 0) { $stageFailures.Add("globalFallbackCount=$($summary.strictSinglePassStereoSequentialFallbackAttemptCount)") }
        if ([int]$summary.endFrameFailureCount -ne 0) { $stageFailures.Add("endFrameFailures=$($summary.endFrameFailureCount)") }
        if ($summaryFailures.Count -ne 0) { $stageFailures.Add("summaryFailures=$($summaryFailures.Count)") }
        if (-not [bool]$summary.viewRenderModeResolutionObserved) { $stageFailures.Add("view render mode resolution was not observed") }
        if ([string]$summary.viewRenderModeRequested -cne "SinglePassStereo") { $stageFailures.Add("requestedMode=$($summary.viewRenderModeRequested)") }
        if ([string]$summary.viewRenderModeEffective -cne "SinglePassStereo") { $stageFailures.Add("effectiveMode=$($summary.viewRenderModeEffective)") }
        if ([string]$summary.viewRenderImplementationPath -cne "TrueSinglePassStereo") { $stageFailures.Add("implementationPath=$($summary.viewRenderImplementationPath)") }
        if (-not [bool]$summary.viewRenderModeSupported) { $stageFailures.Add("viewRenderModeSupported=false") }
        if ([string]$summary.vulkanRenderTargetModeEffective -cne "DynamicRendering") { $stageFailures.Add("renderTargetMode=$($summary.vulkanRenderTargetModeEffective)") }
        if (-not [bool]$summary.vulkanValidationLayersEffective) { $stageFailures.Add("validationLayersEffective=false") }
        if (-not [bool]$summary.vulkanSynchronizationValidationEffective) { $stageFailures.Add("synchronizationValidationEffective=false") }
        if ($frameLedger.Count -ne $retainedFrames) { $stageFailures.Add("retainedLedgerCount=$($frameLedger.Count) expected=$retainedFrames") }
        if ($retainedNoLayerFrames.Count -ne 1) { $stageFailures.Add("retainedZeroLayerFrameCount=$($retainedNoLayerFrames.Count) expected=1") }
        if ($injectedFrames.Count -ne 1) {
            $stageFailures.Add("injectedFrameLedgerCount=$($injectedFrames.Count) expected=1 completedFrame=$injectedCompletedFrameCount")
        }
        else {
            $injectedFrame = $injectedFrames[0]
            if ([bool]$injectedFrame.projectionLayerSubmitted -or
                [int]$injectedFrame.endFrameResult -ne 0 -or
                [uint32]$injectedFrame.endFrameLayerCount -ne 0) {
                $stageFailures.Add("injected retained frame was not one successful zero-layer end-frame")
            }
            if ([long]$injectedFrame.strictSequentialFallbackAttemptDelta -ne 0) { $stageFailures.Add("injectedFrameFallbackDelta=$($injectedFrame.strictSequentialFallbackAttemptDelta)") }
            if ([int]$injectedFrame.validationErrorCount -ne 0) { $stageFailures.Add("injectedFrameValidationErrors=$($injectedFrame.validationErrorCount)") }
            if (-not [bool]$injectedFrame.validationLayersEnabled) { $stageFailures.Add("injectedFrameValidationLayers=false") }
            if (-not [bool]$injectedFrame.synchronizationValidationEnabled) { $stageFailures.Add("injectedFrameSyncValidation=false") }
            if (-not [bool]$injectedFrame.lifetimeValidationPassed) { $stageFailures.Add("injectedFrameLifetimeValidationPassed=false") }

            $expectedAcquireWaitReleaseDelta = if ($stage -in @("Capability", "Target")) { 0L } else { 1L }
            foreach ($eye in @("Left", "Right")) {
                $acquireProperty = "${eye}AcquireDelta"
                $waitProperty = "${eye}WaitDelta"
                $releaseProperty = "${eye}ReleaseDelta"
                $publishProperty = "${eye}PublishDelta"
                $acquireDelta = [long]$injectedFrame.$acquireProperty
                $waitDelta = [long]$injectedFrame.$waitProperty
                $releaseDelta = [long]$injectedFrame.$releaseProperty
                $publishDelta = [long]$injectedFrame.$publishProperty
                if ($acquireDelta -ne $expectedAcquireWaitReleaseDelta) { $stageFailures.Add("${eye}AcquireDelta=$acquireDelta expected=$expectedAcquireWaitReleaseDelta") }
                if ($waitDelta -ne $expectedAcquireWaitReleaseDelta) { $stageFailures.Add("${eye}WaitDelta=$waitDelta expected=$expectedAcquireWaitReleaseDelta") }
                if ($releaseDelta -ne $expectedAcquireWaitReleaseDelta) { $stageFailures.Add("${eye}ReleaseDelta=$releaseDelta expected=$expectedAcquireWaitReleaseDelta") }
                if ($publishDelta -ne 0L) { $stageFailures.Add("${eye}PublishDelta=$publishDelta expected=0") }
            }
        }

        $expectedQueueDisposition = if ($stage -eq "Publish") { "Completed" } else { "NotSubmitted" }
        if ([string]$summary.strictSpsInjectedQueueDisposition -cne $expectedQueueDisposition) {
            $stageFailures.Add("queueDisposition=$($summary.strictSpsInjectedQueueDisposition) expected=$expectedQueueDisposition")
        }

        $forbiddenLogMatches = @(
            Get-ChildItem -LiteralPath (Join-Path $stageRoot "logs") -Recurse -File -ErrorAction SilentlyContinue |
                Select-String -Pattern "Strict SinglePassStereo sequential fallback attempt blocked|retrying .* sequentially|VulkanBatch\.SequentialFallback" -CaseSensitive:$false
        )
        if ($forbiddenLogMatches.Count -ne 0) { $stageFailures.Add("forbiddenSequentialFallbackLogMatches=$($forbiddenLogMatches.Count)") }

        $validationLogMatches = @(
            Get-ChildItem -LiteralPath (Join-Path $stageRoot "logs") -Recurse -File -Filter "log_vulkan.log" -ErrorAction SilentlyContinue |
                Select-String -Pattern "VUID-|SYNC-HAZARD|UNASSIGNED" -CaseSensitive:$false |
                Where-Object { $_.Line.IndexOf("VUID-vkDestroyDevice-device-05137", [StringComparison]::Ordinal) -lt 0 }
        )
        if ($validationLogMatches.Count -ne 0) { $stageFailures.Add("engineValidationLogMatches=$($validationLogMatches.Count)") }

        $passed = $stageFailures.Count -eq 0
        if (-not $passed) {
            $failures.Add("Stage $stage failed: $($stageFailures -join ', ').")
        }
        $entries.Add([pscustomobject]@{
            stage = $stage
            passed = $passed
            runnerExitCode = $runnerExitCode
            completedFrameCount = $completed
            warmupFrameCount = $warmupFrames
            retainedFrameCount = $retainedFrames
            submittedFrameCount = [long]$summary.submittedFrameCount
            noLayerFrameCount = [long]$summary.noLayerFrameCount
            retainedZeroLayerFrameCount = $retainedNoLayerFrames.Count
            injectedCompletedFrameCount = $injectedCompletedFrameCount
            successfulSpsSubmissionCount = [long]$summary.strictSpsSuccessfulSubmissionCount
            handled = [bool]$summary.strictSpsInjectedFailureHandled
            projectionLayerCount = [uint32]$summary.strictSpsInjectedProjectionLayerCount
            sequentialFallbackRequested = [bool]$summary.strictSpsInjectedSequentialFallbackRequested
            sequentialFallbackAttemptDelta = [long]$summary.strictSpsInjectedSequentialFallbackAttemptDelta
            globalSequentialFallbackAttemptCount = [long]$summary.strictSinglePassStereoSequentialFallbackAttemptCount
            actualEndFrameResult = if ($injectedFrames.Count -eq 1) { [int]$injectedFrames[0].endFrameResult } else { $null }
            actualEndFrameLayerCount = if ($injectedFrames.Count -eq 1) { [uint32]$injectedFrames[0].endFrameLayerCount } else { $null }
            queueDisposition = [string]$summary.strictSpsInjectedQueueDisposition
            viewRenderModeRequested = [string]$summary.viewRenderModeRequested
            viewRenderModeEffective = [string]$summary.viewRenderModeEffective
            viewRenderImplementationPath = [string]$summary.viewRenderImplementationPath
            forbiddenSequentialFallbackLogMatchCount = $forbiddenLogMatches.Count
            engineValidationLogMatchCount = $validationLogMatches.Count
            summaryPath = $summaryPath
            failures = @($stageFailures)
        })
    }
}
finally {
    [Environment]::SetEnvironmentVariable("XRE_OPENXR_STRICT_SPS_FAILURE_STAGE", $previousInjection, "Process")
    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }
}

$report = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTimeOffset]::UtcNow
    passed = $failures.Count -eq 0 -and $entries.Count -eq $stages.Count
    expectedStages = $stages
    entries = @($entries)
    failures = @($failures)
}
$reportPath = Join-Path $reports "openxr-strict-sps-failure-matrix.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8

if (-not $report.passed) {
    Write-Host "Strict SPS failure-matrix validation failed. Report=$reportPath"
    exit 1
}

Write-Host "Strict SPS failure-matrix validation passed. Report=$reportPath"
exit 0
