[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('Start', 'Stop', 'Status', 'List', 'Remove')]
    [string]$Action = 'List',

    [Parameter(Position = 1)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Name,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('AnyCPU', 'x64')]
    [string]$Platform = 'AnyCPU',

    [ValidateRange(0, 65535)]
    [int]$Port = 0,

    [ValidateRange(1, 3600)]
    [int]$StartupTimeoutSeconds = 120,

    [ValidateRange(1, 300)]
    [int]$StopTimeoutSeconds = 15,

    [ValidateSet('AlwaysAsk', 'AllowReadOnly', 'AllowMutate', 'AllowDestructive', 'AllowAll')]
    [string]$PermissionPolicy = 'AllowAll',

    [string[]]$EditorArguments = @(),

    [hashtable]$SessionEnvironment = @{},

    [switch]$NoBuild,
    [switch]$NoWait,
    [switch]$NoUnitTesting,
    [switch]$Force,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sessionsRoot = Join-Path $repoRoot 'Build\_AgentValidation\mcp-sessions'
$registryLockPath = Join-Path $sessionsRoot '.registry.lock'
$manifestFileName = 'session.json'
$defaultMcpPort = 5467
$maximumPortProbeCount = 200

function Assert-SessionNameRequired {
    if ([string]::IsNullOrWhiteSpace($Name)) {
        throw "-Name is required for the '$Action' action."
    }
}

function Get-SessionRoot([string]$SessionName) {
    return Join-Path $sessionsRoot $SessionName
}

function Get-ManifestPath([string]$SessionName) {
    return Join-Path (Get-SessionRoot $SessionName) $manifestFileName
}

function Read-JsonFileShared([string]$Path) {
    $fileShare = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        $fileShare)
    try {
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $true, 4096, $true)
        try {
            return $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Read-SessionManifest([string]$SessionName) {
    $path = Get-ManifestPath $SessionName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }

    try {
        return Read-JsonFileShared $path
    }
    catch {
        throw "Session manifest '$path' is unreadable: $($_.Exception.Message)"
    }
}

function Write-SessionManifest($Manifest) {
    $sessionRoot = Get-SessionRoot ([string]$Manifest.name)
    [System.IO.Directory]::CreateDirectory($sessionRoot) | Out-Null
    $path = Join-Path $sessionRoot $manifestFileName
    $temporaryPath = "$path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    $backupPath = "$path.$PID.$([Guid]::NewGuid().ToString('N')).bak"
    $json = $Manifest | ConvertTo-Json -Depth 8
    try {
        [System.IO.File]::WriteAllText($temporaryPath, $json, [System.Text.UTF8Encoding]::new($false))
        if ([System.IO.File]::Exists($path)) {
            $replaced = $false
            for ($attempt = 0; $attempt -lt 40 -and -not $replaced; $attempt++) {
                try {
                    [System.IO.File]::Replace($temporaryPath, $path, $backupPath)
                    $replaced = $true
                }
                catch [System.IO.IOException] {
                    if ($attempt -eq 39) {
                        throw
                    }
                    Start-Sleep -Milliseconds 50
                }
            }
        }
        else {
            [System.IO.File]::Move($temporaryPath, $path)
        }
    }
    finally {
        if ([System.IO.File]::Exists($temporaryPath)) {
            [System.IO.File]::Delete($temporaryPath)
        }
        if ([System.IO.File]::Exists($backupPath)) {
            [System.IO.File]::Delete($backupPath)
        }
    }
}

function Invoke-WithRegistryLock([scriptblock]$Body) {
    [System.IO.Directory]::CreateDirectory($sessionsRoot) | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $stream = $null

    while ($null -eq $stream) {
        try {
            $stream = [System.IO.File]::Open(
                $registryLockPath,
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
        }
        catch [System.IO.IOException] {
            if ([DateTime]::UtcNow -ge $deadline) {
                throw 'Timed out waiting for the MCP editor session registry lock.'
            }
            Start-Sleep -Milliseconds 100
        }
    }

    try {
        return & $Body
    }
    finally {
        $stream.Dispose()
    }
}

function Get-ProcessStartTimeUtc($Process) {
    try {
        return $Process.StartTime.ToUniversalTime()
    }
    catch {
        return $null
    }
}

function Get-OwnedEditorProcess($Manifest) {
    if ($null -eq $Manifest -or $null -eq $Manifest.processId) {
        return $null
    }

    $processId = 0
    if (-not [int]::TryParse([string]$Manifest.processId, [ref]$processId) -or $processId -le 0) {
        return $null
    }

    $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $null
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$Manifest.processStartTimeUtc)) {
        $expectedStart = [DateTime]::Parse(
            [string]$Manifest.processStartTimeUtc,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        $actualStart = Get-ProcessStartTimeUtc $process
        if ($null -eq $actualStart -or [Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -gt 2) {
            return $null
        }
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$Manifest.editorPath)) {
        try {
            $reportedProcessPath = if ($process.PSObject.Properties.Name -contains 'Path' -and -not [string]::IsNullOrWhiteSpace([string]$process.Path)) {
                [string]$process.Path
            }
            else {
                [string]$process.MainModule.FileName
            }
            $actualPath = [System.IO.Path]::GetFullPath($reportedProcessPath)
            $expectedPath = [System.IO.Path]::GetFullPath([string]$Manifest.editorPath)
            if (-not $actualPath.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
                return $null
            }
        }
        catch {
            return $null
        }
    }

    return $process
}

function Test-LauncherAlive($Manifest) {
    if ($null -eq $Manifest -or $null -eq $Manifest.launcherProcessId) {
        return $false
    }

    $launcherProcessId = 0
    if (-not [int]::TryParse([string]$Manifest.launcherProcessId, [ref]$launcherProcessId) -or $launcherProcessId -le 0) {
        return $false
    }

    $launcher = Get-Process -Id $launcherProcessId -ErrorAction SilentlyContinue
    if ($null -eq $launcher) {
        return $false
    }

    if (-not [string]::IsNullOrWhiteSpace([string]$Manifest.launcherProcessStartTimeUtc)) {
        $expectedStart = [DateTime]::Parse(
            [string]$Manifest.launcherProcessStartTimeUtc,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()
        $actualStart = Get-ProcessStartTimeUtc $launcher
        return $null -ne $actualStart -and [Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -le 2
    }

    return $true
}

function Test-ManifestReservesPort($Manifest) {
    if ($null -eq $Manifest -or $null -eq $Manifest.port) {
        return $false
    }

    if ($null -ne (Get-OwnedEditorProcess $Manifest)) {
        return $true
    }

    $preLaunchStates = @('Preparing', 'Building', 'Starting')
    return $preLaunchStates -contains [string]$Manifest.state -and (Test-LauncherAlive $Manifest)
}

function Test-TcpPortAvailable([int]$CandidatePort) {
    $listener = $null
    try {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $CandidatePort)
        $listener.Start()
        return $true
    }
    catch [System.Net.Sockets.SocketException] {
        return $false
    }
    finally {
        if ($null -ne $listener) {
            $listener.Stop()
        }
    }
}

function Select-McpPort([int]$RequestedPort) {
    $reservedPorts = [System.Collections.Generic.HashSet[int]]::new()
    if (Test-Path -LiteralPath $sessionsRoot -PathType Container) {
        foreach ($sessionDirectory in Get-ChildItem -LiteralPath $sessionsRoot -Directory -ErrorAction SilentlyContinue) {
            try {
                $manifestPath = Join-Path $sessionDirectory.FullName $manifestFileName
                if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
                    continue
                }
                $manifest = Read-JsonFileShared $manifestPath
                if (Test-ManifestReservesPort $manifest) {
                    [void]$reservedPorts.Add([int]$manifest.port)
                }
            }
            catch {
                # A corrupt or concurrently written stale manifest must not block every session.
            }
        }
    }

    $firstPort = if ($RequestedPort -gt 0) { $RequestedPort } else { $defaultMcpPort }
    $probeCount = if ($RequestedPort -gt 0) { 1 } else { $maximumPortProbeCount }
    for ($offset = 0; $offset -lt $probeCount; $offset++) {
        $candidate = $firstPort + $offset
        if ($candidate -gt 65535) {
            break
        }
        if ($reservedPorts.Contains($candidate)) {
            continue
        }
        if (Test-TcpPortAvailable $candidate) {
            return $candidate
        }
    }

    if ($RequestedPort -gt 0) {
        throw "MCP port $RequestedPort is already reserved or in use."
    }
    throw "No free MCP port was found in the range $defaultMcpPort-$($defaultMcpPort + $maximumPortProbeCount - 1)."
}

function Find-EditorExecutable([string]$ArtifactsPath, [string]$BuildConfiguration) {
    $editorBinRoot = Join-Path $ArtifactsPath 'bin\XREngine.Editor'
    if (-not (Test-Path -LiteralPath $editorBinRoot -PathType Container)) {
        throw "The isolated build did not create '$editorBinRoot'."
    }

    $configurationToken = $BuildConfiguration.ToLowerInvariant()
    $candidates = @(Get-ChildItem -LiteralPath $editorBinRoot -Filter 'XREngine.Editor.exe' -File -Recurse |
        Where-Object { $_.FullName.ToLowerInvariant().Contains($configurationToken) } |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($candidates.Count -eq 0) {
        $candidates = @(Get-ChildItem -LiteralPath $editorBinRoot -Filter 'XREngine.Editor.exe' -File -Recurse |
            Sort-Object LastWriteTimeUtc -Descending)
    }
    if ($candidates.Count -eq 0) {
        throw "The isolated build completed without producing XREngine.Editor.exe below '$editorBinRoot'."
    }

    return $candidates[0].FullName
}

function ConvertTo-NativeArgument([string]$Value) {
    if ($null -eq $Value -or $Value.Length -eq 0) {
        return '""'
    }
    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $backslashCount = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashCount++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append('\' * (($backslashCount * 2) + 1))
            [void]$builder.Append('"')
            $backslashCount = 0
            continue
        }
        if ($backslashCount -gt 0) {
            [void]$builder.Append('\' * $backslashCount)
            $backslashCount = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashCount -gt 0) {
        [void]$builder.Append('\' * ($backslashCount * 2))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Start-ProcessWithEnvironment(
    [string]$FilePath,
    [string[]]$Arguments,
    [hashtable]$EnvironmentVariables,
    [string]$WorkingDirectory,
    [string]$StandardOutputPath,
    [string]$StandardErrorPath) {

    $previousValues = @{}
    try {
        foreach ($entry in $EnvironmentVariables.GetEnumerator()) {
            $variableName = [string]$entry.Key
            $previousValues[$variableName] = [Environment]::GetEnvironmentVariable($variableName, [EnvironmentVariableTarget]::Process)
            [Environment]::SetEnvironmentVariable($variableName, [string]$entry.Value, [EnvironmentVariableTarget]::Process)
        }

        $nativeArguments = @($Arguments | ForEach-Object { ConvertTo-NativeArgument ([string]$_) })
        $startProcessParameters = @{
            FilePath = $FilePath
            ArgumentList = $nativeArguments
            WorkingDirectory = $WorkingDirectory
            RedirectStandardOutput = $StandardOutputPath
            RedirectStandardError = $StandardErrorPath
            PassThru = $true
        }
        return Start-Process @startProcessParameters
    }
    finally {
        foreach ($entry in $previousValues.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable([string]$entry.Key, $entry.Value, [EnvironmentVariableTarget]::Process)
        }
    }
}

function Get-McpStatus([string]$Endpoint, [int]$TimeoutSeconds = 2) {
    $statusUri = $Endpoint.TrimEnd('/') + '/status'
    return Invoke-RestMethod -Method Get -Uri $statusUri -TimeoutSec $TimeoutSeconds
}

function Initialize-SessionMetadata([string]$SessionRoot) {
    $source = Join-Path $repoRoot 'Metadata'
    $destination = Join-Path $SessionRoot 'metadata'
    if (-not (Test-Path -LiteralPath $source -PathType Container)) {
        return
    }

    $destinationHasEntries = (Test-Path -LiteralPath $destination -PathType Container) -and
        $null -ne (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)
    if ($destinationHasEntries) {
        return
    }

    [System.IO.Directory]::CreateDirectory($destination) | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $destination -Recurse -Force
}

function New-SessionView($Manifest, [switch]$ProbeMcp) {
    $process = Get-OwnedEditorProcess $Manifest
    $state = [string]$Manifest.state
    $mcpReady = $false

    if ($null -ne $process) {
        if ($ProbeMcp -and -not [string]::IsNullOrWhiteSpace([string]$Manifest.endpoint)) {
            try {
                $status = Get-McpStatus ([string]$Manifest.endpoint) 1
                $mcpReady = [bool]$status.isRunning
            }
            catch {
                $mcpReady = $false
            }
        }
        if ($mcpReady) {
            $state = 'Ready'
        }
        elseif ($state -notin @('Starting', 'Ready')) {
            $state = 'Running'
        }
    }
    elseif ($state -in @('Ready', 'Running', 'Starting', 'Stopping')) {
        $state = 'Stopped'
    }

    return [pscustomobject][ordered]@{
        Name = [string]$Manifest.name
        State = $state
        McpReady = $mcpReady
        ProcessId = if ($null -ne $process) { $process.Id } else { $null }
        Port = if ($null -ne $Manifest.port) { [int]$Manifest.port } else { $null }
        Endpoint = [string]$Manifest.endpoint
        Root = Get-SessionRoot ([string]$Manifest.name)
        Artifacts = [string]$Manifest.artifactsPath
        Editor = [string]$Manifest.editorPath
        Logs = Join-Path (Get-SessionRoot ([string]$Manifest.name)) 'logs'
        StartedUtc = [string]$Manifest.startedUtc
    }
}

function Write-Result($Value) {
    if ($AsJson) {
        $Value | ConvertTo-Json -Depth 8
    }
    else {
        $Value
    }
}

function Start-Session {
    Assert-SessionNameRequired
    foreach ($argument in $EditorArguments) {
        if ($argument -ieq '--mcp-port' -or $argument -like '--mcp-port=*') {
            throw 'Do not pass --mcp-port through -EditorArguments; use the session -Port parameter.'
        }
    }

    $sessionRoot = Get-SessionRoot $Name
    $artifactsPath = Join-Path $sessionRoot 'artifacts'
    $buildLogPath = Join-Path $sessionRoot 'build.log'
    $stdoutPath = Join-Path $sessionRoot 'editor.stdout.log'
    $stderrPath = Join-Path $sessionRoot 'editor.stderr.log'
    $editorProject = Join-Path $repoRoot 'XREngine.Editor\XREngine.Editor.csproj'

    $manifest = Invoke-WithRegistryLock {
        $existing = Read-SessionManifest $Name
        if ($null -ne $existing) {
            if ($null -ne (Get-OwnedEditorProcess $existing)) {
                throw "MCP editor session '$Name' is already running as PID $($existing.processId)."
            }
            if (($existing.state -in @('Preparing', 'Building', 'Starting')) -and (Test-LauncherAlive $existing)) {
                throw "MCP editor session '$Name' is already being started by PID $($existing.launcherProcessId)."
            }
        }

        $selectedPort = Select-McpPort $Port
        $created = [pscustomobject][ordered]@{
            schemaVersion = 1
            name = $Name
            state = if ($NoBuild) { 'Preparing' } else { 'Building' }
            configuration = $Configuration
            platform = $Platform
            port = $selectedPort
            endpoint = "http://localhost:$selectedPort/mcp/"
            sessionRoot = $sessionRoot
            artifactsPath = $artifactsPath
            editorPath = $null
            processId = $null
            processStartTimeUtc = $null
            launcherProcessId = $PID
            launcherProcessStartTimeUtc = (Get-ProcessStartTimeUtc (Get-Process -Id $PID)).ToString('O')
            createdUtc = [DateTime]::UtcNow.ToString('O')
            startedUtc = $null
            stoppedUtc = $null
            buildCompletedUtc = $null
            failure = $null
            editorArguments = @()
        }
        Write-SessionManifest $created
        return $created
    }

    [System.IO.Directory]::CreateDirectory($sessionRoot) | Out-Null

    try {
        if (-not $NoBuild) {
            if (-not $AsJson) {
                Write-Host "Building isolated MCP editor session '$Name' into '$artifactsPath'..." -ForegroundColor Cyan
            }
            $buildArguments = @(
                'build',
                $editorProject,
                '--configuration', $Configuration,
                '--artifacts-path', $artifactsPath,
                "-p:Platform=$Platform",
                '-p:RestoreIgnoreFailedSources=true',
                '-p:XREngineUseExistingNativeBridges=true',
                '/property:GenerateFullPaths=true',
                '/consoleloggerparameters:NoSummary'
            )

            if ($AsJson) {
                & dotnet @buildArguments *> $buildLogPath
            }
            else {
                & dotnet @buildArguments 2>&1 | Tee-Object -FilePath $buildLogPath
            }
            $buildExitCode = $LASTEXITCODE
            if ($buildExitCode -ne 0) {
                throw "The isolated editor build failed with exit code $buildExitCode. See '$buildLogPath'."
            }
            $manifest.buildCompletedUtc = [DateTime]::UtcNow.ToString('O')
        }

        $editorPath = Find-EditorExecutable $artifactsPath $Configuration
        $manifest.editorPath = $editorPath
        $manifest.state = 'Starting'

        $launchArguments = [System.Collections.Generic.List[string]]::new()
        if (-not $NoUnitTesting) {
            $launchArguments.Add('--unit-testing')
        }
        $launchArguments.Add('--mcp')
        $launchArguments.Add('--mcp-permission-policy')
        $launchArguments.Add($PermissionPolicy)
        $launchArguments.Add('--mcp-port')
        $launchArguments.Add([string]$manifest.port)
        foreach ($argument in $EditorArguments) {
            $launchArguments.Add([string]$argument)
        }
        $manifest.editorArguments = @($launchArguments)
        Write-SessionManifest $manifest

        Initialize-SessionMetadata $sessionRoot

        $environmentVariables = @{
            'XRE_EDITOR_SESSION_NAME' = $Name
            'XRE_EDITOR_SESSION_ROOT' = $sessionRoot
            'XRE_AGENT_VALIDATION_RUN_ROOT' = $sessionRoot
            'XRE_ENGINE_ASSETS_PATH' = (Join-Path $repoRoot 'Build\CommonAssets')
            'XRE_GAME_ASSETS_PATH' = (Join-Path $repoRoot 'Assets')
            'XRE_GAME_CACHE_PATH' = (Join-Path $sessionRoot 'cache')
            'XRE_GAME_METADATA_PATH' = (Join-Path $sessionRoot 'metadata')
            'XRE_NET_MODE' = 'Local'
            'XRE_WINDOW_TITLE' = "XRE Editor [MCP: $Name @ $($manifest.port)]"
        }
        if (-not $NoUnitTesting) {
            $environmentVariables['XRE_WORLD_MODE'] = 'UnitTesting'
        }
        foreach ($entry in $SessionEnvironment.GetEnumerator()) {
            $environmentVariables[[string]$entry.Key] = [string]$entry.Value
        }

        $startEditorParameters = @{
            FilePath = $editorPath
            Arguments = @($launchArguments)
            EnvironmentVariables = $environmentVariables
            WorkingDirectory = $repoRoot
            StandardOutputPath = $stdoutPath
            StandardErrorPath = $stderrPath
        }
        $process = Start-ProcessWithEnvironment @startEditorParameters

        $processStartTimeUtc = Get-ProcessStartTimeUtc $process
        $manifest.processId = $process.Id
        $manifest.processStartTimeUtc = if ($null -ne $processStartTimeUtc) { $processStartTimeUtc.ToString('O') } else { $null }
        $manifest.startedUtc = [DateTime]::UtcNow.ToString('O')
        Write-SessionManifest $manifest

        if (-not $NoWait) {
            $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
            do {
                $process.Refresh()
                if ($process.HasExited) {
                    $manifest.state = 'Failed'
                    $manifest.failure = "Editor exited with code $($process.ExitCode) before MCP became ready."
                    Write-SessionManifest $manifest
                    throw "$($manifest.failure) See '$stderrPath' and '$($sessionRoot)\logs'."
                }

                try {
                    $status = Get-McpStatus ([string]$manifest.endpoint) 2
                    $reportedSessionName = [string]$status.editorSession.name
                    if ([bool]$status.isRunning -and $reportedSessionName -eq $Name) {
                        $manifest.state = 'Ready'
                        $manifest.failure = $null
                        Write-SessionManifest $manifest
                        Write-Result (New-SessionView $manifest -ProbeMcp)
                        return
                    }
                }
                catch {
                    # Startup commonly refuses connections until the first editor window is initialized.
                }
                Start-Sleep -Milliseconds 500
            } while ([DateTime]::UtcNow -lt $deadline)

            $manifest.state = 'Starting'
            $manifest.failure = "MCP was not ready within $StartupTimeoutSeconds seconds; the editor process is still running."
            Write-SessionManifest $manifest
            if (-not $AsJson) {
                Write-Warning $manifest.failure
            }
        }

        Write-Result (New-SessionView $manifest -ProbeMcp)
    }
    catch {
        if ($null -eq (Get-OwnedEditorProcess $manifest)) {
            $manifest.state = 'Failed'
            $manifest.failure = $_.Exception.Message
            Write-SessionManifest $manifest
        }
        throw
    }
}

function Stop-Session {
    Assert-SessionNameRequired
    $manifest = Read-SessionManifest $Name
    if ($null -eq $manifest) {
        throw "MCP editor session '$Name' does not exist."
    }

    $process = Get-OwnedEditorProcess $manifest
    if ($null -eq $process) {
        $manifest.state = 'Stopped'
        $manifest.stoppedUtc = [DateTime]::UtcNow.ToString('O')
        Write-SessionManifest $manifest
        Write-Result (New-SessionView $manifest)
        return
    }

    $manifest.state = 'Stopping'
    Write-SessionManifest $manifest
    $closedMainWindow = $false
    if (-not $Force) {
        try {
            $closedMainWindow = $process.CloseMainWindow()
        }
        catch {
            $closedMainWindow = $false
        }
    }

    if ($closedMainWindow) {
        [void]$process.WaitForExit($StopTimeoutSeconds * 1000)
    }
    $process.Refresh()

    if (-not $process.HasExited) {
        # The apphost does not always publish the GLFW window as MainWindowHandle.
        # Session ownership has already been verified by executable path, PID, and start time.
        Stop-Process -Id $process.Id -Force
        [void]$process.WaitForExit($StopTimeoutSeconds * 1000)
        $process.Refresh()
        if (-not $process.HasExited) {
            $manifest.state = 'Running'
            Write-SessionManifest $manifest
            throw "Session '$Name' PID $($process.Id) did not terminate within $StopTimeoutSeconds seconds."
        }
    }

    $manifest.state = 'Stopped'
    $manifest.stoppedUtc = [DateTime]::UtcNow.ToString('O')
    Write-SessionManifest $manifest
    Write-Result (New-SessionView $manifest)
}

function Get-SessionStatus {
    Assert-SessionNameRequired
    $manifest = Read-SessionManifest $Name
    if ($null -eq $manifest) {
        throw "MCP editor session '$Name' does not exist."
    }
    Write-Result (New-SessionView $manifest -ProbeMcp)
}

function Get-SessionList {
    if (-not (Test-Path -LiteralPath $sessionsRoot -PathType Container)) {
        if ($AsJson) {
            Write-Output '[]'
        }
        return
    }

    $views = @()
    foreach ($directory in Get-ChildItem -LiteralPath $sessionsRoot -Directory | Sort-Object Name) {
        $manifest = Read-SessionManifest $directory.Name
        if ($null -ne $manifest) {
            $views += New-SessionView $manifest -ProbeMcp
        }
    }
    if ($views.Count -eq 0) {
        if ($AsJson) {
            Write-Output '[]'
        }
        return
    }
    Write-Result $views
}

function Remove-Session {
    Assert-SessionNameRequired
    $sessionRoot = [System.IO.Path]::GetFullPath((Get-SessionRoot $Name))
    $requiredPrefix = [System.IO.Path]::GetFullPath($sessionsRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $sessionRoot.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove session path outside '$sessionsRoot'."
    }

    Invoke-WithRegistryLock {
        $manifest = Read-SessionManifest $Name
        if ($null -ne $manifest -and $null -ne (Get-OwnedEditorProcess $manifest)) {
            throw "MCP editor session '$Name' is running. Stop it before removing its artifacts."
        }
        if (-not (Test-Path -LiteralPath $sessionRoot)) {
            throw "MCP editor session '$Name' does not exist."
        }

        Remove-Item -LiteralPath $sessionRoot -Recurse -Force
    }
    Write-Result ([pscustomobject]@{ Name = $Name; State = 'Removed'; Root = $sessionRoot })
}

switch ($Action) {
    'Start' { Start-Session }
    'Stop' { Stop-Session }
    'Status' { Get-SessionStatus }
    'List' { Get-SessionList }
    'Remove' { Remove-Session }
}
