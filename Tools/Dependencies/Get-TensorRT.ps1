<#!
.SYNOPSIS
    Downloads or stages the NVIDIA TensorRT Windows SDK and optionally configures TENSORRT_ROOT_DIR.

.DESCRIPTION
    TensorRT is required to build the upstream NVIDIA Audio2Face-3D SDK on Windows.
    NVIDIA commonly distributes the Windows C++ SDK as a zip archive through the NVIDIA
    Developer portal. Because that download can be gated, this script supports three flows:

    1. Reuse a local TensorRT zip archive via -ArchivePath.
    2. Download a TensorRT zip archive from a direct URL via -DownloadUrl.
    3. Reuse the newest previously downloaded TensorRT archive in Build\Dependencies\TensorRT\downloads.

    The script extracts the archive into Build\Dependencies\TensorRT\TensorRT-<version>
    by default, validates expected headers/libs, and can optionally set TENSORRT_ROOT_DIR
    for the current user or current process.

.PARAMETER ArchivePath
    Path to a local TensorRT zip archive.

.PARAMETER DownloadUrl
    Direct download URL for a TensorRT Windows zip archive.

.PARAMETER Version
    Version label used for naming extracted folders and downloaded archives.
    Known direct URL resolution is currently built in for TensorRT 10.13.3.9 on Windows CUDA 12.9.

.PARAMETER OutputDir
    Target extraction directory. Defaults to Build\Dependencies\TensorRT\TensorRT-<version>.

.PARAMETER Force
    Re-download or re-extract even if the archive or extracted folder already exists.

.PARAMETER SetUserEnvironment
    Persist TENSORRT_ROOT_DIR for the current user after a successful extract.

.PARAMETER SetProcessEnvironment
    Set TENSORRT_ROOT_DIR in the current PowerShell process after a successful extract.

.EXAMPLE
    pwsh Tools\Dependencies\Get-TensorRT.ps1 -ArchivePath C:\Downloads\TensorRT-10.13.3.9.Windows.win10.cuda-12.9.zip

.EXAMPLE
    pwsh Tools\Dependencies\Get-TensorRT.ps1 -SetUserEnvironment
#>
[CmdletBinding()]
param(
    [string]$ArchivePath,
    [string]$DownloadUrl,
    [string]$Version = "10.13.3.9",
    [string]$OutputDir,
    [switch]$Force,
    [switch]$SetUserEnvironment,
    [switch]$SetProcessEnvironment
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$dependencyRoot = Join-Path $repoRoot "Build\Dependencies\TensorRT"
$downloadDir = Join-Path $dependencyRoot "downloads"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $dependencyRoot ("TensorRT-" + $Version)
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$downloadDir = [System.IO.Path]::GetFullPath($downloadDir)
$tempExtract = Join-Path $dependencyRoot "_tmp_extract"

function Find-TensorRtRoot {
    param([Parameter(Mandatory)][string]$ExtractedRoot)

    $candidates = @()
    if (Test-Path -LiteralPath (Join-Path $ExtractedRoot "include\NvInfer.h")) {
        $candidates += (Get-Item -LiteralPath $ExtractedRoot)
    }

    $candidates += Get-ChildItem -Path $ExtractedRoot -Directory -Recurse | Where-Object {
        Test-Path -LiteralPath (Join-Path $_.FullName "include\NvInfer.h")
    }

    return $candidates | Sort-Object FullName | Select-Object -First 1
}

function Expand-ZipArchive {
    param(
        [Parameter(Mandatory)][string]$SourceArchive,
        [Parameter(Mandatory)][string]$DestinationPath
    )

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Recurse -Force
    }

    New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($SourceArchive, $DestinationPath)
}

function Resolve-KnownTensorRtDownload {
    param([Parameter(Mandatory)][string]$RequestedVersion)

    $normalizedVersion = $RequestedVersion.Trim()
    switch ($normalizedVersion) {
        "10.13.3" {
            return [pscustomobject]@{
                Version = "10.13.3.9"
                ArchiveName = "TensorRT-10.13.3.9.Windows.win10.cuda-12.9.zip"
                DownloadUrl = "https://developer.nvidia.com/downloads/compute/machine-learning/tensorrt/10.13.3.9/TensorRT-10.13.3.9.Windows.win10.cuda-12.9.zip"
                Notes = "Requires an authenticated NVIDIA Developer session if the direct file is gated."
            }
        }
        "10.13.3.9" {
            return [pscustomobject]@{
                Version = "10.13.3.9"
                ArchiveName = "TensorRT-10.13.3.9.Windows.win10.cuda-12.9.zip"
                DownloadUrl = "https://developer.nvidia.com/downloads/compute/machine-learning/tensorrt/10.13.3.9/TensorRT-10.13.3.9.Windows.win10.cuda-12.9.zip"
                Notes = "Requires an authenticated NVIDIA Developer session if the direct file is gated."
            }
        }
        default {
            return $null
        }
    }
}

New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null
New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null

$knownDownload = $null
if ([string]::IsNullOrWhiteSpace($DownloadUrl)) {
    $knownDownload = Resolve-KnownTensorRtDownload -RequestedVersion $Version
    if ($null -ne $knownDownload) {
        $DownloadUrl = $knownDownload.DownloadUrl
        $Version = $knownDownload.Version
        Write-Host "Using built-in TensorRT download mapping for version $Version" -ForegroundColor Yellow
        Write-Host $knownDownload.Notes -ForegroundColor Yellow
    }
}

if (-not [string]::IsNullOrWhiteSpace($DownloadUrl) -and [string]::IsNullOrWhiteSpace($ArchivePath)) {
    if ($null -ne $knownDownload) {
        $archiveName = $knownDownload.ArchiveName
    }
    else {
        $archiveName = if ($Version -eq "latest") { "TensorRT-latest.zip" } else { "TensorRT-$Version.zip" }
    }
    $ArchivePath = Join-Path $downloadDir $archiveName

    if ((-not (Test-Path -LiteralPath $ArchivePath)) -or $Force) {
        Write-Host "Downloading TensorRT archive from $DownloadUrl" -ForegroundColor Yellow
        try {
            Invoke-WebRequest -Uri $DownloadUrl -OutFile $ArchivePath -UseBasicParsing
        }
        catch {
            if (Test-Path -LiteralPath $ArchivePath) {
                Remove-Item -LiteralPath $ArchivePath -Force -ErrorAction SilentlyContinue
            }

            if ($null -ne $knownDownload) {
                Write-Host "TensorRT direct download failed for the built-in NVIDIA URL." -ForegroundColor Yellow
                Write-Host "This usually means NVIDIA requires an authenticated browser session for that asset." -ForegroundColor Yellow
                Write-Host "Download the archive manually from the TensorRT portal, then rerun with -ArchivePath." -ForegroundColor Yellow
                return
            }

            throw
        }
    }
    else {
        Write-Host "Reusing existing TensorRT archive at $ArchivePath (use -Force to re-download)." -ForegroundColor Green
    }
}

if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $localArchive = Get-ChildItem -Path $downloadDir -File | Where-Object {
        $_.Name -match '^TensorRT-.*\.zip$' -or $_.Name -match '^tensorrt-.*\.zip$'
    } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1

    if ($null -ne $localArchive) {
        $ArchivePath = $localArchive.FullName
        Write-Host "Using local TensorRT archive: $ArchivePath" -ForegroundColor Yellow
    }
}

if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    Write-Host "TensorRT Windows SDK download is often gated behind NVIDIA Developer authentication." -ForegroundColor Yellow
    Write-Host "Provide one of the following:" -ForegroundColor Yellow
    Write-Host "  -ArchivePath <TensorRT zip>" -ForegroundColor Yellow
    Write-Host "  -DownloadUrl <direct TensorRT zip URL>" -ForegroundColor Yellow
    Write-Host "Known built-in mapping: version 10.13.3.9 -> TensorRT-10.13.3.9.Windows.win10.cuda-12.9.zip" -ForegroundColor Yellow
    Write-Host "Or place a TensorRT-*.zip archive in $downloadDir and rerun the script." -ForegroundColor Yellow
    return
}

$ArchivePath = [System.IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $ArchivePath)) {
    throw "TensorRT archive not found: $ArchivePath"
}

if (-not $ArchivePath.EndsWith(".zip", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "TensorRT Windows SDK archives are expected to be zip files. Unsupported archive: $ArchivePath"
}

if ((Test-Path -LiteralPath $OutputDir) -and -not $Force) {
    $existingRoot = Find-TensorRtRoot -ExtractedRoot $OutputDir
    if ($null -ne $existingRoot) {
        Write-Host "TensorRT is already extracted at $($existingRoot.FullName) (use -Force to re-extract)." -ForegroundColor Green

        if ($SetProcessEnvironment) {
            $env:TENSORRT_ROOT_DIR = $existingRoot.FullName
            Write-Host "Set process TENSORRT_ROOT_DIR=$($existingRoot.FullName)" -ForegroundColor Green
        }

        if ($SetUserEnvironment) {
            [Environment]::SetEnvironmentVariable("TENSORRT_ROOT_DIR", $existingRoot.FullName, "User")
            Write-Host "Persisted user TENSORRT_ROOT_DIR=$($existingRoot.FullName)" -ForegroundColor Green
        }

        return
    }
}

if (Test-Path -LiteralPath $tempExtract) {
    Remove-Item -LiteralPath $tempExtract -Recurse -Force
}

Write-Host "Extracting TensorRT archive..." -ForegroundColor Yellow
Expand-ZipArchive -SourceArchive $ArchivePath -DestinationPath $tempExtract

$tensorRtRoot = Find-TensorRtRoot -ExtractedRoot $tempExtract
if ($null -eq $tensorRtRoot) {
    Remove-Item -LiteralPath $tempExtract -Recurse -Force
    throw "Unable to locate include\NvInfer.h in the extracted TensorRT archive."
}

if (Test-Path -LiteralPath $OutputDir) {
    Remove-Item -LiteralPath $OutputDir -Recurse -Force
}

New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($OutputDir)) -Force | Out-Null
Move-Item -LiteralPath $tensorRtRoot.FullName -Destination $OutputDir -Force

if (Test-Path -LiteralPath $tempExtract) {
    Remove-Item -LiteralPath $tempExtract -Recurse -Force
}

$requiredPaths = @(
    "include\NvInfer.h",
    "lib\nvinfer.lib"
)

foreach ($relativePath in $requiredPaths) {
    $fullPath = Join-Path $OutputDir $relativePath
    if (-not (Test-Path -LiteralPath $fullPath)) {
        throw "TensorRT extract is missing an expected file: $relativePath"
    }
}

if ($SetProcessEnvironment) {
    $env:TENSORRT_ROOT_DIR = $OutputDir
    Write-Host "Set process TENSORRT_ROOT_DIR=$OutputDir" -ForegroundColor Green
}

if ($SetUserEnvironment) {
    [Environment]::SetEnvironmentVariable("TENSORRT_ROOT_DIR", $OutputDir, "User")
    Write-Host "Persisted user TENSORRT_ROOT_DIR=$OutputDir" -ForegroundColor Green
}

Write-Host "TensorRT setup complete." -ForegroundColor Cyan
Write-Host "TensorRT root: $OutputDir" -ForegroundColor Cyan
Write-Host "If you want the Audio2Face SDK build to pick it up immediately in this shell, set:`n  `$env:TENSORRT_ROOT_DIR = '$OutputDir'" -ForegroundColor Yellow