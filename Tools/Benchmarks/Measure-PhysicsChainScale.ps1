[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Session,
    [ValidateRange(1, 10000)][int]$ChainCount = 2000,
    [ValidateRange(5, 60)][int]$WindowSeconds = 30,
    [ValidateRange(0, 30)][int]$CpuTraceSeconds = 0,
    [ValidateSet('CpuDirect', 'GpuIndirectZeroReadback')]
    [string]$SubmissionStrategy = 'GpuIndirectZeroReadback',
    [ValidateSet('Authored', 'Strict', 'Hz30', 'Hz15', 'Hz7_5', 'Automatic')]
    [string]$QualityTier = 'Authored',
    [ValidateSet('Authored', 'Discrete', 'Interpolate', 'Extrapolate')]
    [string]$InterpolationMode = 'Authored',
    [string]$Label = 'physics-chain-scale',
    [string]$OutputFolder = '',
    [switch]$Telemetry,
    [switch]$CaptureEvidence,
    [switch]$RequireDirectionalShadows,
    [switch]$UseExistingSession
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$validationRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'Build\_AgentValidation'))
$sessionTool = Join-Path $repoRoot 'Tools\Manage-McpEditorSession.ps1'
$sessionStartedHere = $false
$benchmarkStarted = $false
$controllerId = $null
$summaryPath = $null
$traceProcess = $null
$traceCommand = $null
$lastToolName = $null
$lastMethodName = $null
$profilerSettingsToRestore = [ordered]@{}
$submissionOverrideToRestore = $null
$submissionOverrideChanged = $false
$sourceNodeId = $null
$sourceChainId = $null
$sourceProfileToRestore = $null
$sourceProfileChanged = $false
$summary = [ordered]@{
    label = $Label
    session = $Session
    chainCount = $ChainCount
    windowSeconds = $WindowSeconds
    telemetryEnabled = [bool]$Telemetry
    cpuTraceSeconds = $CpuTraceSeconds
    requireDirectionalShadows = [bool]$RequireDirectionalShadows
    submissionStrategy = $SubmissionStrategy
    requestedSourceProfile = [ordered]@{ qualityTier = $QualityTier; interpolationMode = $InterpolationMode }
    accepted = $false
    reason = $null
}

function Invoke-ScaleTool([string]$Name, [hashtable]$Arguments = @{}, [int]$TimeoutSeconds = 60) {
    $script:lastToolName = $Name
    $script:lastMethodName = if ($Arguments.ContainsKey('method_name')) { $Arguments.method_name } else { $null }
    $request = @{ jsonrpc = '2.0'; id = [guid]::NewGuid().ToString(); method = 'tools/call'; params = @{ name = $Name; arguments = $Arguments } }
    $response = Invoke-RestMethod -Uri $script:endpoint -Method Post -Body ($request | ConvertTo-Json -Depth 15 -Compress) -ContentType 'application/json' -TimeoutSec $TimeoutSeconds
    if ($null -ne $response.PSObject.Properties['error']) { throw "MCP $Name failed: $($response.error.message)" }
    if ($null -eq $response.PSObject.Properties['result'] -or $response.result.isError) {
        $message = if ($null -ne $response.result.content) { ($response.result.content | ForEach-Object { $_.text }) -join ' ' } else { 'No response.' }
        throw "MCP $Name failed: $message"
    }
    return $response.result.structuredContent
}

function Get-UniqueNode([string]$Name) {
    $nodes = @( (Invoke-ScaleTool 'find_nodes_by_name' @{ name = $Name; match_mode = 'exact' }).nodes )
    if ($nodes.Count -ne 1) { throw "Expected one '$Name' node; found $($nodes.Count)." }
    return $nodes[0]
}

function Get-ProfilerObserverSettings {
    $settings = [ordered]@{}
    foreach ($name in @('Debug.EnableProfilerFrameLogging', 'Debug.EnableProfilerComponentTiming', 'Debug.EnableGpuRenderPipelineProfiling', 'Debug.EnableProfilerUdpSending')) {
        $entry = Invoke-ScaleTool 'get_editor_preference' @{ property_name = $name }
        if ($entry.value -isnot [bool]) { throw "Profiler preference '$name' is not a Boolean." }
        $settings[$name] = [bool]$entry.value
    }
    return $settings
}

function Get-SourceProfileValue([string]$NodeId, [string]$PropertyName, [string[]]$Names) {
    $value = (Invoke-ScaleTool 'get_component_property' @{
        node_id = $NodeId; component_type = 'PhysicsChainComponent'; property_name = $PropertyName
    }).value
    if ($null -eq $value) { throw "Source $PropertyName is unavailable." }
    $name = [string]$value
    if ($name -in $Names) { return $name }
    $index = 0
    if ([int]::TryParse($name, [ref]$index) -and $index -ge 0 -and $index -lt $Names.Count) {
        return $Names[$index]
    }
    throw "Source $PropertyName has an unknown value '$name'."
}

function Get-OutcomeCounts {
    $script:lastProfilerSnapshot = Invoke-ScaleTool 'get_render_profiler_stats'
    $counts = $script:lastProfilerSnapshot.vulkan.frame_lifecycle.outcome_counts
    if ($null -eq $counts) { throw 'Vulkan outcome counters are not available.' }
    return [ordered]@{
        completed = [long]$counts.completed
        deferred = [long]$counts.deferred
        rejected = [long]$counts.rejected
        failed = [long]$counts.failed
    }
}

function Get-CompletedFrameIntervals([long]$AfterSequence = -1) {
    $data = Invoke-ScaleTool 'invoke_method' @{
        type_name = 'XREngine.RuntimeEngine+Rendering+Stats+Vulkan'
        method_name = 'GetVulkanCompletedFrameIntervalTelemetry'
        arguments = @($AfterSequence)
    }
    $result = $data.result.properties
    if ($null -eq $result) { throw 'Completed-frame interval telemetry is not available.' }
    return $result
}

function Get-OutcomeDelta($Before, $After) {
    return [ordered]@{
        completed = $After.completed - $Before.completed
        deferred = $After.deferred - $Before.deferred
        rejected = $After.rejected - $Before.rejected
        failed = $After.failed - $Before.failed
    }
}

function Assert-StableOutcomes($Before, $After) {
    $delta = Get-OutcomeDelta $Before $After
    if ($delta.completed -lt 0 -or $delta.deferred -ne 0 -or $delta.rejected -ne 0 -or $delta.failed -ne 0) {
        throw "Vulkan frame outcomes changed during readiness or measurement: $($delta | ConvertTo-Json -Compress)"
    }
}

function Get-DispatcherSnapshot {
    $data = Invoke-ScaleTool 'invoke_method' @{ type_name = 'GPUPhysicsChainDispatcher'; method_name = 'get_Instance'; arguments = @() }
    return $data.result.properties
}

function Get-Count {
    return [int](Invoke-ScaleTool 'invoke_method' @{ type_name = 'GPUPhysicsChainDispatcher'; method_name = 'CaptureRegisteredComponentCount'; arguments = @() }).result
}

function Get-TelemetrySnapshot([string]$SourceNodeId) {
    $worldLateTick = (Invoke-ScaleTool 'get_component_property' @{ node_id = $SourceNodeId; component_type = 'PhysicsChainComponent'; property_name = 'WorldLateTickTelemetry' }).value
    $rigidRestCache = (Invoke-ScaleTool 'get_component_property' @{ node_id = $SourceNodeId; component_type = 'PhysicsChainComponent'; property_name = 'RigidGpuRestInputCacheDiagnostics' }).value
    $collection = (Invoke-ScaleTool 'invoke_method' @{ type_name = 'RuntimeWorldRenderer'; method_name = 'get_CollectionTelemetry'; arguments = @() }).result
    $stages = (Invoke-ScaleTool 'invoke_method' @{ type_name = 'RenderableMeshStageTelemetry'; method_name = 'Snapshot'; arguments = @() }).result
    $worldTicks = (Invoke-ScaleTool 'invoke_method' @{ type_name = 'RuntimeWorldTickTelemetry'; method_name = 'CaptureSnapshot'; arguments = @() }).result
    $contention = (Invoke-ScaleTool 'invoke_method' @{ type_name = 'TransformHierarchyStore'; method_name = 'GetReadContentionTelemetrySnapshot'; arguments = @() }).result
    return [ordered]@{ worldLateTick = $worldLateTick; rigidRestCache = $rigidRestCache; collection = $collection; renderableMeshStages = $stages; worldTicks = $worldTicks; transformReadContention = $contention }
}

function Get-NumericDifferences($Before, $After) {
    $differences = [ordered]@{}
    if ($null -eq $Before -or $null -eq $After) { return $differences }
    $beforeValue = if ($null -ne $Before.PSObject.Properties['properties']) { $Before.properties } else { $Before }
    $afterValue = if ($null -ne $After.PSObject.Properties['properties']) { $After.properties } else { $After }
    foreach ($property in $beforeValue.PSObject.Properties) {
        $name = $property.Name
        $old = $property.Value
        $new = $afterValue.$name
        if ($null -eq $old -or $null -eq $new) { continue }
        if ($old -is [ValueType] -and $new -is [ValueType] -and $old -isnot [bool]) {
            $differences[$name] = [double]$new - [double]$old
        }
        elseif ($old -is [pscustomobject]) {
            $nested = Get-NumericDifferences $old $new
            if ($nested.Count -gt 0) { $differences[$name] = $nested }
        }
    }
    return $differences
}

function Get-TickRates($Differences, [double]$Frequency, [long]$CallCount, [long]$RenderedFrames, [string]$Prefix = '') {
    $rates = [ordered]@{}
    foreach ($name in $Differences.Keys) {
        if ($name -notlike '*Ticks' -or $name -like '*SampleTicks' -or
            (-not [string]::IsNullOrEmpty($Prefix) -and $name -notlike "$Prefix*")) { continue }
        $milliseconds = [double]$Differences[$name] * 1000.0 / $Frequency
        $rates[$name] = [ordered]@{
            totalMilliseconds = $milliseconds
            millisecondsPerCall = $(if ($CallCount -gt 0) { $milliseconds / $CallCount } else { $null })
            millisecondsPerRenderedFrame = $(if ($RenderedFrames -gt 0) { $milliseconds / $RenderedFrames } else { $null })
        }
    }
    return $rates
}

function Get-SampledTickRates($Differences, [double]$Frequency) {
    $rates = [ordered]@{}
    foreach ($name in $Differences.Keys) {
        if ($name -notlike '*SampleTicks') { continue }
        $countName = $name.Substring(0, $name.Length - 'Ticks'.Length) + 'Count'
        $count = [long]$Differences[$countName]
        $milliseconds = [double]$Differences[$name] * 1000.0 / $Frequency
        $rates[$name] = [ordered]@{
            sampleCount = $count
            sampledMilliseconds = $milliseconds
            millisecondsPerSample = $(if ($count -gt 0) { $milliseconds / $count } else { $null })
        }
    }
    return $rates
}

function Get-StageDifference([string]$Before, [string]$After, [long]$RenderedFrames) {
    $start = @{}
    foreach ($entry in ($Before -split ';')) {
        if ($entry -match '^([A-Za-z][A-Za-z0-9_]*|\d+):(\d+)/(\d+)$') { $start[$matches[1]] = @([long]$matches[2], [long]$matches[3]) }
    }
    $delta = [ordered]@{}
    foreach ($entry in ($After -split ';')) {
        if ($entry -match '^([A-Za-z][A-Za-z0-9_]*|\d+):(\d+)/(\d+)$' -and $start.ContainsKey($matches[1])) {
            $stage = $matches[1]
            $ticks = [long]$matches[2] - $start[$stage][0]
            $calls = [long]$matches[3] - $start[$stage][1]
            $milliseconds = $ticks * 1000.0 / [System.Diagnostics.Stopwatch]::Frequency
            $delta[$stage] = [ordered]@{
                ticks = $ticks
                calls = $calls
                millisecondsPerRenderedFrame = $(if ($RenderedFrames -gt 0) { $milliseconds / $RenderedFrames } else { $null })
                microsecondsPerCall = $(if ($calls -gt 0) { $milliseconds * 1000.0 / $calls } else { $null })
            }
        }
    }
    return $delta
}

try {
    if ($CpuTraceSeconds -gt 0 -and $CpuTraceSeconds -gt $WindowSeconds - 5) {
        throw 'The CPU trace must end at least five seconds before the measurement window.'
    }
    if ($CpuTraceSeconds -gt 0) {
        $traceCommand = Get-Command dotnet-trace -ErrorAction Stop
    }
    if ([string]::IsNullOrWhiteSpace($OutputFolder)) {
        & (Join-Path $repoRoot 'Tools\Limit-AgentValidation.ps1') -ReserveTaskRun | Out-Null
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $OutputFolder = Join-Path $validationRoot "$stamp-physics-chain-scale\reports"
    }
    $outputPath = [System.IO.Path]::GetFullPath($OutputFolder)
    $prefix = $validationRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $outputPath.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'OutputFolder must be under Build/_AgentValidation.'
    }
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    $safeLabel = [regex]::Replace($Label, '[^A-Za-z0-9._-]', '-')
    $summaryPath = Join-Path $outputPath "$safeLabel-summary.json"

    if (-not $UseExistingSession) {
        $environmentPath = Join-Path $outputPath "$safeLabel-session-environment.json"
        @{ XRE_UNIT_TEST_WORLD_KIND = 'MathIntersections'; XRE_WORLD_TICK_TELEMETRY = $(if ($Telemetry) { '1' } else { '0' }) } |
            ConvertTo-Json | Set-Content -LiteralPath $environmentPath -Encoding UTF8
        & $sessionTool Start -Name $Session -Configuration Release -SessionEnvironmentFile $environmentPath -AsJson | Out-Null
        $sessionStartedHere = $true
    }

    $sessionView = & $sessionTool Status -Name $Session -AsJson | ConvertFrom-Json
    if ($sessionView.state -ne 'Ready' -and $sessionView.state -ne 'Running') { throw "Session '$Session' is $($sessionView.state)." }
    $script:endpoint = [string]$sessionView.endpoint
    if ([string]::IsNullOrWhiteSpace($script:endpoint)) { throw 'Session has no MCP endpoint.' }

    Invoke-ScaleTool 'set_editor_preference' @{ property_name = 'McpDispatchMode'; value = 'MainThread'; session_only = $true } | Out-Null
    $submissionOverrideToRestore = (Invoke-ScaleTool 'invoke_method' @{
        type_name = 'XREnvironment'; method_name = 'GetState'
        arguments = @('XRE_FORCE_MESH_SUBMISSION_STRATEGY')
    }).result.properties
    if ($submissionOverrideToRestore.HasRuntimeOverride -isnot [bool]) {
        throw 'Submission override state is unavailable.'
    }
    Invoke-ScaleTool 'invoke_method' @{
        type_name = 'XREnvironment'; method_name = 'SetRuntimeOverride'
        arguments = @('XRE_FORCE_MESH_SUBMISSION_STRATEGY', $SubmissionStrategy)
    } | Out-Null
    $submissionOverrideChanged = $true
    $profilerSettingsToRestore = Get-ProfilerObserverSettings
    foreach ($name in $profilerSettingsToRestore.Keys) {
        Invoke-ScaleTool 'set_editor_preference' @{ property_name = $name; value = $false; session_only = $true } | Out-Null
    }
    $summary.profilerObserverSettings = Get-ProfilerObserverSettings
    foreach ($name in $summary.profilerObserverSettings.Keys) {
        if ($summary.profilerObserverSettings[$name]) { throw "Profiler preference '$name' is still enabled." }
    }
    $source = Get-UniqueNode 'Physics Chain GPU Dispatcher Skinned Mesh Test'
    $root = Get-UniqueNode 'Root Node'
    $chainNodes = @((Invoke-ScaleTool 'find_nodes_by_type' @{ component_type = 'PhysicsChainComponent' }).nodes |
        Where-Object { $_.path -like "$($source.path)/*" })
    if ($chainNodes.Count -ne 1) { throw "Expected one source physics chain; found $($chainNodes.Count)." }
    $sourceChainId = [string]$chainNodes[0].id
    $sourceNodeId = [string]$chainNodes[0].id
    $rootInfo = Invoke-ScaleTool 'get_scene_node_info' @{ node_id = [string]$root.id }
    $controllers = @($rootInfo.components | Where-Object { $_.type -like '*MathIntersectionsWorldControllerComponent' })
    if ($controllers.Count -ne 1) { throw "Expected one Math Intersections controller; found $($controllers.Count)." }
    $controllerId = [string]$controllers[0].id

    $qualityNames = @('Strict', 'Hz30', 'Hz15', 'Hz7_5', 'Sleep', 'Automatic')
    $interpolationNames = @('Discrete', 'Interpolate', 'Extrapolate')
    $sourceProfileToRestore = [ordered]@{
        qualityTier = Get-SourceProfileValue $sourceNodeId 'QualityTier' $qualityNames
        interpolationMode = Get-SourceProfileValue $sourceNodeId 'InterpolationMode' $interpolationNames
    }
    $summary.originalSourceProfile = $sourceProfileToRestore
    if ($QualityTier -ne 'Authored' -or $InterpolationMode -ne 'Authored') {
        $sourceProfileChanged = $true
        if ($QualityTier -ne 'Authored') {
            Invoke-ScaleTool 'set_component_property' @{
                node_id = $sourceNodeId; component_type = 'PhysicsChainComponent'; property_name = 'QualityTier'; value = $QualityTier
            } | Out-Null
        }
        if ($InterpolationMode -ne 'Authored') {
            Invoke-ScaleTool 'set_component_property' @{
                node_id = $sourceNodeId; component_type = 'PhysicsChainComponent'; property_name = 'InterpolationMode'; value = $InterpolationMode
            } | Out-Null
        }
    }
    $summary.effectiveSourceProfile = [ordered]@{
        qualityTier = Get-SourceProfileValue $sourceNodeId 'QualityTier' $qualityNames
        interpolationMode = Get-SourceProfileValue $sourceNodeId 'InterpolationMode' $interpolationNames
    }
    if (($QualityTier -ne 'Authored' -and $summary.effectiveSourceProfile.qualityTier -ne $QualityTier) -or
        ($InterpolationMode -ne 'Authored' -and $summary.effectiveSourceProfile.interpolationMode -ne $InterpolationMode)) {
        throw 'Source physics-chain profile does not match the requested profile.'
    }

    Invoke-ScaleTool 'set_object_property' @{ object_id = [string]$source.id; property_name = 'IsActiveSelf'; value = $true } | Out-Null
    $cameraScale = [Math]::Max(1.0, [Math]::Sqrt($ChainCount / 2000.0))
    $summary.measurementCamera = [ordered]@{ x = 0; y = 400 * $cameraScale; z = 450 * $cameraScale }
    Invoke-ScaleTool 'set_editor_camera_view' @{ position_x = 0; position_y = $summary.measurementCamera.y; position_z = $summary.measurementCamera.z; look_at_x = 0; look_at_y = 0; look_at_z = 0; duration = 0 } | Out-Null
    Invoke-ScaleTool 'set_component_property' @{ node_id = [string]$root.id; component_id = $controllerId; property_name = '_benchmarkCopyCount'; value = $ChainCount } | Out-Null
    Invoke-ScaleTool 'set_component_property' @{ node_id = [string]$root.id; component_id = $controllerId; property_name = '_benchmarkDurationSeconds'; value = 120 } | Out-Null
    # A timeout can occur after the editor starts the benchmark.
    $benchmarkStarted = $true
    Invoke-ScaleTool 'invoke_method' @{ object_id = $controllerId; method_name = 'SetBenchmarkRunToggle'; arguments = @($true, $false) } 180 | Out-Null

    $readinessStart = Get-OutcomeCounts
    $readinessLimitSeconds = [Math]::Min(80, 110 - $WindowSeconds)
    $readyDeadline = [DateTime]::UtcNow.AddSeconds($readinessLimitSeconds)
    do {
        Start-Sleep -Seconds 1
        $readinessEnd = Get-OutcomeCounts
        $readinessDelta = Get-OutcomeDelta $readinessStart $readinessEnd
        if ($readinessDelta.completed -lt 0 -or $readinessDelta.deferred -ne 0 -or
            $readinessDelta.rejected -ne 0 -or $readinessDelta.failed -ne 0) {
            $readinessStart = $readinessEnd
        }
        $status = [string](Invoke-ScaleTool 'get_component_property' @{ node_id = [string]$root.id; component_id = $controllerId; property_name = '_benchmarkStatus' }).value
        if ($status -match 'failed|cancelled|timed out') { throw "Benchmark did not become ready: $status" }
        if ([DateTime]::UtcNow -ge $readyDeadline) {
            $summary.readinessStart = $readinessStart
            $summary.readinessEnd = $readinessEnd
            $summary.readinessDelta = $readinessDelta
            $script:lastProfilerSnapshot | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-readiness-profiler.json") -Encoding UTF8
            Get-DispatcherSnapshot | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-readiness-dispatcher.json") -Encoding UTF8
            throw "Readiness did not produce 30 clean completed frames: $status"
        }
    } while (($readinessEnd.completed - $readinessStart.completed) -lt 30)

    $beforeDispatcher = Get-DispatcherSnapshot
    $beforeDispatcher | ConvertTo-Json -Depth 40 |
        Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-dispatcher-before.json") -Encoding UTF8
    $beforeFailures = [long]$beforeDispatcher.DispatchDiagnostics.properties.FailureCount
    $beforeProducerEpoch = [long]$beforeDispatcher.OutputPageDiagnostics.properties.ProducerEpoch
    $beforeInputPages = $beforeDispatcher.InputPageDiagnostics.properties
    $beforeCount = [int]$beforeDispatcher.RegisteredComponentCount
    if ($beforeCount -ne $ChainCount) { throw "Registered chain count before window is $beforeCount; expected $ChainCount." }
    $beforeTelemetry = if ($Telemetry) { Get-TelemetrySnapshot $sourceChainId } else { $null }
    if ($Telemetry) {
        $beforeTelemetry | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-telemetry-before.json") -Encoding UTF8
    }
    $beforeRenderState = Invoke-ScaleTool 'get_render_state' @{ viewport_index = 0 }
    $beforeRenderState | ConvertTo-Json -Depth 40 |
        Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-render-state-before.json") -Encoding UTF8
    $expectedStrategy = if ($SubmissionStrategy -eq 'CpuDirect') { 0 } else { 2 }
    if ($beforeRenderState.canonicalFramePackage.state -ne 'Published' -or
        [int]$beforeRenderState.canonicalFramePackage.submission.resolved -ne $expectedStrategy -or
        [int]$beforeRenderState.canonicalFramePackage.submission.requested -ne $expectedStrategy -or
        $beforeRenderState.canonicalFramePackage.submission.downgraded) {
        throw 'The frozen command package does not use the requested submission strategy.'
    }
    if ($RequireDirectionalShadows -and
        (-not $beforeRenderState.directionalShadowLane.enabled -or
            -not $beforeRenderState.directionalShadowLane.consumerReady)) {
        throw 'The directional shadow consumer is not ready.'
    }
    @{ startedUtc = [DateTime]::UtcNow.ToString('O'); chainCount = $ChainCount; windowSeconds = $WindowSeconds } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-window-start.json") -Encoding UTF8
    $beforeIntervals = Get-CompletedFrameIntervals
    $beforeOutcomes = Get-OutcomeCounts
    $samples = New-Object 'System.Collections.Generic.List[object]'
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    if ($CpuTraceSeconds -gt 0) {
        $tracePath = Join-Path $outputPath "$safeLabel-cpu.nettrace"
        $traceArguments = @('collect', '--process-id', [string]$sessionView.processId,
            '--profile', 'dotnet-sampled-thread-time', '--duration', ('00:00:{0:00}' -f $CpuTraceSeconds),
            '--format', 'Speedscope', '--output', ('"{0}"' -f $tracePath))
        $traceProcess = Start-Process -FilePath $traceCommand.Source -ArgumentList $traceArguments -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $outputPath "$safeLabel-trace-output.log") `
            -RedirectStandardError (Join-Path $outputPath "$safeLabel-trace-error.log")
        $summary.cpuTraceStartedUtc = [DateTime]::UtcNow.ToString('O')
    }
    $nextSampleSeconds = 1.0
    while ($timer.Elapsed.TotalSeconds -lt $WindowSeconds) {
        $remaining = [Math]::Min($nextSampleSeconds, $WindowSeconds) - $timer.Elapsed.TotalSeconds
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Ceiling($remaining * 1000)) }
        if ($timer.Elapsed.TotalSeconds -ge $WindowSeconds) { break }
        $sample = Get-OutcomeCounts
        Assert-StableOutcomes $beforeOutcomes $sample
        $count = Get-Count
        if ($count -ne $ChainCount) { throw "Registered chain count changed to $count during the window." }
        $samples.Add([ordered]@{ elapsedSeconds = $timer.Elapsed.TotalSeconds; completed = $sample.completed; registeredChainCount = $count })
        # Slow tool calls skip missed slots instead of extending the wall-clock window.
        $nextSampleSeconds = [Math]::Floor($timer.Elapsed.TotalSeconds) + 1.0
    }
    $afterOutcomes = Get-OutcomeCounts
    $timer.Stop()
    $delta = Get-OutcomeDelta $beforeOutcomes $afterOutcomes
    $summary.elapsedSeconds = $timer.Elapsed.TotalSeconds
    $summary.sampleCount = $samples.Count
    $summary.completedHz = $delta.completed / $timer.Elapsed.TotalSeconds
    $summary.outcomeChanges = $delta
    $summary.samples = @($samples.ToArray())
    if ($null -ne $traceProcess) {
        if (-not $traceProcess.WaitForExit(30000)) { throw 'The CPU trace did not stop after its bounded window.' }
        if ($traceProcess.ExitCode -ne 0) { throw "The CPU trace failed with exit code $($traceProcess.ExitCode)." }
        $summary.cpuTraceCompleted = $true
    }
    $script:lastProfilerSnapshot | ConvertTo-Json -Depth 40 |
        Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-profiler-after.json") -Encoding UTF8
    $afterObserverSettings = Get-ProfilerObserverSettings
    $summary.profilerObserverSettingsAfter = $afterObserverSettings
    foreach ($name in $afterObserverSettings.Keys) {
        if ($afterObserverSettings[$name]) { throw "Profiler preference '$name' changed during the window." }
    }
    $afterIntervals = Get-CompletedFrameIntervals ([long]$beforeIntervals.Sequence)
    $summary.frameIntervalP95Milliseconds = [double]$afterIntervals.P95Milliseconds
    $summary.frameIntervalSampleCount = [int]$afterIntervals.SampleCount
    $summary.frameIntervalTelemetry = $afterIntervals
    Assert-StableOutcomes $beforeOutcomes $afterOutcomes
    $afterDispatcher = Get-DispatcherSnapshot
    $afterDispatcher | ConvertTo-Json -Depth 40 |
        Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-dispatcher-after.json") -Encoding UTF8
    if ($SubmissionStrategy -eq 'GpuIndirectZeroReadback' -and
        ([long]$afterDispatcher.ReadbackDiagnostics.properties.SubmittedCount -ne
            [long]$beforeDispatcher.ReadbackDiagnostics.properties.SubmittedCount)) {
        throw 'Physics readback was submitted during the strict GPU window.'
    }
    $afterFailures = [long]$afterDispatcher.DispatchDiagnostics.properties.FailureCount
    $afterProducerEpoch = [long]$afterDispatcher.OutputPageDiagnostics.properties.ProducerEpoch
    $afterInputPages = $afterDispatcher.InputPageDiagnostics.properties
    if ($afterFailures -ne $beforeFailures) {
        throw "Physics dispatch failed during the window: $($afterDispatcher.DispatchDiagnostics.properties.LastFailureStage)."
    }
    if ($afterProducerEpoch -le $beforeProducerEpoch) {
        throw 'Physics output pages did not advance during the window.'
    }
    if ([long]$afterInputPages.FailureCount -ne [long]$beforeInputPages.FailureCount -or
        [int]$afterInputPages.QuarantinedPageCount -ne 0) {
        throw "Mapped input pages failed during the window: $($afterInputPages.LastFailure)."
    }
    if ([long]$afterInputPages.AcquiredPageCount -le [long]$beforeInputPages.AcquiredPageCount -or
        [long]$afterInputPages.MappedWriteCount -le [long]$beforeInputPages.MappedWriteCount) {
        throw 'Mapped input pages did not advance during the window.'
    }
    if ([long]$afterInputPages.BufferAllocationCount -ne [long]$beforeInputPages.BufferAllocationCount) {
        throw 'Mapped input buffers grew during the steady-state window.'
    }
    $afterCount = [int]$afterDispatcher.RegisteredComponentCount
    if ($afterCount -ne $ChainCount) { throw "Registered chain count after window is $afterCount; expected $ChainCount." }
    $afterTelemetry = if ($Telemetry) { Get-TelemetrySnapshot $sourceChainId } else { $null }
    if ($Telemetry) {
        $afterTelemetry | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-telemetry-after.json") -Encoding UTF8
    }
    $afterRenderState = Invoke-ScaleTool 'get_render_state' @{ viewport_index = 0 }
    $afterRenderState | ConvertTo-Json -Depth 40 |
        Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-render-state-after.json") -Encoding UTF8
    $summary.directionalShadowLaneBefore = $beforeRenderState.directionalShadowLane
    $summary.directionalShadowLaneAfter = $afterRenderState.directionalShadowLane
    if ($RequireDirectionalShadows) {
        $shadowBefore = $beforeRenderState.directionalShadowLane
        $shadowAfter = $afterRenderState.directionalShadowLane
        if (-not $shadowAfter.enabled -or -not $shadowAfter.consumerReady -or
            $shadowAfter.acceptedGroups -le $shadowBefore.acceptedGroups -or
            $shadowAfter.rejectedGroups -ne $shadowBefore.rejectedGroups -or
            $shadowAfter.unconsumedGroups -ne $shadowBefore.unconsumedGroups -or
            $shadowAfter.genericGroups -ne $shadowBefore.genericGroups) {
            throw 'The directional shadow window did not produce new groups with stable admission outcomes.'
        }
    }
    if ($afterRenderState.canonicalFramePackage.state -ne 'Published' -or
        [int]$afterRenderState.canonicalFramePackage.submission.resolved -ne $expectedStrategy -or
        [int]$afterRenderState.canonicalFramePackage.submission.requested -ne $expectedStrategy -or
        $afterRenderState.canonicalFramePackage.submission.downgraded) {
        throw 'The frozen submission strategy changed during the window.'
    }
    if ([long]$afterIntervals.ResetCount -ne [long]$beforeIntervals.ResetCount -or
        -not [bool]$afterIntervals.IsValid -or [long]$afterIntervals.DroppedSampleCount -ne 0 -or
        [long]$afterIntervals.SampleCount -lt $delta.completed) {
        throw "Completed-frame interval window is incomplete: $($afterIntervals | ConvertTo-Json -Compress)"
    }
    $summary.accepted = $true
    $summary.startedUtc = [DateTime]::UtcNow.AddSeconds(-$timer.Elapsed.TotalSeconds).ToString('O')
    $summary.elapsedSeconds = $timer.Elapsed.TotalSeconds
    $summary.sampleCount = $samples.Count
    $summary.completedHz = $delta.completed / $timer.Elapsed.TotalSeconds
    $summary.frameIntervalP95Milliseconds = [double]$afterIntervals.P95Milliseconds
    $summary.frameIntervalSampleCount = [int]$afterIntervals.SampleCount
    $summary.frameIntervalTargetMet = [double]$afterIntervals.P95Milliseconds -le 10.0
    $summary.completedHzTargetMet = $summary.completedHz -ge 100.0
    $summary.timingTargetsMet = $summary.completedHzTargetMet -and $summary.frameIntervalTargetMet
    $summary.frameIntervalTelemetry = $afterIntervals
    $summary.outcomeChanges = $delta
    $summary.registeredChainCountBefore = $beforeCount
    $summary.registeredChainCountAfter = $afterCount
    $summary.paletteBefore = $beforeDispatcher.PaletteDiagnostics
    $summary.paletteAfter = $afterDispatcher.PaletteDiagnostics
    $summary.readbackBefore = $beforeDispatcher.ReadbackDiagnostics
    $summary.readbackAfter = $afterDispatcher.ReadbackDiagnostics
    $summary.inputPagesBefore = $beforeInputPages
    $summary.inputPagesAfter = $afterInputPages
    $summary.samples = @($samples.ToArray())
    $summary.dispatcherDifferences = Get-NumericDifferences $beforeDispatcher $afterDispatcher
    if ($Telemetry) {
        $worldDifferences = Get-NumericDifferences $beforeTelemetry.worldLateTick $afterTelemetry.worldLateTick
        $collectionDifferences = Get-NumericDifferences $beforeTelemetry.collection $afterTelemetry.collection
        $worldValue = if ($null -ne $beforeTelemetry.worldLateTick.PSObject.Properties['properties']) { $beforeTelemetry.worldLateTick.properties } else { $beforeTelemetry.worldLateTick }
        $collectionValue = $beforeTelemetry.collection.properties
        if (-not $worldValue.Enabled -or -not $collectionValue.Enabled -or
            [double]$worldValue.StopwatchFrequency -le 0 -or [double]$collectionValue.StopwatchFrequency -le 0) {
            throw 'Diagnostic telemetry is not enabled in this session. Start the session with XRE_WORLD_TICK_TELEMETRY=1.'
        }
        $summary.telemetryDifferences = [ordered]@{
            worldLateTick = $worldDifferences
            rigidRestCache = Get-NumericDifferences $beforeTelemetry.rigidRestCache $afterTelemetry.rigidRestCache
            worldLateTickRates = Get-TickRates $worldDifferences ([double]$worldValue.StopwatchFrequency) ([long]$worldDifferences.TickCount) $delta.completed
            sampledInputRates = Get-SampledTickRates $worldDifferences ([double]$worldValue.StopwatchFrequency)
            collection = $collectionDifferences
            collectionRates = [ordered]@{
                collect = Get-TickRates $collectionDifferences ([double]$collectionValue.StopwatchFrequency) ([long]$collectionDifferences.CollectCalls) $delta.completed 'Collect'
                swap = Get-TickRates $collectionDifferences ([double]$collectionValue.StopwatchFrequency) ([long]$collectionDifferences.SwapCalls) $delta.completed 'Swap'
            }
            renderableMeshStages = Get-StageDifference ([string]$beforeTelemetry.renderableMeshStages) ([string]$afterTelemetry.renderableMeshStages) $delta.completed
        }
    }
    if ($CaptureEvidence) {
        $capturesPath = Join-Path (Split-Path $outputPath -Parent) 'mcp-captures'
        New-Item -ItemType Directory -Path $capturesPath -Force | Out-Null
        $wideCapture = Invoke-ScaleTool 'capture_viewport_screenshot' @{ output_dir = $capturesPath; include_screen_space_ui = $false }
        Invoke-ScaleTool 'set_editor_camera_view' @{ position_x = 4; position_y = 4; position_z = 9; look_at_x = 0; look_at_y = 2; look_at_z = 0; duration = 0 } | Out-Null
        $nearCapture = Invoke-ScaleTool 'capture_viewport_screenshot' @{ output_dir = $capturesPath; include_screen_space_ui = $false }
        $summary.captures = [ordered]@{ wide = $wideCapture.path; near = $nearCapture.path }
    }
}
catch {
    $summary.accepted = $false
    $summary.reason = $_.Exception.Message
    $summary.failureTool = $script:lastToolName
    $summary.failureMethod = $script:lastMethodName
    if ($benchmarkStarted) {
        try {
            Invoke-ScaleTool 'get_render_profiler_stats' | ConvertTo-Json -Depth 40 |
                Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-failure-profiler.json") -Encoding UTF8
            Get-DispatcherSnapshot | ConvertTo-Json -Depth 40 |
                Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-failure-dispatcher.json") -Encoding UTF8
            Invoke-ScaleTool 'get_render_state' @{ viewport_index = 0 } | ConvertTo-Json -Depth 40 |
                Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-failure-render-state.json") -Encoding UTF8
        } catch { Write-Warning "Could not capture benchmark failure state: $($_.Exception.Message)" }
    }
    Write-Error $_
}
finally {
    if ($null -ne $traceProcess -and -not $traceProcess.HasExited) {
        Stop-Process -Id $traceProcess.Id -ErrorAction SilentlyContinue
    }
    if ($benchmarkStarted -and -not [string]::IsNullOrWhiteSpace($controllerId)) {
        try { Invoke-ScaleTool 'invoke_method' @{ object_id = $controllerId; method_name = 'SetBenchmarkRunToggle'; arguments = @($false, $false) } | Out-Null }
        catch { Write-Warning "Could not stop benchmark: $($_.Exception.Message)" }
    }
    if ($sourceProfileChanged -and $null -ne $sourceProfileToRestore) {
        $restoreFailures = @()
        foreach ($propertyName in @('QualityTier', 'InterpolationMode')) {
            $key = if ($propertyName -eq 'QualityTier') { 'qualityTier' } else { 'interpolationMode' }
            try {
                Invoke-ScaleTool 'set_component_property' @{
                    node_id = $sourceNodeId; component_type = 'PhysicsChainComponent'
                    property_name = $propertyName; value = $sourceProfileToRestore[$key]
                } | Out-Null
            } catch {
                $message = "Could not restore source ${propertyName}: $($_.Exception.Message)"
                $restoreFailures += $message
                Write-Warning $message
            }
        }
        $summary.sourceProfileRestoreFailures = $restoreFailures
    }
    foreach ($name in $profilerSettingsToRestore.Keys) {
        try { Invoke-ScaleTool 'set_editor_preference' @{ property_name = $name; value = $profilerSettingsToRestore[$name]; session_only = $true } | Out-Null }
        catch { Write-Warning "Could not restore profiler preference '$name': $($_.Exception.Message)" }
    }
    if ($submissionOverrideChanged) {
        try {
            if ($submissionOverrideToRestore.HasRuntimeOverride) {
                Invoke-ScaleTool 'invoke_method' @{
                    type_name = 'XREnvironment'; method_name = 'SetRuntimeOverride'
                    arguments = @('XRE_FORCE_MESH_SUBMISSION_STRATEGY', $submissionOverrideToRestore.RuntimeOverrideValue)
                } | Out-Null
            } else {
                Invoke-ScaleTool 'invoke_method' @{
                    type_name = 'XREnvironment'; method_name = 'ClearRuntimeOverride'
                    arguments = @('XRE_FORCE_MESH_SUBMISSION_STRATEGY')
                } | Out-Null
            }
        } catch { Write-Warning "Could not restore submission strategy: $($_.Exception.Message)" }
    }
    if ($sessionStartedHere) {
        try { & $sessionTool Stop -Name $Session -AsJson | Out-Null }
        catch { Write-Warning "Could not stop session: $($_.Exception.Message)" }
    }
    if ($null -ne $summaryPath) {
        $summary | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
        Write-Output $summaryPath
    }
}
