<#
.SYNOPSIS
    Downloads and stages the NVIDIA Streamline SDK runtime DLLs for XRENGINE.

.DESCRIPTION
    Retrieves an official NVIDIA-RTX/Streamline GitHub release archive, verifies
    its SHA-256 digest, and copies the production Windows x64 runtime files into:

      ThirdParty/NVIDIA/SDK/win-x64/

    The normal repo build then copies these files next to executable projects so
    DLSS, DLSS frame generation, and Reflex/Streamline runtime paths can load.

.PARAMETER Version
    Streamline release tag to install. Defaults to v2.12.0.

.PARAMETER ArchivePath
    Use an already downloaded Streamline SDK ZIP instead of downloading one.

.PARAMETER ExpectedSha256
    SHA-256 digest to require for the archive. Defaults to the pinned digest for
    v2.12.0 or the GitHub release asset digest when available.

.PARAMETER OutputDir
    Runtime DLL drop folder. Defaults to ThirdParty/NVIDIA/SDK/win-x64.

.PARAMETER Force
    Re-download the release archive and overwrite staged files.

.PARAMETER PreserveExisting
    Do not remove previously staged Streamline/DLSS runtime files before copying.

.PARAMETER SkipHashValidation
    Allow install when no expected SHA-256 is available. Avoid this for official
    public releases unless manually verifying the archive another way.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-StreamlineSdk.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Dependencies\Get-StreamlineSdk.ps1 -Force
#>
[CmdletBinding()]
param(
    [string]$Version = "v2.12.0",
    [string]$ArchivePath,
    [string]$ExpectedSha256,
    [string]$OutputDir,
    [switch]$Force,
    [switch]$PreserveExisting,
    [switch]$SkipHashValidation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Normalize-StreamlineVersion {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Version must not be empty."
    }

    $trimmed = $Value.Trim()
    if ($trimmed.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "v$($trimmed.Substring(1))"
    }

    return "v$trimmed"
}

function Get-KnownStreamlineSha256 {
    param([Parameter(Mandatory = $true)][string]$VersionTag)

    switch ($VersionTag.ToLowerInvariant()) {
        "v2.12.0" { return "f5c0a3d870707dddc3570fb4bcd3655cf48a8a68c3a9d342910cfa21b77dcf48" }
        "v2.11.1" { return "0c1d562e59557434cabfb8997157cb8c04fc7d23f077c8bdf5260975b73dfb89" }
        default { return $null }
    }
}

function Get-ReleaseAssetDigest {
    param([object]$Asset)

    if ($null -eq $Asset) {
        return $null
    }

    $properties = $Asset.PSObject.Properties
    if ($null -eq $properties["digest"]) {
        return $null
    }

    $digest = [string]$Asset.digest
    if ($digest -match "^sha256:([A-Fa-f0-9]{64})$") {
        return $Matches[1].ToLowerInvariant()
    }

    return $null
}

function Save-Url {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $tmpPath = "$Path.download"
    Remove-Item -LiteralPath $tmpPath -ErrorAction SilentlyContinue

    Write-Host "Downloading $Description from $Url" -ForegroundColor Yellow
    Invoke-WebRequest -Uri $Url -OutFile $tmpPath -UseBasicParsing

    if (-not (Test-Path -LiteralPath $tmpPath)) {
        throw "Download failed: $Description temporary file was not created."
    }

    Move-Item -LiteralPath $tmpPath -Destination $Path -Force
}

function Test-RequiredFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string[]]$FileNames
    )

    foreach ($fileName in $FileNames) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $fileName))) {
            return $false
        }
    }

    return $true
}

function Copy-ZipEntryToDirectory {
    param(
        [Parameter(Mandatory = $true)][System.IO.Compression.ZipArchiveEntry]$Entry,
        [Parameter(Mandatory = $true)][string]$Directory
    )

    $fileName = [System.IO.Path]::GetFileName($Entry.FullName)
    if ([string]::IsNullOrWhiteSpace($fileName)) {
        return
    }

    $destination = Join-Path $Directory $fileName
    [System.IO.Compression.ZipFileExtensions]::ExtractToFile($Entry, $destination, $true)
}

$versionTag = Normalize-StreamlineVersion -Value $Version
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$dependencyRoot = Join-Path $repoRoot "Build\Dependencies\Streamline"
$downloadDir = Join-Path $dependencyRoot "downloads"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "ThirdParty\NVIDIA\SDK\win-x64"
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Path $dependencyRoot -Force | Out-Null
New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$release = $null
$asset = $null

if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    $apiUrl = "https://api.github.com/repos/NVIDIA-RTX/Streamline/releases/tags/$versionTag"
    Write-Host "Querying Streamline release metadata from $apiUrl" -ForegroundColor Yellow
    $release = Invoke-RestMethod -Uri $apiUrl -Headers @{ "User-Agent" = "XRENGINE-Dependencies" } -UseBasicParsing
    if ($null -eq $release) {
        throw "Failed to load Streamline release metadata from GitHub."
    }

    $expectedAssetName = "streamline-sdk-$versionTag.zip"
    $asset = $release.assets |
        Where-Object { $_.name -ieq $expectedAssetName } |
        Select-Object -First 1

    if ($null -eq $asset) {
        $asset = $release.assets |
            Where-Object { $_.name -match "^streamline-sdk-.*\.zip$" } |
            Sort-Object name |
            Select-Object -First 1
    }

    if ($null -eq $asset) {
        throw "Could not find a Streamline SDK ZIP asset in release '$versionTag'."
    }

    $ArchivePath = Join-Path $downloadDir $asset.name
    $legacyArchivePath = Join-Path $dependencyRoot $asset.name
    if ((-not (Test-Path -LiteralPath $ArchivePath)) -and (Test-Path -LiteralPath $legacyArchivePath) -and -not $Force) {
        $ArchivePath = $legacyArchivePath
        Write-Host "Reusing existing archive at $ArchivePath" -ForegroundColor Green
    }
    elseif ((-not (Test-Path -LiteralPath $ArchivePath)) -or $Force) {
        Save-Url -Url $asset.browser_download_url -Path $ArchivePath -Description $asset.name
    }
    else {
        Write-Host "Reusing existing archive at $ArchivePath (use -Force to re-download)." -ForegroundColor Green
    }
}

$ArchivePath = [System.IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $ArchivePath)) {
    throw "Archive not found: $ArchivePath"
}

$expectedHash = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
    $expectedHash = $ExpectedSha256.Trim().ToLowerInvariant()
}
else {
    $expectedHash = Get-KnownStreamlineSha256 -VersionTag $versionTag
    if ([string]::IsNullOrWhiteSpace($expectedHash)) {
        $expectedHash = Get-ReleaseAssetDigest -Asset $asset
    }
}

if (-not [string]::IsNullOrWhiteSpace($expectedHash)) {
    if ($expectedHash -notmatch "^[a-f0-9]{64}$") {
        throw "Expected SHA-256 value is not a 64-character hex digest: $expectedHash"
    }

    $actualHash = (Get-FileHash -Algorithm SHA256 -Path $ArchivePath).Hash.ToLowerInvariant()
    if ($actualHash -ne $expectedHash) {
        throw "Streamline archive SHA-256 mismatch. Expected $expectedHash, found $actualHash."
    }

    Write-Host "Verified Streamline archive SHA-256: $actualHash" -ForegroundColor Green
}
elseif (-not $SkipHashValidation) {
    throw "No SHA-256 digest is known for $versionTag. Provide -ExpectedSha256 or rerun with -SkipHashValidation after manually verifying the archive."
}
else {
    Write-Host "WARNING: Skipping Streamline archive hash validation." -ForegroundColor Yellow
}

$requiredFiles = @(
    "sl.interposer.dll",
    "sl.common.dll",
    "sl.dlss.dll",
    "nvngx_dlss.dll",
    "sl.dlss_g.dll",
    "nvngx_dlssg.dll",
    "NvLowLatencyVk.dll",
    "sl.reflex.dll",
    "sl.pcl.dll",
    "nvngx_dlss.license.txt",
    "reflex.license.txt"
)

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($ArchivePath)
try {
    $entriesToCopy = @($archive.Entries | Where-Object {
        ($_.FullName -like "bin/x64/sl.*.dll") -or
        ($_.FullName -like "bin/x64/nvngx_*.dll") -or
        ($_.FullName -eq "bin/x64/NvLowLatencyVk.dll") -or
        ($_.FullName -like "bin/x64/*.license.txt") -or
        ($_.FullName -eq "license.txt") -or
        ($_.FullName -eq "3rd-party-licenses.md") -or
        ($_.FullName -eq "NVIDIA Nsight Perf SDK License (28Sept2022).pdf")
    })

    if ($null -eq $entriesToCopy -or $entriesToCopy.Count -eq 0) {
        throw "No production x64 Streamline runtime files were found under bin/x64 in $ArchivePath."
    }

    if (-not $PreserveExisting) {
        $deletePatterns = @(
            "sl.*.dll",
            "nvngx_*.dll",
            "NvLowLatencyVk.dll",
            "*.license.txt",
            "license.txt",
            "3rd-party-licenses.md",
            "NVIDIA Nsight Perf SDK License (28Sept2022).pdf"
        )

        foreach ($pattern in $deletePatterns) {
            Get-ChildItem -LiteralPath $OutputDir -Filter $pattern -File -ErrorAction SilentlyContinue |
                Remove-Item -Force
        }
    }

    foreach ($entry in $entriesToCopy) {
        Copy-ZipEntryToDirectory -Entry $entry -Directory $OutputDir
    }
}
finally {
    $archive.Dispose()
}

if (Get-Command Unblock-File -ErrorAction SilentlyContinue) {
    Get-ChildItem -LiteralPath $OutputDir -File | Unblock-File
}

if (-not (Test-RequiredFiles -Directory $OutputDir -FileNames $requiredFiles)) {
    $missing = $requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $OutputDir $_)) }
    throw "Streamline SDK install is incomplete. Missing: $($missing -join ', ')"
}

$installInfoPath = Join-Path $dependencyRoot "streamline-sdk-$versionTag.install.txt"
Set-Content -Path $installInfoPath -Value @(
    "Version: $versionTag",
    "Archive: $ArchivePath",
    "OutputDir: $OutputDir",
    "InstalledUtc: $([DateTime]::UtcNow.ToString('o'))"
)

Write-Host "Streamline SDK $versionTag setup complete." -ForegroundColor Cyan
Write-Host "Archive: $ArchivePath" -ForegroundColor Cyan
Write-Host "Runtime DLLs: $OutputDir" -ForegroundColor Cyan
Get-ChildItem -LiteralPath $OutputDir -File |
    Where-Object {
        $_.Name -like "sl.*.dll" -or
        $_.Name -like "nvngx_*.dll" -or
        $_.Name -eq "NvLowLatencyVk.dll" -or
        $_.Name -like "*.license.txt"
    } |
    Sort-Object Name |
    Select-Object Name, Length |
    Format-Table -AutoSize
