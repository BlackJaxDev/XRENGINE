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
    # Shared: every copy has identical mesh content and draws in one indexed instance group.
    # Unique: every copy has distinct mesh content, so no instance group can merge copies.
    [ValidateSet('Shared', 'Unique')]
    [string]$MeshSharing = 'Shared',
    [string]$Label = 'physics-chain-scale',
    [string]$OutputFolder = '',
    [switch]$Telemetry,
    [switch]$CaptureEvidence,
    [switch]$RequireDirectionalShadows,
    [switch]$UseExistingSession,
    # Measures every count in LadderChainCounts in one owned session and fits each phase.
    [switch]$Ladder,
    # Comma-separated counts. A string survives pwsh -File, which does not pass arrays.
    [string]$LadderChainCounts = '1,250,500,1000,2000',
    # Decimal or 0x-prefixed hexadecimal processor mask for the editor process.
    [string]$CpuAffinityMask = '',
    # Permits a window without nvidia-smi clock samples, for example on a non-NVIDIA GPU.
    [switch]$AllowMissingGpuClocks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$validationRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'Build\_AgentValidation'))
$sessionTool = Join-Path $repoRoot 'Tools\Manage-McpEditorSession.ps1'
$sessionStartedHere = $false
$benchmarkStarted = $false
$controllerId = $null
$rootNodeId = $null
$uniqueMeshesToRestore = $null
$uniqueMeshesChanged = $false
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
$editorProcess = $null
$originalAffinity = $null
$affinityChanged = $false
$requestedAffinity = $null
$nvidiaSmiPath = $null
$summary = [ordered]@{
    label = $Label
    session = $Session
    chainCount = $ChainCount
    windowSeconds = $WindowSeconds
    telemetryEnabled = [bool]$Telemetry
    cpuTraceSeconds = $CpuTraceSeconds
    requireDirectionalShadows = [bool]$RequireDirectionalShadows
    submissionStrategy = $SubmissionStrategy
    meshSharing = $MeshSharing
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

function Get-PhaseTimings($BeforeSnapshot, $AfterSnapshot, [long]$CompletedFrames) {
    # Cumulative frame-loop phase totals from get_render_profiler_stats. They work with every
    # observer off; the same snapshots bracket the completed-frame count.
    $beforeTotals = $BeforeSnapshot.frame_lifecycle.phase_totals
    $afterTotals = $AfterSnapshot.frame_lifecycle.phase_totals
    if ($null -eq $beforeTotals -or $null -eq $afterTotals) {
        Write-Warning 'Frame-loop phase totals are not available in this editor build.'
        return $null
    }
    $timings = [ordered]@{}
    foreach ($phase in @('update', 'collect', 'swap', 'render', 'collect_wait_for_render', 'render_wait_for_collect')) {
        $calls = [long]$afterTotals.$phase.calls - [long]$beforeTotals.$phase.calls
        $milliseconds = [double]$afterTotals.$phase.total_ms - [double]$beforeTotals.$phase.total_ms
        $timings[$phase] = [ordered]@{
            calls = $calls
            milliseconds = $milliseconds
            millisecondsPerCompletedFrame = $(if ($CompletedFrames -gt 0) { $milliseconds / $CompletedFrames } else { $null })
            millisecondsPerCall = $(if ($calls -gt 0) { $milliseconds / $calls } else { $null })
        }
    }
    return $timings
}

function ConvertTo-AffinityMask([string]$Text) {
    $value = $Text.Trim()
    try {
        if ($value -match '^0[xX][0-9A-Fa-f]+$') { $mask = [Convert]::ToInt64($value.Substring(2), 16) }
        else { $mask = [long]::Parse($value, [System.Globalization.CultureInfo]::InvariantCulture) }
    } catch { throw "CpuAffinityMask '$Text' is not a decimal or 0x-prefixed hexadecimal number." }
    $processorCount = [Environment]::ProcessorCount
    $limit = if ($processorCount -ge 63) { [long]::MaxValue } else { ([long]1 -shl $processorCount) - 1 }
    if ($mask -le 0 -or ($mask -band (-bnot $limit)) -ne 0) {
        throw "CpuAffinityMask '$Text' selects no processor or a processor that does not exist."
    }
    return $mask
}

function ConvertTo-NullableDouble([string]$Text) {
    $value = 0.0
    if ([double]::TryParse($Text.Trim(), [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture, [ref]$value)) { return $value }
    return $null
}

function Get-ActivePowerPlan {
    $text = ''
    try { $text = (& powercfg /getactivescheme 2>$null) -join ' ' } catch { }
    if ($text -match '([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\s*\((.+?)\)') {
        return [ordered]@{ guid = $matches[1]; name = $matches[2] }
    }
    return [ordered]@{ guid = $null; name = $null; raw = $text }
}

function Get-GpuInventory {
    if ($null -ne $script:nvidiaSmiPath) {
        $lines = @(& $script:nvidiaSmiPath --query-gpu=index,name,driver_version --format=csv,noheader 2>$null)
        if ($LASTEXITCODE -eq 0) {
            return @($lines | Where-Object { $_ } | ForEach-Object {
                $fields = @(([string]$_) -split ',\s*')
                [ordered]@{ index = [int]$fields[0]; name = $fields[1].Trim(); driver = $fields[2].Trim() }
            })
        }
    }
    return @(Get-CimInstance -ClassName Win32_VideoController | ForEach-Object {
        [ordered]@{ index = $null; name = [string]$_.Name; driver = [string]$_.DriverVersion }
    })
}

function Get-MachineState([System.Diagnostics.Process]$Process) {
    $Process.Refresh()
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    return [ordered]@{
        cpu = @(Get-CimInstance -ClassName Win32_Processor | ForEach-Object { ([string]$_.Name).Trim() })
        logicalProcessorCount = [Environment]::ProcessorCount
        os = "$($os.Caption) $($os.Version)"
        gpus = Get-GpuInventory
        powerPlan = Get-ActivePowerPlan
        editorProcess = [ordered]@{
            processId = $Process.Id
            affinityMask = ('0x{0:X}' -f $Process.ProcessorAffinity.ToInt64())
            requestedAffinityMask = $(if ($null -ne $script:requestedAffinity) { '0x{0:X}' -f $script:requestedAffinity } else { $null })
            priorityClass = [string]$Process.PriorityClass
        }
        gpuClocks = $null
    }
}

function Get-GpuClockSample([double]$ElapsedSeconds) {
    $lines = @(& $script:nvidiaSmiPath --query-gpu=index,pstate,clocks.gr,clocks.mem,power.draw,temperature.gpu,utilization.gpu --format=csv,noheader,nounits 2>$null)
    if ($LASTEXITCODE -ne 0) { return $null }
    $gpus = New-Object 'System.Collections.Generic.List[object]'
    foreach ($line in $lines) {
        $fields = @(([string]$line) -split ',')
        if ($fields.Count -lt 7) { continue }
        $gpus.Add([ordered]@{
            index = [int]$fields[0].Trim()
            pstate = $fields[1].Trim()
            graphicsMHz = ConvertTo-NullableDouble $fields[2]
            memoryMHz = ConvertTo-NullableDouble $fields[3]
            powerWatts = ConvertTo-NullableDouble $fields[4]
            temperatureC = ConvertTo-NullableDouble $fields[5]
            utilizationPercent = ConvertTo-NullableDouble $fields[6]
        })
    }
    if ($gpus.Count -eq 0) { return $null }
    return [ordered]@{ elapsedSeconds = $ElapsedSeconds; gpus = @($gpus.ToArray()) }
}

function Get-ValueRange($Values) {
    $numbers = @($Values | Where-Object { $null -ne $_ })
    if ($numbers.Count -eq 0) { return $null }
    $measure = $numbers | Measure-Object -Minimum -Maximum -Average
    return [ordered]@{ minimum = $measure.Minimum; mean = $measure.Average; maximum = $measure.Maximum }
}

function Get-GpuClockSummary($Samples) {
    $byGpu = [ordered]@{}
    foreach ($sample in $Samples) {
        foreach ($gpu in $sample.gpus) {
            $key = "gpu$($gpu.index)"
            if (-not $byGpu.Contains($key)) { $byGpu[$key] = New-Object 'System.Collections.Generic.List[object]' }
            $byGpu[$key].Add($gpu)
        }
    }
    $result = [ordered]@{}
    foreach ($key in $byGpu.Keys) {
        $entries = @($byGpu[$key].ToArray())
        $result[$key] = [ordered]@{
            sampleCount = $entries.Count
            pstates = @($entries | ForEach-Object { $_.pstate } | Sort-Object -Unique)
            graphicsMHz = Get-ValueRange @($entries | ForEach-Object { $_.graphicsMHz })
            memoryMHz = Get-ValueRange @($entries | ForEach-Object { $_.memoryMHz })
            utilizationPercent = Get-ValueRange @($entries | ForEach-Object { $_.utilizationPercent })
        }
    }
    return $result
}

function Get-LinearFit([double[]]$X, [double[]]$Y) {
    # Ordinary least squares: fixed cost (intercept) and cost per chain (slope).
    $n = $X.Count
    if ($n -lt 2 -or $Y.Count -ne $n) { return $null }
    $meanX = 0.0; $meanY = 0.0
    for ($i = 0; $i -lt $n; $i++) { $meanX += $X[$i]; $meanY += $Y[$i] }
    $meanX /= $n; $meanY /= $n
    $sxx = 0.0; $sxy = 0.0; $syy = 0.0
    for ($i = 0; $i -lt $n; $i++) {
        $dx = $X[$i] - $meanX; $dy = $Y[$i] - $meanY
        $sxx += $dx * $dx; $sxy += $dx * $dy; $syy += $dy * $dy
    }
    if ($sxx -le 0.0) { return $null }
    $slope = $sxy / $sxx
    return [ordered]@{
        pointCount = $n
        fixedMilliseconds = $meanY - $slope * $meanX
        microsecondsPerChain = $slope * 1000.0
        rSquared = $(if ($syy -gt 0.0) { ($sxy * $sxy) / ($sxx * $syy) } else { 1.0 })
    }
}

function Get-PointFit($Points, [scriptblock]$Selector) {
    $x = New-Object 'System.Collections.Generic.List[double]'
    $y = New-Object 'System.Collections.Generic.List[double]'
    foreach ($point in $Points) {
        $value = & $Selector $point
        if ($null -eq $value) { continue }
        $x.Add([double]$point.chainCount)
        $y.Add([double]$value)
    }
    return Get-LinearFit $x.ToArray() $y.ToArray()
}

function Get-LadderFits($Points) {
    $accepted = @($Points | Where-Object { $_.accepted })
    if ($accepted.Count -lt 2) { return $null }
    $phases = [ordered]@{}
    foreach ($phase in @('update', 'collect', 'swap', 'render', 'collect_wait_for_render', 'render_wait_for_collect')) {
        $phases[$phase] = [ordered]@{
            perCall = Get-PointFit $accepted { param($p) if ($null -eq $p.phaseTimings) { $null } else { $p.phaseTimings.$phase.millisecondsPerCall } }
            perCompletedFrame = Get-PointFit $accepted { param($p) if ($null -eq $p.phaseTimings) { $null } else { $p.phaseTimings.$phase.millisecondsPerCompletedFrame } }
        }
    }
    return [ordered]@{
        acceptedPointCount = $accepted.Count
        meanFrameIntervalMilliseconds = Get-PointFit $accepted { param($p) $p.meanFrameIntervalMilliseconds }
        frameIntervalP95Milliseconds = Get-PointFit $accepted { param($p) $p.frameIntervalP95Milliseconds }
        phases = $phases
    }
}

function Wait-ForBenchmarkTeardown([int]$TimeoutSeconds) {
    # Gradual teardown restores the single source chain before the next start.
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Get-Count) -le 1) { return $true }
        Start-Sleep -Seconds 1
    }
    Write-Warning "Benchmark teardown did not finish within $TimeoutSeconds seconds."
    return $false
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

    $nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if ($null -ne $nvidiaSmi) { $script:nvidiaSmiPath = $nvidiaSmi.Source }
    elseif (-not $AllowMissingGpuClocks) {
        throw 'nvidia-smi was not found. Add it to PATH, or pass -AllowMissingGpuClocks to measure without GPU clock samples.'
    }
    if (-not [string]::IsNullOrWhiteSpace($CpuAffinityMask)) { $requestedAffinity = ConvertTo-AffinityMask $CpuAffinityMask }

    if ($Ladder) {
        # Each point runs this script in a child process against the same owned session,
        # so the single-window path below stays the only measurement implementation.
        $parsedCounts = New-Object 'System.Collections.Generic.List[int]'
        foreach ($token in ($LadderChainCounts -split '[,\s]+')) {
            if ([string]::IsNullOrWhiteSpace($token)) { continue }
            $value = 0
            if (-not [int]::TryParse($token, [ref]$value) -or $value -lt 1 -or $value -gt 10000) {
                throw "LadderChainCounts contains '$token'; each count must be an integer from 1 to 10000."
            }
            $parsedCounts.Add($value)
        }
        $counts = @($parsedCounts.ToArray() | Sort-Object -Unique)
        if ($counts.Count -lt 2) { throw 'A ladder needs at least two distinct chain counts.' }
        $summary = [ordered]@{
            label = $Label
            session = $Session
            ladder = $true
            chainCounts = $counts
            windowSeconds = $WindowSeconds
            submissionStrategy = $SubmissionStrategy
            meshSharing = $MeshSharing
            requestedSourceProfile = [ordered]@{ qualityTier = $QualityTier; interpolationMode = $InterpolationMode }
            cpuAffinityMask = $(if ($null -ne $requestedAffinity) { '0x{0:X}' -f $requestedAffinity } else { $null })
            accepted = $false
            reason = $null
            points = $null
            fits = $null
        }
        $summaryPath = Join-Path $outputPath "$safeLabel-ladder-summary.json"
        $hostPath = (Get-Process -Id $PID).Path
        $points = New-Object 'System.Collections.Generic.List[object]'
        foreach ($count in $counts) {
            $teardownFinished = Wait-ForBenchmarkTeardown 90
            $pointLabel = "$Label-$count"
            $pointSafeLabel = [regex]::Replace($pointLabel, '[^A-Za-z0-9._-]', '-')
            $childArgs = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
                '-Session', $Session, '-UseExistingSession', '-ChainCount', [string]$count,
                '-WindowSeconds', [string]$WindowSeconds, '-SubmissionStrategy', $SubmissionStrategy,
                '-QualityTier', $QualityTier, '-InterpolationMode', $InterpolationMode,
                '-MeshSharing', $MeshSharing, '-Label', $pointLabel, '-OutputFolder', $outputPath)
            if ($Telemetry) { $childArgs += '-Telemetry' }
            if ($CaptureEvidence) { $childArgs += '-CaptureEvidence' }
            if ($RequireDirectionalShadows) { $childArgs += '-RequireDirectionalShadows' }
            if ($AllowMissingGpuClocks) { $childArgs += '-AllowMissingGpuClocks' }
            if ($CpuTraceSeconds -gt 0) { $childArgs += @('-CpuTraceSeconds', [string]$CpuTraceSeconds) }
            if ($null -ne $requestedAffinity) { $childArgs += @('-CpuAffinityMask', $CpuAffinityMask) }
            # Windows PowerShell 5.1 turns redirected native stderr into terminating errors under Stop.
            $previousPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try { & $hostPath @childArgs > (Join-Path $outputPath "$pointSafeLabel-console.log") 2>&1 }
            finally { $ErrorActionPreference = $previousPreference }
            $exitCode = $LASTEXITCODE
            $pointSummaryPath = Join-Path $outputPath "$pointSafeLabel-summary.json"
            $point = [ordered]@{
                chainCount = $count
                summaryPath = $pointSummaryPath
                exitCode = $exitCode
                teardownFinishedBeforeStart = $teardownFinished
                accepted = $false
                reason = 'The point wrote no summary.'
                completedHz = $null
                meanFrameIntervalMilliseconds = $null
                frameIntervalP95Milliseconds = $null
                phaseTimings = $null
                gpuClocks = $null
            }
            if (Test-Path -LiteralPath $pointSummaryPath) {
                $pointSummary = Get-Content -LiteralPath $pointSummaryPath -Raw | ConvertFrom-Json
                $point.accepted = [bool]$pointSummary.accepted
                $point.reason = $pointSummary.reason
                if ($null -ne $pointSummary.PSObject.Properties['completedHz'] -and [double]$pointSummary.completedHz -gt 0) {
                    $point.completedHz = [double]$pointSummary.completedHz
                    $point.meanFrameIntervalMilliseconds = 1000.0 / [double]$pointSummary.completedHz
                }
                if ($null -ne $pointSummary.PSObject.Properties['frameIntervalP95Milliseconds']) {
                    $point.frameIntervalP95Milliseconds = $pointSummary.frameIntervalP95Milliseconds
                }
                if ($null -ne $pointSummary.PSObject.Properties['phaseTimings']) { $point.phaseTimings = $pointSummary.phaseTimings }
                if ($null -ne $pointSummary.PSObject.Properties['machineState'] -and $null -ne $pointSummary.machineState) {
                    $point.gpuClocks = $pointSummary.machineState.gpuClocks.summary
                }
            }
            $points.Add($point)
        }
        Wait-ForBenchmarkTeardown 90 | Out-Null
        $summary.points = @($points.ToArray())
        $summary.fits = Get-LadderFits $summary.points
        $summary.accepted = @($summary.points | Where-Object { -not $_.accepted }).Count -eq 0
        if (-not $summary.accepted) {
            $summary.reason = 'At least one ladder point was not accepted. The fits use accepted points only.'
        }
        return
    }

    $editorProcess = Get-Process -Id ([int]$sessionView.processId) -ErrorAction Stop
    if ($null -ne $requestedAffinity) {
        $originalAffinity = $editorProcess.ProcessorAffinity
        $editorProcess.ProcessorAffinity = [IntPtr]$requestedAffinity
        $affinityChanged = $true
    }

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
    $rootNodeId = [string]$root.id
    $uniqueMeshesToRestore = [bool](Invoke-ScaleTool 'get_component_property' @{
        node_id = $rootNodeId; component_id = $controllerId; property_name = '_benchmarkUniqueMeshes'
    }).value
    $uniqueMeshesChanged = $true
    Invoke-ScaleTool 'set_component_property' @{
        node_id = $rootNodeId; component_id = $controllerId; property_name = '_benchmarkUniqueMeshes'; value = ($MeshSharing -eq 'Unique')
    } | Out-Null

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

    # Clean frame outcomes do not prove a warm frame. Physics output must advance, and a
    # late directional shadow pipeline compile retries frames internally, which settles the
    # physics markers of the dropped plan as PlanUnsubmitted inside the window. Wait for
    # accepted shadow groups with no new rejections. A stalled physics ring fails at once.
    while ($true) {
        $healthStart = Get-DispatcherSnapshot
        $shadowStart = (Invoke-ScaleTool 'get_render_state' @{ viewport_index = 0 }).directionalShadowLane
        Start-Sleep -Seconds 1
        $healthEnd = Get-DispatcherSnapshot
        $shadowEnd = (Invoke-ScaleTool 'get_render_state' @{ viewport_index = 0 }).directionalShadowLane
        if ([long]$healthEnd.DispatchDiagnostics.properties.FailureCount -ne [long]$healthStart.DispatchDiagnostics.properties.FailureCount -or
            [long]$healthEnd.OutputPageDiagnostics.properties.ProducerEpoch -le [long]$healthStart.OutputPageDiagnostics.properties.ProducerEpoch) {
            throw "Physics output is not advancing after readiness: $($healthEnd.DispatchDiagnostics.properties.LastFailureStage), producer epoch $($healthEnd.OutputPageDiagnostics.properties.ProducerEpoch)."
        }
        $shadowsWarm = $null -eq $shadowEnd -or -not $shadowEnd.enabled -or
            ([long]$shadowEnd.acceptedGroups -gt [long]$shadowStart.acceptedGroups -and
                [long]$shadowEnd.rejectedGroups -eq [long]$shadowStart.rejectedGroups)
        if ($shadowsWarm) { break }
        if ([DateTime]::UtcNow -ge $readyDeadline) {
            throw "Directional shadow admission did not settle before the readiness limit: accepted $($shadowEnd.acceptedGroups), rejected $($shadowEnd.rejectedGroups), $($shadowEnd.lastDeclineReason)."
        }
    }
    $summary.warmGate = [ordered]@{ shadowAcceptedGroups = $shadowEnd.acceptedGroups; shadowRejectedGroups = $shadowEnd.rejectedGroups }

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
    $summary.machineState = Get-MachineState $editorProcess
    $gpuClockSamples = New-Object 'System.Collections.Generic.List[object]'
    $beforeIntervals = Get-CompletedFrameIntervals
    $beforeOutcomes = Get-OutcomeCounts
    $beforeProfilerSnapshot = $script:lastProfilerSnapshot
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
        if ($null -ne $script:nvidiaSmiPath) {
            $clockSample = Get-GpuClockSample $timer.Elapsed.TotalSeconds
            if ($null -ne $clockSample) { $gpuClockSamples.Add($clockSample) }
        }
        # Slow tool calls skip missed slots instead of extending the wall-clock window.
        $nextSampleSeconds = [Math]::Floor($timer.Elapsed.TotalSeconds) + 1.0
    }
    $afterOutcomes = Get-OutcomeCounts
    $timer.Stop()
    $afterProfilerSnapshot = $script:lastProfilerSnapshot
    $delta = Get-OutcomeDelta $beforeOutcomes $afterOutcomes
    $summary.elapsedSeconds = $timer.Elapsed.TotalSeconds
    $summary.sampleCount = $samples.Count
    $summary.completedHz = $delta.completed / $timer.Elapsed.TotalSeconds
    $summary.outcomeChanges = $delta
    $summary.phaseTimings = Get-PhaseTimings $beforeProfilerSnapshot $afterProfilerSnapshot $delta.completed
    $summary.samples = @($samples.ToArray())
    $summary.machineState.gpuClocks = [ordered]@{
        source = $(if ($null -ne $script:nvidiaSmiPath) { 'nvidia-smi' } else { 'unavailable' })
        sampleCount = $gpuClockSamples.Count
        summary = Get-GpuClockSummary @($gpuClockSamples.ToArray())
        samples = @($gpuClockSamples.ToArray())
    }
    if ($null -ne $script:nvidiaSmiPath -and $gpuClockSamples.Count -eq 0) {
        throw 'GPU clock sampling returned no samples inside the timed window.'
    }
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
    if ($SubmissionStrategy -eq 'GpuIndirectZeroReadback') {
        # Indexed instance groups merge payloads with identical content. Each unique copy
        # needs its own group; shared copies need fewer groups than copies.
        $inputShape = (Invoke-ScaleTool 'get_advanced_profile_diagnostics' @{ viewport_index = 0 }).framePlanInputCopyDiagnostics
        if ($null -eq $inputShape) { throw 'Advanced visibility input diagnostics are not available.' }
        $summary.meshSharingEvidence = [ordered]@{
            payloadCount = [int]$inputShape.LatestPayloadCount
            indexedInstanceGroupCount = [int]$inputShape.LatestIndexedInstanceGroupCount
        }
        $groupCount = $summary.meshSharingEvidence.indexedInstanceGroupCount
        if ($MeshSharing -eq 'Unique' -and $groupCount -lt $ChainCount) {
            throw "Unique meshes formed $groupCount indexed instance groups for $ChainCount chains; content is shared."
        }
        if ($MeshSharing -eq 'Shared' -and $ChainCount -gt 1 -and $groupCount -ge $ChainCount) {
            throw "Shared meshes formed $groupCount indexed instance groups for $ChainCount chains; instancing did not merge them."
        }
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
            [ordered]@{
                pages = (Invoke-ScaleTool 'invoke_method' @{ type_name = 'GPUPhysicsChainDispatcher'; method_name = 'CaptureOutputPageStates'; arguments = @() }).result
                stalls = (Invoke-ScaleTool 'invoke_method' @{ type_name = 'GPUPhysicsChainDispatcher'; method_name = 'CaptureOutputPageStallDiagnostics'; arguments = @() }).result
            } | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $outputPath "$safeLabel-failure-output-pages.json") -Encoding UTF8
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
    if ($uniqueMeshesChanged) {
        try {
            Invoke-ScaleTool 'set_component_property' @{
                node_id = $rootNodeId; component_id = $controllerId; property_name = '_benchmarkUniqueMeshes'; value = $uniqueMeshesToRestore
            } | Out-Null
        } catch { Write-Warning "Could not restore the benchmark mesh-sharing mode: $($_.Exception.Message)" }
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
    if ($affinityChanged -and $null -ne $editorProcess) {
        try { if (-not $editorProcess.HasExited) { $editorProcess.ProcessorAffinity = $originalAffinity } }
        catch { Write-Warning "Could not restore the editor processor affinity: $($_.Exception.Message)" }
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
