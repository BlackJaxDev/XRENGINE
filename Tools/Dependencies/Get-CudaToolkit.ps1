<#!
.SYNOPSIS
    Downloads or stages the NVIDIA CUDA Toolkit installer and optionally runs it silently.

.DESCRIPTION
    The NVIDIA Audio2Face-3D SDK requires a compatible CUDA toolkit on Windows.
    This script supports either downloading a CUDA installer from a direct URL or reusing
    a local installer already downloaded by the user. By default it stages the installer in
    Build\Dependencies\CUDA\downloads. If -Install is specified, it launches the installer
    in silent mode.

    The script does not invent version-specific NVIDIA download URLs because those filenames
    change between releases. Supply -DownloadUrl when you want unattended downloads.

.PARAMETER InstallerPath
    Path to a local CUDA Windows installer (.exe).

.PARAMETER DownloadUrl
    Direct download URL for a CUDA Windows installer.

.PARAMETER Version
    Version label used for the downloaded installer name.

.PARAMETER OutputDir
    Directory where installers are stored. Defaults to Build\Dependencies\CUDA\downloads.

.PARAMETER Force
    Re-download the installer even if it already exists in OutputDir.

.PARAMETER Install
    Run the CUDA installer in silent mode after acquisition.

.PARAMETER InstallerArguments
    Extra arguments passed to the CUDA installer. Defaults to -s.

.EXAMPLE
    pwsh Tools\Dependencies\Get-CudaToolkit.ps1 -DownloadUrl https://developer.download.nvidia.com/.../cuda_installer.exe

.EXAMPLE
    pwsh Tools\Dependencies\Get-CudaToolkit.ps1 -InstallerPath C:\Downloads\cuda_12.9.1_windows.exe -Install
#>
[CmdletBinding()]
param(
    [string]$InstallerPath,
    [string]$DownloadUrl,
    [string]$Version = "12.9",
    [string]$OutputDir,
    [switch]$Force,
    [switch]$Install,
    [string[]]$InstallerArguments = @("-s")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "Build\Dependencies\CUDA\downloads"
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

if (-not [string]::IsNullOrWhiteSpace($DownloadUrl) -and [string]::IsNullOrWhiteSpace($InstallerPath)) {
    $fileName = if ($Version -eq "latest") { "cuda-latest-windows.exe" } else { "cuda-$Version-windows.exe" }
    $InstallerPath = Join-Path $OutputDir $fileName

    if ((-not (Test-Path -LiteralPath $InstallerPath)) -or $Force) {
        Write-Host "Downloading CUDA installer from $DownloadUrl" -ForegroundColor Yellow
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $InstallerPath -UseBasicParsing
    }
    else {
        Write-Host "Reusing existing CUDA installer at $InstallerPath (use -Force to re-download)." -ForegroundColor Green
    }
}

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $localInstaller = Get-ChildItem -Path $OutputDir -File | Where-Object {
        $_.Name -match '^cuda-.*\.exe$' -or $_.Name -match '^cuda_.*\.exe$'
    } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1

    if ($null -ne $localInstaller) {
        $InstallerPath = $localInstaller.FullName
        Write-Host "Using local CUDA installer: $InstallerPath" -ForegroundColor Yellow
    }
}

if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    Write-Host "CUDA Toolkit setup requires either a local installer or a direct NVIDIA download URL." -ForegroundColor Yellow
    Write-Host "Provide one of the following:" -ForegroundColor Yellow
    Write-Host "  -InstallerPath <cuda installer exe>" -ForegroundColor Yellow
    Write-Host "  -DownloadUrl <direct cuda installer URL>" -ForegroundColor Yellow
    Write-Host "Or place a cuda-*.exe installer in $OutputDir and rerun the script." -ForegroundColor Yellow
    return
}

$InstallerPath = [System.IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $InstallerPath)) {
    throw "CUDA installer not found: $InstallerPath"
}

if (-not $InstallerPath.EndsWith(".exe", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "CUDA installer must be a Windows .exe file: $InstallerPath"
}

Write-Host "CUDA installer ready: $InstallerPath" -ForegroundColor Cyan

if ($Install) {
    Write-Host "Running CUDA installer silently..." -ForegroundColor Yellow
    & $InstallerPath @InstallerArguments
    if ($LASTEXITCODE -ne 0) {
        throw "CUDA installer failed with exit code $LASTEXITCODE"
    }

    if ([string]::IsNullOrWhiteSpace($env:CUDA_PATH)) {
        Write-Host "CUDA installer completed, but CUDA_PATH is not visible in the current shell yet. Open a new shell or set it manually if needed." -ForegroundColor Yellow
    }
    else {
        Write-Host "CUDA_PATH is now '$env:CUDA_PATH'" -ForegroundColor Green
    }
}
else {
    Write-Host "Install was skipped. Rerun with -Install to launch the installer silently." -ForegroundColor Yellow
}