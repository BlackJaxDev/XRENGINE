<#
.SYNOPSIS
    Clones or updates the official NVIDIA Audio2Face-3D SDK and optionally prepares its build environment.

.DESCRIPTION
    The upstream NVIDIA SDK is distributed as source, not as a packaged binary release.
    This script mirrors the official Windows setup flow by cloning the public GitHub repository,
    optionally running git-lfs pull, fetching the SDK's build dependencies, and optionally building
    the SDK or downloading gated models.

    By default, the script:
    - Clones or updates https://github.com/NVIDIA/Audio2Face-3D-SDK.git
    - Runs git lfs pull when git-lfs is available
    - Runs fetch_deps.bat release

    It does not build the SDK unless -Build is specified because that requires CUDA + TensorRT.
    It does not download gated Hugging Face models unless -DownloadModels is specified.

.PARAMETER Ref
    Branch, tag, or commit-ish to check out. Defaults to main.

.PARAMETER OutputDir
    Destination folder. Defaults to Build\Dependencies\Audio2Face-3D-SDK in this repo.

.PARAMETER Configuration
    SDK build configuration used by fetch_deps/build scripts. Defaults to release.

.PARAMETER Force
    For an existing checkout, fetch and hard-reset to the requested ref. For fetch_deps, force a re-run.

.PARAMETER SkipGitLfsPull
    Skip git lfs pull even if git-lfs is installed.

.PARAMETER SkipFetchDeps
    Skip fetch_deps.bat.

.PARAMETER Build
    Run build.bat all <configuration> after acquisition. Requires TENSORRT_ROOT_DIR.

.PARAMETER DownloadModels
    Run the upstream download_models.bat after preparing a local Python venv and requirements.
    Requires a Python version supported by the SDK and an authenticated Hugging Face CLI session.

.PARAMETER GenerateTestData
    Run the upstream gen_testdata.bat after models are available.

.EXAMPLE
    pwsh Tools\Dependencies\Get-Audio2Face3DSdk.ps1

.EXAMPLE
    pwsh Tools\Dependencies\Get-Audio2Face3DSdk.ps1 -Build

.EXAMPLE
    pwsh Tools\Dependencies\Get-Audio2Face3DSdk.ps1 -DownloadModels -GenerateTestData
#>
[CmdletBinding()]
param(
    [string]$RepoUrl = "https://github.com/NVIDIA/Audio2Face-3D-SDK.git",
    [string]$Ref = "main",
    [string]$OutputDir,

    [ValidateSet("release", "debug")]
    [string]$Configuration = "release",

    [switch]$Force,
    [switch]$SkipGitLfsPull,
    [switch]$SkipFetchDeps,
    [switch]$Build,
    [switch]$DownloadModels,
    [switch]$GenerateTestData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "Build\Dependencies\Audio2Face-3D-SDK"
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

function Invoke-External {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [string[]]$ArgumentList = @(),

        [string]$WorkingDirectory = $PWD.Path
    )
    try {
        Push-Location $WorkingDirectory
        & $FilePath @ArgumentList
        if ($LASTEXITCODE -ne 0) {
            $joinedArgs = if ($ArgumentList.Count -gt 0) { $ArgumentList -join ' ' } else { '' }
            throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $joinedArgs"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-RequiredCommand {
    param([Parameter(Mandatory)][string]$Name)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "Required command '$Name' was not found on PATH."
    }

    return $command.Source
}

function Invoke-GitLfsPull {
    param([Parameter(Mandatory)][string]$WorkingDirectory)

    if ($SkipGitLfsPull) {
        Write-Host "Skipping git lfs pull." -ForegroundColor Yellow
        return
    }

    $gitLfsAvailable = $null -ne (Get-Command git-lfs -ErrorAction SilentlyContinue)
    if (-not $gitLfsAvailable) {
        try {
            Invoke-External -FilePath "git" -ArgumentList @("lfs", "version") -WorkingDirectory $WorkingDirectory
            $gitLfsAvailable = $true
        }
        catch {
            $gitLfsAvailable = $false
        }
    }

    if (-not $gitLfsAvailable) {
        Write-Host "git-lfs was not found. Continuing without 'git lfs pull'; sample-data may remain as LFS pointers." -ForegroundColor Yellow
        return
    }

    Write-Host "Running git lfs pull..." -ForegroundColor Yellow
    Invoke-External -FilePath "git" -ArgumentList @("lfs", "pull") -WorkingDirectory $WorkingDirectory
}

function Update-SdkCheckout {
    param(
        [Parameter(Mandatory)][string]$RepositoryUrl,
        [Parameter(Mandatory)][string]$TargetDirectory,
        [Parameter(Mandatory)][string]$GitRef
    )

    $gitPath = Get-RequiredCommand "git"

    if (-not (Test-Path -LiteralPath $TargetDirectory)) {
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($TargetDirectory)) -Force | Out-Null
        Write-Host "Cloning NVIDIA Audio2Face-3D SDK into $TargetDirectory" -ForegroundColor Yellow
        Invoke-External -FilePath $gitPath -ArgumentList @("clone", "--branch", $GitRef, $RepositoryUrl, $TargetDirectory) -WorkingDirectory $repoRoot
        return
    }

    if (-not (Test-Path -LiteralPath (Join-Path $TargetDirectory ".git"))) {
        throw "Target directory already exists but is not a git checkout: $TargetDirectory"
    }

    Write-Host "Updating existing NVIDIA Audio2Face-3D SDK checkout at $TargetDirectory" -ForegroundColor Yellow
    Invoke-External -FilePath $gitPath -ArgumentList @("remote", "set-url", "origin", $RepositoryUrl) -WorkingDirectory $TargetDirectory
    Invoke-External -FilePath $gitPath -ArgumentList @("fetch", "--all", "--tags", "--prune") -WorkingDirectory $TargetDirectory
    Invoke-External -FilePath $gitPath -ArgumentList @("checkout", $GitRef) -WorkingDirectory $TargetDirectory

    if ($Force) {
        Invoke-External -FilePath $gitPath -ArgumentList @("reset", "--hard", "origin/$GitRef") -WorkingDirectory $TargetDirectory
    }
    else {
        try {
            Invoke-External -FilePath $gitPath -ArgumentList @("pull", "--ff-only", "origin", $GitRef) -WorkingDirectory $TargetDirectory
        }
        catch {
            Write-Host "Fast-forward pull was not applied for '$GitRef'. Leaving the checked-out ref in place." -ForegroundColor Yellow
        }
    }
}

function New-PythonVenv {
    param([Parameter(Mandatory)][string]$SdkDirectory)

    $pythonPath = Get-RequiredCommand "python"
    $venvDir = Join-Path $SdkDirectory "venv"
    $venvPython = Join-Path $venvDir "Scripts\python.exe"

    if (-not (Test-Path -LiteralPath $venvPython)) {
        Write-Host "Creating Python virtual environment for Audio2Face-3D SDK..." -ForegroundColor Yellow
        Invoke-External -FilePath $pythonPath -ArgumentList @("-m", "venv", $venvDir) -WorkingDirectory $SdkDirectory
    }

    Write-Host "Installing Python requirements for Audio2Face-3D SDK..." -ForegroundColor Yellow
    Invoke-External -FilePath $venvPython -ArgumentList @("-m", "pip", "install", "-r", "deps\requirements.txt") -WorkingDirectory $SdkDirectory

    return $venvPython
}

Update-SdkCheckout -RepositoryUrl $RepoUrl -TargetDirectory $OutputDir -GitRef $Ref
Invoke-GitLfsPull -WorkingDirectory $OutputDir

if (-not $SkipFetchDeps) {
    Write-Host "Running fetch_deps.bat $Configuration..." -ForegroundColor Yellow
    Invoke-External -FilePath "cmd.exe" -ArgumentList @("/d", "/c", "call fetch_deps.bat $Configuration") -WorkingDirectory $OutputDir
}

if ($Build) {
    if ([string]::IsNullOrWhiteSpace($env:TENSORRT_ROOT_DIR)) {
        throw "TENSORRT_ROOT_DIR must be set before using -Build. See the upstream NVIDIA Audio2Face-3D SDK build docs."
    }

    if ([string]::IsNullOrWhiteSpace($env:CUDA_PATH)) {
        Write-Host "CUDA_PATH is not set. This is acceptable if the CUDA installer already configured the toolchain globally." -ForegroundColor Yellow
    }

    Write-Host "Running build.bat all $Configuration..." -ForegroundColor Yellow
    Invoke-External -FilePath "cmd.exe" -ArgumentList @("/d", "/c", "call build.bat all $Configuration") -WorkingDirectory $OutputDir
}

$venvPython = $null
if ($DownloadModels -or $GenerateTestData) {
    $venvPython = New-PythonVenv -SdkDirectory $OutputDir
}

if ($DownloadModels) {
    Write-Host "Downloading gated Audio2Face / Audio2Emotion models via download_models.bat..." -ForegroundColor Yellow
    Invoke-External -FilePath "cmd.exe" -ArgumentList @("/d", "/c", "call download_models.bat") -WorkingDirectory $OutputDir
}

if ($GenerateTestData) {
    Write-Host "Generating TensorRT test data via gen_testdata.bat..." -ForegroundColor Yellow
    Invoke-External -FilePath "cmd.exe" -ArgumentList @("/d", "/c", "call gen_testdata.bat") -WorkingDirectory $OutputDir
}

$requiredFiles = @(
    "README.md",
    "fetch_deps.bat",
    "build.bat",
    "download_models.bat",
    "gen_testdata.bat"
)

foreach ($relativePath in $requiredFiles) {
    $fullPath = Join-Path $OutputDir $relativePath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        throw "Audio2Face-3D SDK checkout is missing an expected file: $relativePath"
    }
}

if (-not $SkipFetchDeps) {
    $depsDir = Join-Path $OutputDir "_deps\build-deps"
    if (-not (Test-Path -LiteralPath $depsDir)) {
        throw "fetch_deps.bat completed but _deps\build-deps was not found at $depsDir"
    }
}

if ($Build) {
    $sdkDll = Join-Path $OutputDir "_build\$Configuration\audio2x-sdk\bin\audio2x.dll"
    if (-not (Test-Path -LiteralPath $sdkDll)) {
        throw "Audio2X SDK build completed without producing audio2x.dll at $sdkDll"
    }
}

Write-Host "Audio2Face-3D SDK setup complete." -ForegroundColor Cyan
Write-Host "SDK directory: $OutputDir" -ForegroundColor Cyan
if (-not $Build) {
    Write-Host "Build was skipped. To compile the NVIDIA SDK, set TENSORRT_ROOT_DIR and rerun with -Build." -ForegroundColor Yellow
}
if (-not $DownloadModels) {
    Write-Host "Model download was skipped. Use -DownloadModels after authenticating 'hf auth login' if you need the gated models." -ForegroundColor Yellow
}