[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Start', 'Run', 'Status', 'Stop', 'List')]
    [string]$Action = 'Status',

    [Parameter(Position = 1)]
    [string]$Name,

    [ValidateRange(0, 65535)]
    [int]$Port = 0,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('AnyCPU', 'x64')]
    [string]$Platform = 'AnyCPU',

    [ValidateSet('Presentationless', 'Component')]
    [string]$ExecutionMode = 'Presentationless',

    [ValidateRange(1, 16384)]
    [int]$Width = 1920,

    [ValidateRange(1, 16384)]
    [int]$Height = 1080,

    [ValidateRange(0, 1000000)]
    [int]$WarmupFrames = 30,

    [ValidateRange(1, 1000000)]
    [int]$StabilityFrames = 5,

    [ValidateRange(1, 1000000)]
    [int]$CaptureFrames = 120,

    [string]$OutputDirectory,

    [switch]$NoBuild,
    [switch]$AsJson
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$agentValidationRoot = Join-Path $repoRoot 'Build\_AgentValidation'
$sessionsRoot = Join-Path $agentValidationRoot '00000000-000000-shared\mcp-sessions'
$projectPath = Join-Path $repoRoot 'XREngine.RenderBench\XREngine.RenderBench.csproj'

function Assert-Name {
    if ([string]::IsNullOrWhiteSpace($Name)) {
        throw "Action '$Action' requires a session name."
    }
    if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$') {
        throw "Session names must use 1-64 letters, digits, '.', '_', or '-'."
    }
}

function Get-SessionRoot([string]$sessionName) {
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $sessionsRoot "renderbench-$sessionName"))
    $prefix = [System.IO.Path]::GetFullPath($sessionsRoot) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Resolved session root escapes the RenderBench sessions directory."
    }
    return $candidate
}

function Get-ManifestPath([string]$sessionName) {
    return Join-Path (Get-SessionRoot $sessionName) 'session.json'
}

function Read-Manifest([string]$sessionName) {
    $path = Get-ManifestPath $sessionName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Write-Manifest($manifest) {
    $root = Get-SessionRoot $manifest.name
    [System.IO.Directory]::CreateDirectory($root) | Out-Null
    $path = Join-Path $root 'session.json'
    $temporary = "$path.tmp-$PID"
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $path -Force
}

function Select-Port([int]$requestedPort) {
    $listener = $null
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $requestedPort)
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    catch {
        throw "MCP port '$requestedPort' is unavailable: $($_.Exception.Message)"
    }
    finally {
        if ($null -ne $listener) { $listener.Stop() }
    }
}

function Get-OwnedProcess($manifest) {
    if ($null -eq $manifest -or $null -eq $manifest.processId) { return $null }
    $process = Get-Process -Id ([int]$manifest.processId) -ErrorAction SilentlyContinue
    if ($null -eq $process) { return $null }

    $recordedStart = [DateTime]::Parse([string]$manifest.processStartTimeUtc).ToUniversalTime()
    $actualStart = $process.StartTime.ToUniversalTime()
    if ([Math]::Abs(($actualStart - $recordedStart).TotalSeconds) -gt 2) { return $null }

    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)" -ErrorAction SilentlyContinue
    if ($null -eq $cim -or [string]::IsNullOrWhiteSpace([string]$cim.CommandLine)) { return $null }
    if (([string]$cim.CommandLine).IndexOf([string]$manifest.renderBenchPath, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { return $null }
    return $process
}

function Find-RenderBenchDll([string]$artifactsPath) {
    $matches = @(Get-ChildItem -LiteralPath (Join-Path $artifactsPath 'bin') -Filter 'XREngine.RenderBench.dll' -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/]ref(int)?[\\/]' })
    if ($matches.Count -eq 0) { throw "Could not find XREngine.RenderBench.dll under '$artifactsPath'." }
    return ($matches | Sort-Object FullName | Select-Object -Last 1).FullName
}

function Get-StatusObject($manifest) {
    if ($null -eq $manifest) { return $null }
    $process = Get-OwnedProcess $manifest
    $runtime = $null
    if ($null -ne $process) {
        try {
            $candidateRuntime = Invoke-RestMethod -Method Get -Uri "$($manifest.endpoint)status" -TimeoutSec 2
            if ($candidateRuntime.processId -eq $process.Id -and $candidateRuntime.sessionName -eq $manifest.name) {
                $runtime = $candidateRuntime
            }
        }
        catch { }
    }
    return [pscustomobject][ordered]@{
        name = $manifest.name
        state = if ($null -ne $runtime) { $runtime.phase } elseif ($null -ne $process) { 'CapturingOrUnavailable' } else { $manifest.state }
        processId = if ($null -ne $process) { $process.Id } else { $null }
        port = $manifest.port
        endpoint = $manifest.endpoint
        sessionRoot = $manifest.sessionRoot
        resultPath = if ($null -ne $runtime) { $runtime.resultPath } else { $manifest.resultPath }
        failure = if ($null -ne $runtime) { $runtime.failure } else { $manifest.failure }
        owned = $null -ne $process
    }
}

function Emit($value) {
    if ($AsJson) { $value | ConvertTo-Json -Depth 8 }
    else { $value }
}

function Start-Session {
    Assert-Name
    $existing = Read-Manifest $Name
    if ($null -ne (Get-OwnedProcess $existing)) {
        throw "RenderBench session '$Name' is already running as PID $($existing.processId)."
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        & (Join-Path $repoRoot 'Tools\Limit-AgentValidation.ps1') -ReserveTaskRun | Out-Null
        $safeName = $Name.ToLowerInvariant() -replace '[^a-z0-9-]', '-'
        $OutputDirectory = Join-Path $agentValidationRoot "$(Get-Date -Format 'yyyyMMdd-HHmmss')-renderbench-$safeName"
    }
    $evidencePath = [System.IO.Path]::GetFullPath($OutputDirectory)
    $validationPrefix = [System.IO.Path]::GetFullPath($agentValidationRoot) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $evidencePath.StartsWith($validationPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "RenderBench output must be under '$agentValidationRoot'."
    }
    $sessionRoot = Get-SessionRoot $Name
    $artifactsPath = Join-Path $sessionRoot 'artifacts'
    $logsPath = Join-Path $sessionRoot 'logs'
    [System.IO.Directory]::CreateDirectory($artifactsPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($evidencePath) | Out-Null
    [System.IO.Directory]::CreateDirectory($logsPath) | Out-Null

    $manifest = [pscustomobject][ordered]@{
        schemaVersion = 1
        name = $Name
        state = if ($NoBuild) { 'Preparing' } else { 'Building' }
        configuration = $Configuration
        platform = $Platform
        port = 0
        endpoint = $null
        sessionRoot = $sessionRoot
        artifactsPath = $artifactsPath
        evidencePath = $evidencePath
        renderBenchPath = $null
        processId = $null
        processStartTimeUtc = $null
        sessionToken = [Guid]::NewGuid().ToString('N')
        shutdownEventName = "Local\XRE-RenderBench-$([Guid]::NewGuid().ToString('N'))"
        createdUtc = [DateTime]::UtcNow.ToString('O')
        startedUtc = $null
        stoppedUtc = $null
        resultPath = $null
        failure = $null
    }
    Write-Manifest $manifest

    try {
        if (-not $NoBuild) {
            $buildLog = Join-Path $logsPath 'build.log'
            $buildArguments = @(
                'build', $projectPath,
                '--configuration', $Configuration,
                '--artifacts-path', $artifactsPath,
                "-p:Platform=$Platform",
                '-p:RestoreIgnoreFailedSources=true',
                '-p:XREngineUseExistingNativeBridges=true',
                '-p:UseSharedCompilation=false',
                '/nodeReuse:false',
                '/property:GenerateFullPaths=true',
                '/consoleloggerparameters:NoSummary'
            )
            & dotnet @buildArguments *> $buildLog
            if ($LASTEXITCODE -ne 0) { throw "Isolated RenderBench build failed. See '$buildLog'." }
        }

        $renderBenchPath = Find-RenderBenchDll $artifactsPath
        $manifest.renderBenchPath = $renderBenchPath
        $registryMutex = [System.Threading.Mutex]::new($false, 'Local\XREngine-McpRenderBenchSessionRegistry')
        $registryLockTaken = $false
        try {
            $registryLockTaken = $registryMutex.WaitOne([TimeSpan]::FromSeconds(30))
            if (-not $registryLockTaken) { throw 'Timed out waiting for the RenderBench session registry lock.' }
            $selectedPort = Select-Port $Port
            $manifest.port = $selectedPort
            $manifest.endpoint = "http://localhost:$selectedPort/mcp/"
            $manifest.state = 'Starting'
            Write-Manifest $manifest

        $arguments = @(
            ('"{0}"' -f $renderBenchPath),
            '--backend', 'Vulkan',
            '--execution-mode', $ExecutionMode,
            '--recipe', 'deterministic-clear',
            '--fixture', 'synthetic-clear',
            '--output-dir', ('"{0}"' -f $evidencePath),
            '--width', [string]$Width,
            '--height', [string]$Height,
            '--warmup-frames', [string]$WarmupFrames,
            '--stability-frames', [string]$StabilityFrames,
            '--capture-frames', [string]$CaptureFrames,
            '--fixed-step', ([string](1.0 / 60.0)),
            '--random-seed', '5784133',
            '--frozen-world',
            '--mcp-policy', 'Control',
            '--mcp-port', [string]$selectedPort,
            '--session-token', $manifest.sessionToken,
            '--session-name', $Name,
            '--shutdown-event', $manifest.shutdownEventName,
            '--wait-for-mcp-start'
        )
        $stdoutPath = Join-Path $logsPath 'renderbench.stdout.log'
        $stderrPath = Join-Path $logsPath 'renderbench.stderr.log'
        $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
            $sessionEnvironment = @{
            'XRE_RENDER_BENCH_SESSION_NAME' = $Name
            'XRE_RENDER_BENCH_SESSION_ROOT' = $sessionRoot
            'XRE_AGENT_VALIDATION_RUN_ROOT' = $evidencePath
            'XRE_ENGINE_ASSETS_PATH' = (Join-Path $repoRoot 'Build\CommonAssets')
            'XRE_GAME_ASSETS_PATH' = (Join-Path $repoRoot 'Assets')
            'XRE_GAME_CACHE_PATH' = (Join-Path $sessionRoot 'cache')
            'XRE_GAME_METADATA_PATH' = (Join-Path $sessionRoot 'metadata')
            'XRE_TEXTURE_STREAMING_CACHE_WARMUP_ENABLED' = 'false'
        }
            $previousEnvironment = @{}
            try {
                foreach ($entry in $sessionEnvironment.GetEnumerator()) {
                    $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
                    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
                }
                $process = Start-Process -FilePath $dotnetPath -ArgumentList $arguments -WorkingDirectory $repoRoot -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -WindowStyle Hidden -PassThru
            }
            finally {
                foreach ($entry in $previousEnvironment.GetEnumerator()) {
                    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
                }
            }
        }
        finally {
            if ($registryLockTaken) { $registryMutex.ReleaseMutex() }
            $registryMutex.Dispose()
        }
        $process.Refresh()
        $manifest.processId = $process.Id
        $manifest.processStartTimeUtc = $process.StartTime.ToUniversalTime().ToString('O')
        $manifest.startedUtc = [DateTime]::UtcNow.ToString('O')
        $manifest.state = 'Starting'
        Write-Manifest $manifest

        $runtime = $null
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        do {
            if ($process.HasExited) { throw "RenderBench exited before MCP readiness. See '$stderrPath'." }
            try {
                $runtime = Invoke-RestMethod -Method Get -Uri "$($manifest.endpoint)status" -TimeoutSec 2
                if ($runtime.phase -eq 'Idle' -and $runtime.processId -eq $process.Id -and $runtime.sessionName -eq $Name) { break }
            }
            catch { }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($null -eq $runtime -or $runtime.phase -ne 'Idle' -or $runtime.processId -ne $process.Id -or $runtime.sessionName -ne $Name) { throw "Timed out waiting for identity-matched RenderBench MCP readiness." }

        $manifest.state = 'Idle'
        Write-Manifest $manifest
        Emit (Get-StatusObject $manifest)
    }
    catch {
        $manifest.failure = $_.Exception.Message
        $manifest.state = 'Failed'
        Write-Manifest $manifest
        $owned = Get-OwnedProcess $manifest
        if ($null -ne $owned) { Stop-Process -Id $owned.Id -Force }
        throw
    }
}

function Run-Session {
    Assert-Name
    $manifest = Read-Manifest $Name
    $process = Get-OwnedProcess $manifest
    if ($null -eq $process) { throw "RenderBench session '$Name' is not owned or running." }
    $headers = @{ 'X-XRE-Session-Token' = [string]$manifest.sessionToken }
    $body = @{ jsonrpc = '2.0'; id = 1; method = 'tools/call'; params = @{ name = 'start_render_bench'; arguments = @{} } } | ConvertTo-Json -Depth 6
    $response = Invoke-RestMethod -Method Post -Uri $manifest.endpoint -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 10
    if ($null -ne $response.error) { throw "RenderBench rejected start: $($response.error.message)" }
    $manifest.state = 'Capturing'
    Write-Manifest $manifest

    $runtime = $null
    $deadline = [DateTime]::UtcNow.AddMinutes(10)
    do {
        if ($process.HasExited) { throw "RenderBench exited during the run. See '$($manifest.sessionRoot)\logs'." }
        try {
            $runtime = Invoke-RestMethod -Method Get -Uri "$($manifest.endpoint)status" -TimeoutSec 2
            if ($runtime.phase -in @('Completed', 'Failed')) { break }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $runtime -or $runtime.phase -notin @('Completed', 'Failed')) { throw "Timed out waiting for RenderBench completion." }
    $manifest.state = [string]$runtime.phase
    $manifest.resultPath = [string]$runtime.resultPath
    $manifest.failure = [string]$runtime.failure
    Write-Manifest $manifest
    Emit (Get-StatusObject $manifest)
}

function Stop-Session {
    Assert-Name
    $manifest = Read-Manifest $Name
    if ($null -eq $manifest) { throw "RenderBench session '$Name' does not exist." }
    $process = Get-OwnedProcess $manifest
    if ($null -eq $process) {
        $manifest.state = 'Stopped'
        $manifest.stoppedUtc = [DateTime]::UtcNow.ToString('O')
        Write-Manifest $manifest
        Emit (Get-StatusObject $manifest)
        return
    }

    try {
        $headers = @{ 'X-XRE-Session-Token' = [string]$manifest.sessionToken }
        Invoke-RestMethod -Method Post -Uri "$($manifest.endpoint)shutdown" -Headers $headers -TimeoutSec 5 | Out-Null
    }
    catch {
        try {
            $shutdownEvent = [System.Threading.EventWaitHandle]::OpenExisting([string]$manifest.shutdownEventName)
            try { $shutdownEvent.Set() | Out-Null }
            finally { $shutdownEvent.Dispose() }
        }
        catch { }
    }
    if (-not $process.WaitForExit(30000)) {
        $owned = Get-OwnedProcess $manifest
        if ($null -eq $owned) { throw "PID ownership changed while stopping RenderBench session '$Name'." }
        Stop-Process -Id $owned.Id -Force
        $owned.WaitForExit(5000) | Out-Null
    }
    $manifest.state = 'Stopped'
    $manifest.stoppedUtc = [DateTime]::UtcNow.ToString('O')
    Write-Manifest $manifest
    Emit (Get-StatusObject $manifest)
}

switch ($Action) {
    'Start' { Start-Session }
    'Run' { Run-Session }
    'Stop' { Stop-Session }
    'Status' { Assert-Name; Emit (Get-StatusObject (Read-Manifest $Name)) }
    'List' {
        if (-not (Test-Path -LiteralPath $sessionsRoot)) { Emit @(); break }
        $items = @(Get-ChildItem -LiteralPath $sessionsRoot -Directory | ForEach-Object { Get-StatusObject (Read-Manifest $_.Name) })
        Emit $items
    }
}
