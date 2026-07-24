[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$RdcCliVersion = "0.5.6",

    [Parameter()]
    [switch]$ForceRenderDocInstall,

    [Parameter()]
    [switch]$ForceRdcCliInstall,

    [Parameter()]
    [switch]$SkipRenderDocPythonSetup
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "The XRENGINE RenderDoc tool installer currently supports Windows only."
}

$renderDocPackageId = "BaldurKarlsson.RenderDoc"
$uvPackageId = "astral-sh.uv"

function Invoke-Native {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter()]
        [string[]]$Arguments = @()
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
    }
}

function Test-NativeExecutable {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    try {
        & $Path "--version" *> $null
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

function Find-Executable {
    param(
        [Parameter(Mandatory)]
        [string]$CommandName,

        [Parameter()]
        [string[]]$Candidates = @()
    )

    $command = Get-Command $CommandName -ErrorAction SilentlyContinue
    if ($null -ne $command -and (Test-NativeExecutable -Path $command.Source)) {
        return $command.Source
    }

    foreach ($candidate in $Candidates | Select-Object -Unique) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-NativeExecutable -Path $candidate)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

function Find-RenderDocCommand {
    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($root in @(
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:LOCALAPPDATA
    )) {
        if (-not [string]::IsNullOrWhiteSpace($root)) {
            $candidates.Add((Join-Path $root "RenderDoc\renderdoccmd.exe"))
        }
    }

    return Find-Executable -CommandName "renderdoccmd" -Candidates $candidates
}

function Find-WinGetCommand {
    $command = Get-Command winget -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        return $null
    }

    return $command.Source
}

function Refresh-ProcessPath {
    $machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $env:Path = "$env:Path;$machinePath;$userPath"
}

function Install-WinGetPackage {
    param(
        [Parameter(Mandatory)]
        [string]$PackageId,

        [Parameter()]
        [switch]$Force
    )

    $winget = Find-WinGetCommand
    if ([string]::IsNullOrWhiteSpace($winget)) {
        throw "winget is required to install '$PackageId'. Install Windows App Installer, then rerun this tool."
    }

    $arguments = @(
        "install",
        "--id", $PackageId,
        "--exact",
        "--source", "winget",
        "--accept-package-agreements",
        "--accept-source-agreements"
    )
    if ($Force) {
        $arguments += "--force"
    }

    Invoke-Native -FilePath $winget -Arguments $arguments
    Refresh-ProcessPath
}

function Find-UvCommand {
    $candidates = [System.Collections.Generic.List[string]]::new()
    $candidates.Add((Join-Path $env:USERPROFILE ".local\bin\uv.exe"))
    $candidates.Add((Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\uv.exe"))

    $wingetPackagesRoot = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages"
    if (Test-Path -LiteralPath $wingetPackagesRoot -PathType Container) {
        foreach ($package in Get-ChildItem -LiteralPath $wingetPackagesRoot -Directory -Filter "astral-sh.uv_*") {
            $candidates.Add((Join-Path $package.FullName "uv.exe"))
        }
    }

    return Find-Executable -CommandName "uv" -Candidates $candidates
}

function Find-RdcCommand {
    $candidates = @(
        (Join-Path $env:USERPROFILE ".local\bin\rdc.exe"),
        (Join-Path $env:APPDATA "Python\Scripts\rdc.exe")
    )
    return Find-Executable -CommandName "rdc" -Candidates $candidates
}

function Get-RdcCliVersion {
    param([Parameter(Mandatory)][string]$RdcPath)

    $versionOutput = (& $RdcPath "--version" 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch 'version\s+(?<version>\d+\.\d+\.\d+)') {
        return $null
    }

    return $Matches.version
}

function Add-UserPathEntry {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $userEntries = @(
        $userPath -split ';' |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    $alreadyPersisted = $false
    foreach ($entry in $userEntries) {
        $expandedEntry = [Environment]::ExpandEnvironmentVariables($entry).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        if ($expandedEntry.Equals($fullPath, [StringComparison]::OrdinalIgnoreCase)) {
            $alreadyPersisted = $true
            break
        }
    }

    if (-not $alreadyPersisted) {
        $updated = (@($userEntries) + $fullPath) -join ';'
        [Environment]::SetEnvironmentVariable("Path", $updated, "User")
        Write-Host "Added to the per-user PATH: $fullPath" -ForegroundColor Cyan
    }

    $processEntries = @($env:Path -split ';')
    if (-not ($processEntries | Where-Object {
        [Environment]::ExpandEnvironmentVariables($_).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar).Equals(
            $fullPath,
            [StringComparison]::OrdinalIgnoreCase)
    })) {
        $env:Path = "$env:Path;$fullPath"
    }
}

$renderDocCommand = Find-RenderDocCommand
if ([string]::IsNullOrWhiteSpace($renderDocCommand) -or $ForceRenderDocInstall) {
    Write-Host "Installing RenderDoc through winget..." -ForegroundColor Yellow
    Install-WinGetPackage -PackageId $renderDocPackageId -Force:$ForceRenderDocInstall
    $renderDocCommand = Find-RenderDocCommand
}

if ([string]::IsNullOrWhiteSpace($renderDocCommand)) {
    throw "RenderDoc installation completed, but renderdoccmd.exe could not be found."
}

Add-UserPathEntry -Path (Split-Path -Parent $renderDocCommand)
$renderDocVersion = (& $renderDocCommand "--version" 2>&1 | Out-String).Trim()
Write-Host "RenderDoc ready: $renderDocVersion" -ForegroundColor Green

$rdcCommand = Find-RdcCommand
$installedRdcVersion = if ([string]::IsNullOrWhiteSpace($rdcCommand)) {
    $null
}
else {
    Get-RdcCliVersion -RdcPath $rdcCommand
}

if ($ForceRdcCliInstall -or $installedRdcVersion -ne $RdcCliVersion) {
    $uvCommand = Find-UvCommand
    if ([string]::IsNullOrWhiteSpace($uvCommand)) {
        Write-Host "Installing uv through winget..." -ForegroundColor Yellow
        Install-WinGetPackage -PackageId $uvPackageId
        $uvCommand = Find-UvCommand
    }

    if ([string]::IsNullOrWhiteSpace($uvCommand)) {
        throw "uv installation completed, but uv.exe could not be found."
    }

    Write-Host "Installing pinned rdc-cli $RdcCliVersion in an isolated uv tool environment..." -ForegroundColor Yellow
    Invoke-Native -FilePath $uvCommand -Arguments @(
        "tool", "install", "--force", "rdc-cli==$RdcCliVersion"
    )
    $rdcCommand = Find-RdcCommand
}

if ([string]::IsNullOrWhiteSpace($rdcCommand)) {
    throw "rdc-cli installation completed, but rdc.exe could not be found."
}

Add-UserPathEntry -Path (Split-Path -Parent $rdcCommand)
$installedRdcVersion = Get-RdcCliVersion -RdcPath $rdcCommand
if ($installedRdcVersion -ne $RdcCliVersion) {
    throw "Expected rdc-cli $RdcCliVersion, but '$rdcCommand' reports '$installedRdcVersion'."
}
Write-Host "rdc-cli ready: $installedRdcVersion" -ForegroundColor Green

Write-Host "Running rdc doctor..." -ForegroundColor Yellow
& $rdcCommand "doctor"
$doctorExitCode = $LASTEXITCODE
if ($doctorExitCode -ne 0) {
    if ($SkipRenderDocPythonSetup) {
        throw "rdc doctor failed with exit code $doctorExitCode and RenderDoc Python setup was skipped."
    }

    Write-Host "Bootstrapping the RenderDoc replay Python module for rdc-cli..." -ForegroundColor Yellow
    Invoke-Native -FilePath $rdcCommand -Arguments @("setup-renderdoc")
    Invoke-Native -FilePath $rdcCommand -Arguments @("doctor")
}

$renderDocPythonRoot = Join-Path $env:LOCALAPPDATA "rdc\renderdoc"
$renderDocPythonModule = Join-Path $renderDocPythonRoot "renderdoc.pyd"
if (Test-Path -LiteralPath $renderDocPythonModule -PathType Leaf) {
    [Environment]::SetEnvironmentVariable(
        "RENDERDOC_PYTHON_PATH",
        $renderDocPythonRoot,
        "User")
    $env:RENDERDOC_PYTHON_PATH = $renderDocPythonRoot
    Write-Host "RENDERDOC_PYTHON_PATH=$renderDocPythonRoot" -ForegroundColor Cyan
}

Write-Host "RenderDoc and rdc-cli installation validated successfully." -ForegroundColor Green
Write-Host "Open a new shell so the persisted PATH is inherited by other tools." -ForegroundColor Cyan
