[CmdletBinding()]
param(
    [Parameter()]
    [string]$RuntimeJson,

    [Parameter()]
    [string]$ServiceExe,

    [Parameter()]
    [string]$MarkerPath = "Build\_AgentValidation\monado-service-marker.json",

    [Parameter()]
    [string]$LogDirectory = "Build\_AgentValidation\monado-service-logs",

    [Parameter()]
    [ValidateSet("wobble", "rotate", "stationary", "user_input")]
    [string]$SimulatedHmdPoseMode = "stationary",

    [Parameter()]
    [switch]$Stop
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$findRuntimeScript = Join-Path $scriptDirectory "Find-MonadoRuntime.ps1"

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if ([System.IO.Path]::IsPathRooted($expanded)) {
        return [System.IO.Path]::GetFullPath($expanded)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $expanded))
}

function Test-SameProcessStart {
    param(
        [Parameter(Mandatory)]$Process,
        [Parameter()][string]$StartedAtUtc
    )

    if ([string]::IsNullOrWhiteSpace($StartedAtUtc)) {
        return $true
    }

    try {
        $expected = [DateTimeOffset]::Parse($StartedAtUtc).UtcDateTime
        $actual = $Process.StartTime.ToUniversalTime()
        return [Math]::Abs(($actual - $expected).TotalSeconds) -lt 5
    }
    catch {
        return $false
    }
}

function Stop-OwnedService {
    param([Parameter(Mandatory)][string]$Marker)

    if (-not (Test-Path -LiteralPath $Marker -PathType Leaf)) {
        [pscustomobject]@{ Stopped = $false; Reason = "Marker was not present."; MarkerPath = $Marker }
        return
    }

    $markerData = Get-Content -LiteralPath $Marker -Raw | ConvertFrom-Json
    if (-not $markerData.ownedByRunner) {
        [pscustomobject]@{ Stopped = $false; Reason = "Marker indicates the service was not started by this runner."; MarkerPath = $Marker }
        return
    }

    $processId = [int]$markerData.pid
    $ownedStart = [DateTimeOffset]::Parse([string]$markerData.startedAtUtc).UtcDateTime
    $ownedExecutable = [System.IO.Path]::GetFullPath([string]$markerData.serviceExe)
    $stoppedProcessIds = [System.Collections.Generic.List[int]]::new()

    # Some Monado Windows builds bootstrap the long-lived service in a second
    # monado-service process and let the Start-Process child exit. The marker is
    # still authoritative because startup first proved that no service existed.
    # Stop every same-executable process created during that owned interval so a
    # chained validation cohort cannot mistake the bootstrap child for an
    # unrelated, unowned service.
    foreach ($process in @(Get-Process -Name "monado-service" -ErrorAction SilentlyContinue)) {
        $matchesMarkerProcess = $process.Id -eq $processId -and
            (Test-SameProcessStart -Process $process -StartedAtUtc ([string]$markerData.startedAtUtc))
        $matchesOwnedBootstrap = $false
        try {
            $processExecutable = [System.IO.Path]::GetFullPath([string]$process.Path)
            $matchesOwnedBootstrap =
                $process.StartTime.ToUniversalTime() -ge $ownedStart.AddSeconds(-5) -and
                [string]::Equals($processExecutable, $ownedExecutable, [StringComparison]::OrdinalIgnoreCase)
        }
        catch {
            $matchesOwnedBootstrap = $false
        }

        if (-not $matchesMarkerProcess -and -not $matchesOwnedBootstrap) {
            continue
        }

        Stop-Process -Id $process.Id -Force
        $null = $process.WaitForExit(5000)
        $stoppedProcessIds.Add($process.Id)
    }

    Remove-Item -LiteralPath $Marker -Force -ErrorAction SilentlyContinue
    if ($stoppedProcessIds.Count -gt 0) {
        [pscustomobject]@{ Stopped = $true; Pids = @($stoppedProcessIds); MarkerPath = $Marker }
        return
    }

    [pscustomobject]@{ Stopped = $false; Reason = "Owned service processes were already gone or no longer matched the marker."; Pid = $processId; MarkerPath = $Marker }
}

function Find-ServiceExe {
    param(
        [Parameter(Mandatory)]$RuntimeInfo,
        [string]$ExplicitServiceExe
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitServiceExe)) {
        $resolved = Resolve-FullPath $ExplicitServiceExe
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Explicit Monado service executable does not exist: $resolved"
        }
        return $resolved
    }

    $libraryDirectory = Split-Path -Parent ([string]$RuntimeInfo.LibraryPath)
    $manifestDirectory = [string]$RuntimeInfo.ManifestDirectory
    $candidates = @(
        (Join-Path $manifestDirectory "monado-service.exe"),
        (Join-Path $manifestDirectory "bin\monado-service.exe"),
        (Join-Path $libraryDirectory "monado-service.exe"),
        (Join-Path (Split-Path -Parent $libraryDirectory) "bin\monado-service.exe")
    )

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

$markerFullPath = Resolve-FullPath $MarkerPath
if ($Stop) {
    Stop-OwnedService -Marker $markerFullPath
    return
}

$runtimeInfo = & $findRuntimeScript -RuntimeJson $RuntimeJson
$servicePath = Find-ServiceExe -RuntimeInfo $runtimeInfo -ExplicitServiceExe $ServiceExe
if ($null -eq $servicePath) {
    [pscustomobject]@{
        Started       = $false
        OwnedByRunner = $false
        Reason        = "Could not locate monado-service.exe near the runtime manifest/library."
        RuntimeJson   = $runtimeInfo.RuntimeJson
        MarkerPath    = $markerFullPath
    }
    return
}

$existing = Get-Process -Name "monado-service" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -ne $existing) {
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $markerFullPath)) | Out-Null
    [pscustomobject]@{
        pid           = $existing.Id
        startedAtUtc  = $existing.StartTime.ToUniversalTime().ToString("O")
        ownedByRunner = $false
        serviceExe    = $servicePath
        runtimeJson   = $runtimeInfo.RuntimeJson
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $markerFullPath -Encoding UTF8

    [pscustomobject]@{
        Started       = $false
        OwnedByRunner = $false
        Pid           = $existing.Id
        SimulatedHmdPoseMode = "unknown-existing-process"
        Reason        = "monado-service is already running; this script will not stop it."
        RuntimeJson   = $runtimeInfo.RuntimeJson
        MarkerPath    = $markerFullPath
    }
    return
}

$logDirectoryFullPath = Resolve-FullPath $LogDirectory
[System.IO.Directory]::CreateDirectory($logDirectoryFullPath) | Out-Null
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $markerFullPath)) | Out-Null
$stdout = Join-Path $logDirectoryFullPath "monado-service.stdout.log"
$stderr = Join-Path $logDirectoryFullPath "monado-service.stderr.log"

$previousPoseMode = [Environment]::GetEnvironmentVariable("SIMULATED_HMD_POSE_MODE", "Process")
try {
    # The simulated driver otherwise defaults to a time-based wobble. Acceptance
    # runs need a stationary runtime pose so only the scripted validation scene
    # drives head rotation/translation and the static-pose oracle is meaningful.
    [Environment]::SetEnvironmentVariable(
        "SIMULATED_HMD_POSE_MODE",
        $SimulatedHmdPoseMode,
        "Process")
    $process = Start-Process `
        -FilePath $servicePath `
        -WorkingDirectory (Split-Path -Parent $servicePath) `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -PassThru
}
finally {
    [Environment]::SetEnvironmentVariable(
        "SIMULATED_HMD_POSE_MODE",
        $previousPoseMode,
        "Process")
}

[pscustomobject]@{
    pid           = $process.Id
    startedAtUtc  = $process.StartTime.ToUniversalTime().ToString("O")
    ownedByRunner = $true
    simulatedHmdPoseMode = $SimulatedHmdPoseMode
    serviceExe    = $servicePath
    runtimeJson   = $runtimeInfo.RuntimeJson
    stdout        = $stdout
    stderr        = $stderr
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $markerFullPath -Encoding UTF8

[pscustomobject]@{
    Started       = $true
    OwnedByRunner = $true
    Pid           = $process.Id
    SimulatedHmdPoseMode = $SimulatedHmdPoseMode
    RuntimeJson   = $runtimeInfo.RuntimeJson
    ServiceExe    = $servicePath
    MarkerPath    = $markerFullPath
    Stdout        = $stdout
    Stderr        = $stderr
}
