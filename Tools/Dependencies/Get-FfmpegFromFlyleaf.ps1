<#
.SYNOPSIS
    Retrieves FFmpeg runtime DLLs from the Flyleaf GitHub repository and stages them for XRENGINE.

.DESCRIPTION
    Downloads a source archive from a Flyleaf GitHub repository, searches for a Windows x64
    FFmpeg runtime folder (containing avformat-*.dll), and copies discovered FFmpeg-family DLLs
    into:

      - Build/Dependencies/FFmpeg/Seed/win-x64 (always, unless -NoCopyToSeed)
      - Build/Dependencies/FFmpeg/HlsReference/win-x64 (optional, with -CopyToRuntime)

    The engine's EnsureHlsReferenceFfmpeg target can then bootstrap runtime DLLs from the seed folder.

.PARAMETER RepoOwner
    GitHub owner/org for the Flyleaf repository.

.PARAMETER RepoName
    GitHub repository name.

.PARAMETER Ref
    Git ref to fetch (branch, tag, or commit). Defaults to "master".

.PARAMETER Force
    Re-download and overwrite staged DLLs.

.PARAMETER NoCopyToSeed
    Do not copy DLLs to Build/Dependencies/FFmpeg/Seed/win-x64.

.PARAMETER CopyToRuntime
    Also copy DLLs directly into Build/Dependencies/FFmpeg/HlsReference/win-x64.

.EXAMPLE
    pwsh Tools/Dependencies/Get-FfmpegFromFlyleaf.ps1

.EXAMPLE
    pwsh Tools/Dependencies/Get-FfmpegFromFlyleaf.ps1 -Ref v3.8.2 -CopyToRuntime -Force
#>
[CmdletBinding()]
param(
    [string]$RepoOwner = "SuRGeoNix",
    [string]$RepoName = "Flyleaf",
    [string]$Ref = "master",
    [switch]$Force,
    [switch]$NoCopyToSeed,
    [switch]$CopyToRuntime
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Find-BestFfmpegFolder {
    param([Parameter(Mandatory = $true)][string]$Root)

    $core = @(
        "avcodec",
        "avdevice",
        "avfilter",
        "avformat",
        "avutil",
        "postproc",
        "swresample",
        "swscale"
    )

    $candidates = @{}
    $avformatDlls = Get-ChildItem -Path $Root -Recurse -File -Filter "avformat-*.dll"
    foreach ($dll in $avformatDlls) {
        $dir = $dll.Directory.FullName
        if (-not $candidates.ContainsKey($dir)) {
            $candidates[$dir] = [ordered]@{
                Dir = $dir
                CoreCount = 0
                IsX64 = $false
                DllCount = 0
            }

            $names = Get-ChildItem -Path $dir -File -Filter "*.dll" | Select-Object -ExpandProperty Name
            $lower = $names | ForEach-Object { $_.ToLowerInvariant() }

            $coreCount = 0
            foreach ($name in $core) {
                if ($lower -match "^$name-\d+\.dll$") {
                    $coreCount++
                }
            }

            $candidates[$dir].CoreCount = $coreCount
            $candidates[$dir].DllCount = $names.Count
            $candidates[$dir].IsX64 = ($dir -match "(?i)(x64|win64|windows-x64)")
        }
    }

    if ($candidates.Count -eq 0) {
        return $null
    }

    return $candidates.Values |
        Sort-Object -Property @{Expression = { if ($_.IsX64) { 1 } else { 0 } }; Descending = $true }, @{Expression = { $_.CoreCount }; Descending = $true }, @{Expression = { $_.DllCount }; Descending = $true } |
        Select-Object -First 1
}

function Copy-FfmpegDlls {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$DestinationDir,
        [switch]$Overwrite
    )

    New-Item -ItemType Directory -Path $DestinationDir -Force | Out-Null

    $copied = 0
    $dlls = Get-ChildItem -Path $SourceDir -File -Filter "*.dll"
    foreach ($dll in $dlls) {
        $name = $dll.Name.ToLowerInvariant()
        if (
            $name -notmatch "^(avcodec|avdevice|avfilter|avformat|avutil|postproc|swresample|swscale)-\d+\.dll$"
        ) {
            continue
        }

        $dest = Join-Path $DestinationDir $dll.Name
        if ((Test-Path $dest) -and -not $Overwrite) {
            continue
        }

        Copy-Item $dll.FullName -Destination $dest -Force
        $copied++
    }

    return $copied
}

# --- Paths ---
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$depsRoot = Join-Path $repoRoot "Build\Dependencies\FFmpeg"
$downloadDir = Join-Path $depsRoot "_downloads"
$extractRoot = Join-Path $depsRoot "_tmp_flyleaf"
$seedDir = Join-Path $depsRoot "Seed\win-x64"
$runtimeDir = Join-Path $depsRoot "HlsReference\win-x64"

New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null

$safeRef = ($Ref -replace "[^A-Za-z0-9._-]", "_")
$archivePath = Join-Path $downloadDir ("{0}-{1}-{2}.zip" -f $RepoOwner, $RepoName, $safeRef)
$archiveUrl = "https://codeload.github.com/$RepoOwner/$RepoName/zip/refs/heads/$Ref"

if ($Ref -match "^[vV]?\d+(\.\d+){0,3}$") {
    $archiveUrl = "https://codeload.github.com/$RepoOwner/$RepoName/zip/refs/tags/$Ref"
}

if ((-not (Test-Path $archivePath)) -or $Force) {
    Write-Host "Downloading Flyleaf archive:" -ForegroundColor Yellow
    Write-Host "  $archiveUrl" -ForegroundColor Yellow
    Invoke-WebRequest -Uri $archiveUrl -OutFile $archivePath -UseBasicParsing
}
else {
    Write-Host "Reusing existing archive: $archivePath" -ForegroundColor Green
}

if (Test-Path $extractRoot) {
    Remove-Item $extractRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $extractRoot)

$detected = Find-BestFfmpegFolder -Root $extractRoot
if ($null -eq $detected) {
    throw "Unable to locate a Flyleaf FFmpeg x64 DLL folder (expected avformat-*.dll in archive)."
}

Write-Host "Detected FFmpeg candidate:" -ForegroundColor Cyan
Write-Host "  Directory : $($detected.Dir)" -ForegroundColor Cyan
Write-Host "  Core DLLs : $($detected.CoreCount)" -ForegroundColor Cyan
Write-Host "  Total DLLs: $($detected.DllCount)" -ForegroundColor Cyan

$overwrite = [bool]$Force
if (-not $NoCopyToSeed) {
    $seedCopied = Copy-FfmpegDlls -SourceDir $detected.Dir -DestinationDir $seedDir -Overwrite:$overwrite
    Write-Host "Copied $seedCopied FFmpeg DLL(s) to seed folder: $seedDir" -ForegroundColor Green
}
else {
    Write-Host "Skipped seed copy due to -NoCopyToSeed." -ForegroundColor Yellow
}

if ($CopyToRuntime) {
    $runtimeCopied = Copy-FfmpegDlls -SourceDir $detected.Dir -DestinationDir $runtimeDir -Overwrite:$overwrite
    Write-Host "Copied $runtimeCopied FFmpeg DLL(s) to runtime folder: $runtimeDir" -ForegroundColor Green
}

Remove-Item $extractRoot -Recurse -Force

Write-Host "Flyleaf FFmpeg retrieval complete." -ForegroundColor Cyan
