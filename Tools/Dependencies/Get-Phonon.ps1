<#
.SYNOPSIS
    Downloads the Steam Audio (Phonon) native library and copies phonon.dll
    to the XREngine.Audio project's runtimes folder.

.DESCRIPTION
    Fetches a Steam Audio release zip from the official GitHub repository,
    extracts phonon.dll for the specified platform, and places it in the
    correct runtimes/ directory so the .NET build system copies it to
    the output alongside XREngine.Audio.dll.

    Default version matches the bindings in Phonon.cs (4.6.0).

.PARAMETER Version
    Steam Audio version to fetch (e.g. "4.6.0"). Defaults to "4.6.0".

.PARAMETER Rid
    .NET runtime identifier. Currently only "win-x64" is supported.

.PARAMETER Force
    Re-download even if the archive already exists locally.

.PARAMETER NoCopyToRuntime
    Download and extract only; do not copy phonon.dll into the runtimes tree.

.EXAMPLE
    pwsh Tools\Dependencies\Get-Phonon.ps1
    pwsh Tools\Dependencies\Get-Phonon.ps1 -Version 4.6.0 -Force
#>
[CmdletBinding()]
param(
    [string]$Version = "4.6.0",

    [ValidateSet("win-x64")]
    [string]$Rid = "win-x64",

    [switch]$Force,
    [switch]$NoCopyToRuntime
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Paths ---
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$downloadsDir = Join-Path $repoRoot "Build\Dependencies\SteamAudio"
$tempExtract = Join-Path $downloadsDir "_tmp_extract"

# Map RID → zip‑internal path to phonon.dll and destination runtimes dir
$ridMap = @{
    "win-x64" = @{
        ZipSubPath  = "steamaudio/lib/windows-x64/phonon.dll"
        RuntimeDir  = "XREngine.Audio\runtimes\win-x64\native"
        Binary      = "phonon.dll"
    }
}

if (-not $ridMap.ContainsKey($Rid)) {
    throw "Unsupported runtime identifier: $Rid. Currently supported: $($ridMap.Keys -join ', ')"
}

$info = $ridMap[$Rid]
$targetRuntimeDir = Join-Path $repoRoot $info.RuntimeDir
$targetBinary = Join-Path $targetRuntimeDir $info.Binary

# --- Skip if already present (unless -Force) ---
if ((Test-Path $targetBinary) -and -not $Force) {
    $size = (Get-Item $targetBinary).Length
    Write-Host "$($info.Binary) already exists at $targetRuntimeDir ($size bytes). Use -Force to re-download." -ForegroundColor Green
    return
}

# --- Download ---
New-Item -ItemType Directory -Path $downloadsDir -Force | Out-Null

$archiveName = "steamaudio_$Version.zip"
$archivePath = Join-Path $downloadsDir $archiveName
$downloadUrl = "https://github.com/ValveSoftware/steam-audio/releases/download/v$Version/$archiveName"

if ((-not (Test-Path $archivePath)) -or $Force) {
    Write-Host "Downloading Steam Audio $Version from:" -ForegroundColor Yellow
    Write-Host "  $downloadUrl" -ForegroundColor Yellow
    Invoke-WebRequest -Uri $downloadUrl -OutFile $archivePath -UseBasicParsing
    Write-Host "Downloaded to $archivePath" -ForegroundColor Green
}
else {
    Write-Host "Reusing existing archive at $archivePath" -ForegroundColor Green
}

if (-not (Test-Path $archivePath)) {
    throw "Download failed: archive not found at $archivePath"
}

# --- Extract ---
if (Test-Path $tempExtract) {
    Remove-Item $tempExtract -Recurse -Force
}

Write-Host "Extracting $archiveName..." -ForegroundColor Yellow
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $tempExtract)

$extractedBinary = Join-Path $tempExtract $info.ZipSubPath
if (-not (Test-Path $extractedBinary)) {
    # The zip structure may vary between releases. Try a fallback search.
    $fallback = Get-ChildItem -Path $tempExtract -Recurse -Filter $info.Binary | Select-Object -First 1
    if ($null -ne $fallback) {
        $extractedBinary = $fallback.FullName
        Write-Host "Found $($info.Binary) at fallback path: $extractedBinary" -ForegroundColor Yellow
    }
    else {
        Remove-Item $tempExtract -Recurse -Force
        throw "Unable to locate $($info.Binary) in extracted archive. Expected: $($info.ZipSubPath)"
    }
}

# --- Copy to runtimes ---
if (-not $NoCopyToRuntime) {
    New-Item -ItemType Directory -Path $targetRuntimeDir -Force | Out-Null
    Copy-Item $extractedBinary -Destination $targetBinary -Force
    $size = (Get-Item $targetBinary).Length
    Write-Host "Copied $($info.Binary) to $targetRuntimeDir ($size bytes)" -ForegroundColor Green
}

# --- Cleanup ---
Remove-Item $tempExtract -Recurse -Force

Write-Host "Steam Audio $Version ($Rid) setup complete." -ForegroundColor Cyan
