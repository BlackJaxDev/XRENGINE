<#
.SYNOPSIS
Runs a bounded MCP control-plane smoke test against an existing RenderBench build.
.EXAMPLE
pwsh Tools/Tests/Test-RenderProfileMcp.ps1 -RunRoot Build/_AgentValidation/<existing-run> `
  -RenderBenchDirectory Build/RenderBench/Release/AnyCPU/Release/net10.0-windows7.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [string]$RenderBenchDirectory = 'Build/RenderBench/Release/AnyCPU/Release/net10.0-windows7.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$manager = Join-Path $repoRoot 'Tools/Manage-McpRenderBenchSession.ps1'
$validationRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'Build/_AgentValidation'))
$sharedSessions = Join-Path $validationRoot '00000000-000000-shared/mcp-sessions'
$run = [IO.Path]::GetFullPath((Join-Path $repoRoot $RunRoot))
$validationPrefix = $validationRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $run.StartsWith($validationPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $run -PathType Container)) {
    throw '-RunRoot must name an existing task run beneath Build/_AgentValidation.'
}
$source = [IO.Path]::GetFullPath((Join-Path $repoRoot $RenderBenchDirectory))
foreach ($file in @('XREngine.RenderBench.dll', 'XREngine.RenderBench.deps.json',
                   'XREngine.RenderBench.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) {
        throw "The supplied RenderBench directory lacks $file. Build separately before running this smoke test."
    }
}
$reportDirectory = Join-Path $run 'reports/mcp'
[IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$sessionName = "render-profile-smoke-$suffix"
$sessionRoot = Join-Path $sharedSessions "renderbench-$sessionName"
$manifestPath = Join-Path $sessionRoot 'session.json'
$started = $false
$stopped = $false
$checks = [Collections.Generic.List[string]]::new()
$endpoint = $null
$headers = $null

function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Copy-Binaries([string]$destinationRoot) {
    $bin = Join-Path $destinationRoot 'artifacts/bin/smoke'
    [IO.Directory]::CreateDirectory($bin) | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $bin -Recurse -Force
}

function Invoke-Tool([string]$name, [hashtable]$arguments, [bool]$expectError = $false) {
    $body = @{ jsonrpc = '2.0'; id = [Guid]::NewGuid().ToString('N'); method = 'tools/call';
        params = @{ name = $name; arguments = $arguments } } | ConvertTo-Json -Depth 32 -Compress
    $response = Invoke-RestMethod -Method Post -Uri $endpoint -Headers $headers -ContentType 'application/json' `
        -Body $body -TimeoutSec 30
    if ($null -ne $response.PSObject.Properties['error']) {
        throw "$name transport error: $($response.error.message)"
    }
    if ([bool]$response.result.isError -ne $expectError) {
        $message = @($response.result.content | ForEach-Object { $_.text }) -join ' '
        throw "$name returned unexpected isError=$($response.result.isError): $message"
    }
    return $response.result.structuredContent
}

function New-Recipe([string]$name, [int]$warmup, [int]$timeout,
                    [int[]]$workers = @(1), [int]$capture = 20) {
    return @{
        schema_version = 1; name = $name; component = 'SecondaryCommandRecording';
        execution_mode = 'component'; backend = 'vulkan'; fixture = 'secondary-command-recording';
        width = 640; height = 360; warmup_frames = $warmup; stability_frames = 10;
        capture_frames = $capture; repetitions = 1; timeout_seconds = $timeout;
        instrumentation = 'aggregate_cpu, coarse_gpu'; validation_mode = 'counters_and_hash';
        label_policy = 'disabled'; hardware_counter_policy = 'disabled';
        cpu_sampling_policy = 'aggregate_only'; profile_mode = 'clean_profile';
        worker_counts = $workers;
        scene = @{ scene_identity = 'synthetic:immutable-command-chains'; camera_identity = 'synthetic:none';
            animation_identity = 'frozen'; output_identities = @('control-color') };
        mutation = @{ policy = 'forced_dirty_every_frame'; dirty_every_n_frames = 1 };
        workload = @{ chain_count = 32; barrier_count = 258 };
        budgets = @{ max_capture_thread_allocated_bytes = 0; max_worker_allocated_bytes = 0;
            require_output_hash = $true };
    }
}

function Load-Recipe($recipe) {
    $json = $recipe | ConvertTo-Json -Depth 32 -Compress
    $loaded = Invoke-Tool 'load_render_profile_recipe' @{ recipe_json = $json }
    Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$loaded.recipeId)) 'Recipe load returned no recipeId.'
    return $loaded.recipeId
}

function Wait-Status([string]$sessionId, [string[]]$terminalStates, [int]$seconds) {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($seconds)
    do {
        $status = Invoke-Tool 'get_render_profile_status' @{ session_id = $sessionId }
        if ($status.state -in $terminalStates) { return $status }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Profile $sessionId did not reach $($terminalStates -join '/') within $seconds seconds."
}

function Wait-ResultFiles([int]$count, [int]$seconds, [string]$sessionId = '', [string]$matrixJobId = '') {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($seconds)
    $nextStatusCheck = [DateTimeOffset]::MaxValue
    do {
        $paths = @(Get-ChildItem -LiteralPath $evidenceRoot -Recurse -Filter 'render-bench-result.json' `
            -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
        if ($paths.Count -ge $count) { return $paths }
        if ($sessionId -and $nextStatusCheck -eq [DateTimeOffset]::MaxValue -and
            @(Get-ChildItem -LiteralPath $evidenceRoot -Recurse -Filter 'render-profile-validation.json' `
                -File -ErrorAction SilentlyContinue).Count -gt 0) {
            # Validation is written after measured frames; only then inspect buffered MCP state.
            $nextStatusCheck = [DateTimeOffset]::UtcNow.AddMilliseconds(500)
        }
        if ($sessionId -and [DateTimeOffset]::UtcNow -ge $nextStatusCheck) {
            try {
                $profileStatus = Invoke-Tool 'get_render_profile_status' @{ session_id = $sessionId }
                if ($profileStatus.state -in @('Failed', 'Cancelled')) {
                    throw "Profile $sessionId ended $($profileStatus.state): $($profileStatus.error)"
                }
            }
            catch {
                if ($_.Exception.Message -like "Profile $sessionId ended *") { throw }
                # Drainage may still suspend the transport after validation was written.
            }
            $nextStatusCheck = [DateTimeOffset]::UtcNow.AddSeconds(2)
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($sessionId) {
        try {
            $profileStatus = Invoke-Tool 'get_render_profile_status' @{ session_id = $sessionId }
            throw "Expected $count durable result files; profile $sessionId is $($profileStatus.state): $($profileStatus.error)"
        }
        catch {
            if ($_.Exception.Message -like "Expected $count durable result files; profile *") { throw }
        }
    }
    if ($matrixJobId) {
        try {
            $matrixStatus = Invoke-Tool 'get_render_profile_matrix_status' @{ job_id = $matrixJobId }
            throw "Expected $count durable result files; matrix $matrixJobId is $($matrixStatus.state): $($matrixStatus.error)"
        }
        catch {
            if ($_.Exception.Message -like "Expected $count durable result files; matrix *") { throw }
        }
    }
    throw "Expected $count durable result files within $seconds seconds."
}

function Assert-Result([string]$path, [int]$frames) {
    Assert-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Missing result at $path."
    $result = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100
    Assert-Condition ($result.captureFrames -eq $frames) "Captured $($result.captureFrames) frames, expected $frames."
    Assert-Condition (@($result.cpuFrameNanoseconds).Count -eq $frames) 'CPU frame stream length differs.'
    Assert-Condition (@($result.gpuFrameNanoseconds).Count -eq $frames) 'GPU frame stream length differs.'
    Assert-Condition (-not [string]::IsNullOrWhiteSpace([string]$result.outputSha256)) 'Output SHA-256 is absent.'
    Assert-Condition ($result.allocatedBytesOnCaptureThread -eq 0) 'Capture thread allocated bytes.'
    Assert-Condition ($result.allocatedBytesOnFixtureWorkers -eq 0) 'Fixture workers allocated bytes.'
    foreach ($gate in $result.stabilityGates) {
        Assert-Condition ([bool]$gate.passed) "Validity gate $($gate.name) failed."
    }
    Assert-Condition (@($result.stabilityGates).Count -gt 0) 'Result contains no validity gates.'
    return $result
}

function Safe-RemoveOwnSession([string]$path, [string]$name) {
    $resolved = [IO.Path]::GetFullPath($path)
    $prefix = [IO.Path]::GetFullPath($sharedSessions).TrimEnd([IO.Path]::DirectorySeparatorChar) + `
        [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -cne "renderbench-$name") {
        throw "Refusing to remove a session outside the owned manager directory: $resolved"
    }
    if (Test-Path -LiteralPath $resolved -PathType Container) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

try {
    Copy-Binaries $sessionRoot
    $evidenceRoot = Join-Path $reportDirectory "evidence-$suffix"
    $session = & $manager Start -Name $sessionName -NoBuild -Configuration Release -Platform AnyCPU `
        -ExecutionMode Component -OutputDirectory $evidenceRoot -AsJson | ConvertFrom-Json
    $started = $true
    Assert-Condition ($session.owned -and $session.state -eq 'Idle') 'Named RenderBench session did not reach Idle.'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $ownedStatus = & $manager Status -Name $sessionName -AsJson | ConvertFrom-Json
    Assert-Condition ($ownedStatus.owned -and $ownedStatus.processId -eq $manifest.processId -and
        $ownedStatus.state -eq 'Idle') 'Named session status did not verify exact process ownership.'
    $listed = @(& $manager List -AsJson | ConvertFrom-Json)
    Assert-Condition (@($listed | Where-Object { $_.name -eq $sessionName -and $_.owned -and
        $_.processId -eq $manifest.processId }).Count -eq 1) 'Manager list did not verify named session ownership.'
    $endpoint = [string]$manifest.endpoint
    $headers = @{ 'X-XRE-Session-Token' = [string]$manifest.sessionToken }

    $recipeId = Load-Recipe (New-Recipe "mcp-clean-$suffix" 30 120)
    $prepared = Invoke-Tool 'prepare_render_profile' @{ recipe_id = $recipeId }
    $profileId = [string]$prepared.session_id
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($profileId)) 'Prepare returned no session id.'
    $ready = Invoke-Tool 'wait_render_profile_ready' @{ session_id = $profileId; timeout_seconds = 120 }
    Assert-Condition ($ready.state -eq 'Created') 'Profile was not ready to arm.'
    $armed = Invoke-Tool 'arm_render_profile' @{ session_id = $profileId }
    Assert-Condition ($armed.state -eq 'Armed') 'Profile was not armed.'
    [void](Invoke-Tool 'start_render_profile' @{ session_id = $profileId })
    # The transport is suspended during measured capture. Wait on durable files only.
    $paths = Wait-ResultFiles 1 120 $profileId
    $status = Wait-Status $profileId @('Completed') 30
    Assert-Condition ($status.capturedFrames -eq 20) 'MCP status reported an unexpected frame count.'
    $resultEnvelope = Invoke-Tool 'get_render_profile_result' @{ session_id = $profileId }
    Assert-Condition ($resultEnvelope.capturedFrames -eq 20) 'MCP result reported an unexpected frame count.'
    $resultPath = [string]$resultEnvelope.artifacts.result
    [void](Assert-Result $resultPath 20)
    $checks.Add('clean 20-frame capture, output, gates, and zero allocations')

    $readyCancelId = Load-Recipe (New-Recipe "mcp-ready-cancel-$suffix" 30 120)
    $readyCancelPrepared = Invoke-Tool 'prepare_render_profile' @{ recipe_id = $readyCancelId }
    $readyCancelSessionId = [string]$readyCancelPrepared.session_id
    $readyCancelStatus = Invoke-Tool 'wait_render_profile_ready' `
        @{ session_id = $readyCancelSessionId; timeout_seconds = 120 }
    Assert-Condition ($readyCancelStatus.state -eq 'Created') 'Ready-cancel profile did not reach Created.'
    [void](Invoke-Tool 'cancel_render_profile' @{ session_id = $readyCancelSessionId })
    $readyCancelStatus = Wait-Status $readyCancelSessionId @('Cancelled') 30
    Assert-Condition ($readyCancelStatus.capturedFrames -eq 0) 'Ready-cancel captured frames.'
    $checks.Add('cancellation after ready and before arm')

    $armedCancelId = Load-Recipe (New-Recipe "mcp-armed-cancel-$suffix" 30 120)
    $armedCancelPrepared = Invoke-Tool 'prepare_render_profile' @{ recipe_id = $armedCancelId }
    $armedCancelSessionId = [string]$armedCancelPrepared.session_id
    $armedCancelReady = Invoke-Tool 'wait_render_profile_ready' `
        @{ session_id = $armedCancelSessionId; timeout_seconds = 120 }
    Assert-Condition ($armedCancelReady.state -eq 'Created') 'Armed-cancel profile did not reach Created.'
    $armedCancelStatus = Invoke-Tool 'arm_render_profile' @{ session_id = $armedCancelSessionId }
    Assert-Condition ($armedCancelStatus.state -eq 'Armed') 'Armed-cancel profile did not reach Armed.'
    [void](Invoke-Tool 'cancel_render_profile' @{ session_id = $armedCancelSessionId })
    $armedCancelStatus = Wait-Status $armedCancelSessionId @('Cancelled') 30
    Assert-Condition ($armedCancelStatus.capturedFrames -eq 0) 'Armed-cancel captured frames.'
    $checks.Add('cancellation while armed without measured frames')

    $parkedTimeoutRecipe = New-Recipe "mcp-parked-timeout-$suffix" 0 5
    $parkedTimeoutRecipe.stability_frames = 1
    $parkedTimeoutId = Load-Recipe $parkedTimeoutRecipe
    $parkedTimeoutPrepared = Invoke-Tool 'prepare_render_profile' @{ recipe_id = $parkedTimeoutId }
    $parkedTimeoutSessionId = [string]$parkedTimeoutPrepared.session_id
    $parkedReady = Invoke-Tool 'wait_render_profile_ready' `
        @{ session_id = $parkedTimeoutSessionId; timeout_seconds = 5 }
    Assert-Condition ($parkedReady.state -eq 'Created') 'Parked-timeout profile did not reach Created.'
    $parkedStatus = Wait-Status $parkedTimeoutSessionId @('Failed') 15
    Assert-Condition ($parkedStatus.capturedFrames -eq 0 -and
        [string]$parkedStatus.error -match 'timed out') 'Parked profile failed without a timeout or captured frames.'
    $checks.Add('five-second timeout while ready and parked')

    $cancelId = Load-Recipe (New-Recipe "mcp-cancel-$suffix" 1000000 120)
    $cancelPrepared = Invoke-Tool 'prepare_render_profile' @{ recipe_id = $cancelId }
    [void](Invoke-Tool 'cancel_render_profile' @{ session_id = [string]$cancelPrepared.session_id })
    $cancelStatus = Wait-Status ([string]$cancelPrepared.session_id) @('Cancelled') 30
    $checks.Add('cancellation during long warmup')

    $timeoutId = Load-Recipe (New-Recipe "mcp-timeout-$suffix" 1000000 1)
    $timeoutPrepared = Invoke-Tool 'prepare_render_profile' @{ recipe_id = $timeoutId }
    $timeoutStatus = Wait-Status ([string]$timeoutPrepared.session_id) @('Failed') 30
    Assert-Condition ([string]$timeoutStatus.error -match 'timed out') 'Long warmup failed for a reason other than timeout.'
    $checks.Add('one-second timeout during long warmup')

    $invalid = New-Recipe "mcp-invalid-$suffix" 30 120
    $invalid.component = 'MissingRequiredTarget'
    [void](Invoke-Tool 'load_render_profile_recipe' @{ recipe_json = ($invalid | ConvertTo-Json -Depth 32 -Compress) } $true)
    $checks.Add('invalid required target rejected')

    $matrixId = Load-Recipe (New-Recipe "mcp-matrix-$suffix" 30 120 @(1, 2))
    $matrix = Invoke-Tool 'run_render_profile_matrix' @{ recipe_id = $matrixId }
    Assert-Condition ($matrix.totalVariants -eq 2) 'Matrix did not declare two variants.'
    # Both child results must be durable before querying a transport that pauses for each capture.
    $paths = Wait-ResultFiles 3 240 '' ([string]$matrix.jobId)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        try { $matrixStatus = Invoke-Tool 'get_render_profile_matrix_status' @{ job_id = [string]$matrix.jobId } }
        catch { $matrixStatus = $null }
        if ($null -ne $matrixStatus -and $matrixStatus.state -in @('Completed', 'Failed', 'Cancelled')) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    Assert-Condition ($null -ne $matrixStatus -and $matrixStatus.state -eq 'Completed' -and
        $matrixStatus.completedVariants -eq 2) 'Worker matrix did not complete both variants.'
    foreach ($childId in $matrixStatus.sessionIds) {
        $child = Invoke-Tool 'get_render_profile_result' @{ session_id = [string]$childId }
        [void](Assert-Result ([string]$child.artifacts.result) 20)
    }
    $checks.Add('worker-count matrix 1,2')

    # Only the owned session manifest is modified; each probe restores its exact original bytes.
    $originalManifestBytes = [IO.File]::ReadAllBytes($manifestPath)
    try {
        $forged = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -Depth 32
        $forged.processStartTimeUtc = '1970-01-01T00:00:00.0000000Z'
        $forged | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
        [void](& $manager Stop -Name $sessionName -AsJson)
        Assert-Condition ($null -ne (Get-Process -Id ([int]$manifest.processId) -ErrorAction SilentlyContinue)) `
            'Stopping a forged start-time manifest terminated the owned process.'
        $checks.Add('PID/start-time mismatch cannot stop process')
        [IO.File]::WriteAllBytes($manifestPath, $originalManifestBytes)

        $forged = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -Depth 32
        $forged.renderBenchPath = Join-Path (Split-Path -Parent ([string]$manifest.renderBenchPath)) `
            'foreign-render-bench.dll'
        $forged | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
        [void](& $manager Stop -Name $sessionName -AsJson)
        Assert-Condition ($null -ne (Get-Process -Id ([int]$manifest.processId) -ErrorAction SilentlyContinue)) `
            'Stopping a forged executable manifest terminated the owned process.'
        $checks.Add('foreign executable path cannot stop process')
        [IO.File]::WriteAllBytes($manifestPath, $originalManifestBytes)

        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start()
        try {
            $forged = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json -Depth 32
            $forged.processStartTimeUtc = '1970-01-01T00:00:00.0000000Z'
            $forged | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
            $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
            $occupiedFailed = $false
            try {
                [void](& $manager Start -Name $sessionName -NoBuild -Configuration Release -Platform AnyCPU `
                    -ExecutionMode Component -OutputDirectory (Join-Path $reportDirectory 'occupied-evidence') `
                    -Port $port -AsJson)
            }
            catch { $occupiedFailed = $_.Exception.Message -match 'unavailable' }
            Assert-Condition $occupiedFailed 'Occupied-port session start did not fail before launch.'
            $occupiedManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            Assert-Condition ($null -eq $occupiedManifest.processId) 'Occupied-port start spawned a process.'
            Assert-Condition ($null -ne (Get-Process -Id ([int]$manifest.processId) -ErrorAction SilentlyContinue)) `
                'Occupied-port probe terminated the original owned process.'
        }
        finally { $listener.Stop() }
        $checks.Add('occupied port fails before process launch')
    }
    finally { [IO.File]::WriteAllBytes($manifestPath, $originalManifestBytes) }

    $report = [ordered]@{ schemaVersion = 1; generatedUtc = [DateTimeOffset]::UtcNow;
        sessionName = $sessionName; checks = @($checks); status = 'pass'; resultPath = $resultPath }
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $reportDirectory 'render-profile-mcp-smoke.json') -Encoding utf8NoBOM
    $report | ConvertTo-Json -Depth 10
}
finally {
    if ($started) {
        try {
            [void](& $manager Stop -Name $sessionName -AsJson)
            $stopped = $true
        }
        catch { Write-Warning "Named RenderBench session could not be stopped: $($_.Exception.Message)" }
    }
    if ($stopped) {
        $logsSource = Join-Path $sessionRoot 'logs'
        if (Test-Path -LiteralPath $logsSource -PathType Container) {
            Copy-Item -LiteralPath $logsSource -Destination (Join-Path $reportDirectory 'session-logs') -Recurse -Force
        }
        try { Safe-RemoveOwnSession $sessionRoot $sessionName }
        catch { Write-Warning "Owned session artifacts were retained: $($_.Exception.Message)" }
    }
}
