[CmdletBinding()]
param(
    [switch]$ReserveTaskRun,
    [switch]$AsJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$validationRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'Build\_AgentValidation'))
$sharedRootName = '00000000-000000-shared'
$sharedRoot = Join-Path $validationRoot $sharedRootName
$maximumImmediateDirectoryCount = 5
$maximumTaskRunCount = $maximumImmediateDirectoryCount - 1
$maximumMcpSessionCount = 5
$runNamePattern = '^\d{8}-\d{6}-[A-Za-z0-9][A-Za-z0-9._-]*$'

function Test-OwnedProcess($Manifest) {
    if ($null -eq $Manifest -or $null -eq $Manifest.processId) {
        return $false
    }

    $process = Get-Process -Id ([int]$Manifest.processId) -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return $false
    }

    $expectedStart = [DateTime]::MinValue
    if (-not [DateTime]::TryParse(
            [string]$Manifest.processStartTimeUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$expectedStart)) {
        return $false
    }

    try {
        return $process.StartTime.ToUniversalTime() -eq $expectedStart.ToUniversalTime()
    }
    catch {
        return $false
    }
}

function Test-ActiveDirectory([System.IO.DirectoryInfo]$Directory) {
    foreach ($manifestPath in Get-ChildItem -LiteralPath $Directory.FullName -Filter session.json -File -Recurse -ErrorAction SilentlyContinue) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath.FullName -Raw | ConvertFrom-Json
            if (Test-OwnedProcess $manifest) {
                return $true
            }
        }
        catch {
            # An unreadable ignored manifest cannot establish ownership.
        }
    }
    return $false
}

function Get-DirectoryActivityUtc([System.IO.DirectoryInfo]$Directory) {
    $manifestPath = Join-Path $Directory.FullName 'session.json'
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            foreach ($propertyName in @('stoppedUtc', 'startedUtc', 'createdUtc')) {
                $parsed = [DateTime]::MinValue
                if ([DateTime]::TryParse(
                        [string]$manifest.$propertyName,
                        [Globalization.CultureInfo]::InvariantCulture,
                        [Globalization.DateTimeStyles]::RoundtripKind,
                        [ref]$parsed)) {
                    return $parsed.ToUniversalTime()
                }
            }
        }
        catch {
            # Fall through to filesystem activity for malformed disposable metadata.
        }
    }
    return $Directory.LastWriteTimeUtc
}

function Remove-ContainedDirectory([System.IO.DirectoryInfo]$Directory) {
    $fullPath = [System.IO.Path]::GetFullPath($Directory.FullName)
    $requiredPrefix = $validationRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove agent output outside '$validationRoot'."
    }
    if (Test-ActiveDirectory $Directory) {
        throw "Refusing to remove active agent output '$fullPath'."
    }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

[System.IO.Directory]::CreateDirectory($sharedRoot) | Out-Null
$removed = [System.Collections.Generic.List[string]]::new()

foreach ($file in Get-ChildItem -LiteralPath $validationRoot -File -Force) {
    $fullPath = [System.IO.Path]::GetFullPath($file.FullName)
    $requiredPrefix = $validationRoot.TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove agent output outside '$validationRoot'."
    }
    [System.IO.File]::Delete($fullPath)
    $removed.Add($fullPath)
}

$sessionsRoot = Join-Path $sharedRoot 'mcp-sessions'
if (Test-Path -LiteralPath $sessionsRoot -PathType Container) {
    $sessions = @(
        Get-ChildItem -LiteralPath $sessionsRoot -Directory |
            Sort-Object { Get-DirectoryActivityUtc $_ } -Descending
    )
    for ($index = 0; $index -lt $sessions.Count; $index++) {
        $session = $sessions[$index]
        if (Test-ActiveDirectory $session) {
            continue
        }
        $cache = Get-Item -LiteralPath (Join-Path $session.FullName 'cache') -ErrorAction SilentlyContinue
        if ($null -ne $cache) {
            Remove-ContainedDirectory $cache
            $removed.Add($cache.FullName)
        }
        $metadata = Get-Item -LiteralPath (Join-Path $session.FullName 'metadata') -ErrorAction SilentlyContinue
        if ($null -ne $metadata) {
            Remove-ContainedDirectory $metadata
            $removed.Add($metadata.FullName)
        }
        $artifacts = Get-Item -LiteralPath (Join-Path $session.FullName 'artifacts') -ErrorAction SilentlyContinue
        if ($null -ne $artifacts) {
            Remove-ContainedDirectory $artifacts
            $removed.Add($artifacts.FullName)
        }
    }
    foreach ($session in @($sessions | Select-Object -Skip $maximumMcpSessionCount)) {
        Remove-ContainedDirectory $session
        $removed.Add($session.FullName)
    }
}

$brokerRoot = Join-Path $sharedRoot 'agent-tools'
if (Test-Path -LiteralPath $brokerRoot -PathType Container) {
    $deployments = @(
        Get-ChildItem -LiteralPath $brokerRoot -Directory -Filter 'LocalAgentBroker-*' |
            Sort-Object Name -Descending
    )
    foreach ($deployment in @($deployments | Select-Object -Skip 2)) {
        Remove-ContainedDirectory $deployment
        $removed.Add($deployment.FullName)
    }
}

$hotReloadRoot = Join-Path $sharedRoot 'renderer-hot-reload'
$hotReloadBuildRoot = Get-Item -LiteralPath (Join-Path $hotReloadRoot 'build') -ErrorAction SilentlyContinue
if ($null -ne $hotReloadBuildRoot) {
    Remove-ContainedDirectory $hotReloadBuildRoot
    $removed.Add($hotReloadBuildRoot.FullName)
}
$hotReloadGenerationsRoot = Join-Path $hotReloadRoot 'generations'
if (Test-Path -LiteralPath $hotReloadGenerationsRoot -PathType Container) {
    foreach ($backend in Get-ChildItem -LiteralPath $hotReloadGenerationsRoot -Directory) {
        $generations = @(
            Get-ChildItem -LiteralPath $backend.FullName -Directory |
                Where-Object { $_.Name -match '^\d+$' } |
                Sort-Object { [long]$_.Name } -Descending
        )
        foreach ($generation in @($generations | Select-Object -Skip 2)) {
            Remove-ContainedDirectory $generation
            $removed.Add($generation.FullName)
        }
    }
}

$staleBenchmarkBuild = Get-Item -LiteralPath (Join-Path $sharedRoot 'tools\VulkanPerformance') -ErrorAction SilentlyContinue
if ($null -ne $staleBenchmarkBuild) {
    Remove-ContainedDirectory $staleBenchmarkBuild
    $removed.Add($staleBenchmarkBuild.FullName)
}

$immediateDirectories = @(Get-ChildItem -LiteralPath $validationRoot -Directory)
foreach ($directory in $immediateDirectories) {
    if ($directory.Name -eq $sharedRootName -or $directory.Name -match $runNamePattern) {
        continue
    }
    Remove-ContainedDirectory $directory
    $removed.Add($directory.FullName)
}

$targetTaskRunCount = $maximumTaskRunCount - [int]$ReserveTaskRun.IsPresent
$taskRuns = @(
    Get-ChildItem -LiteralPath $validationRoot -Directory |
        Where-Object { $_.Name -ne $sharedRootName } |
        Sort-Object LastWriteTimeUtc -Descending
)
foreach ($run in @($taskRuns | Select-Object -Skip $targetTaskRunCount)) {
    Remove-ContainedDirectory $run
    $removed.Add($run.FullName)
}

$result = [pscustomobject]@{
    Root = $validationRoot
    MaximumImmediateDirectories = $maximumImmediateDirectoryCount
    MaximumMcpSessions = $maximumMcpSessionCount
    Removed = @($removed)
    ImmediateDirectories = @(
        Get-ChildItem -LiteralPath $validationRoot -Directory |
            Sort-Object Name |
            ForEach-Object { $_.Name }
    )
    McpSessions = if (Test-Path -LiteralPath $sessionsRoot -PathType Container) {
        @(
            Get-ChildItem -LiteralPath $sessionsRoot -Directory |
                Sort-Object { Get-DirectoryActivityUtc $_ } -Descending |
                ForEach-Object { $_.Name }
        )
    }
    else {
        @()
    }
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 5
}
else {
    $result
}
