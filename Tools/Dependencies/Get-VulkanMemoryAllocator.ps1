param(
    [string]$Version = "v3.3.0",
    [switch]$ForceDownload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Normalize-VmaVersion {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "Version must not be empty."
    }

    $trimmed = $Value.Trim()
    if ($trimmed.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "v$($trimmed.Substring(1))"
    }

    return "v$trimmed"
}

function Get-HeaderVersion {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return $null
    }

    $match = Select-String -Path $Path -Pattern '<b>Version\s+([0-9]+(?:\.[0-9]+)+)</b>' -AllMatches | Select-Object -First 1
    if (-not $match) {
        return $null
    }

    return "v$($match.Matches[0].Groups[1].Value)"
}

function Save-Url {
    param(
        [string]$Url,
        [string]$Path,
        [string]$Description
    )

    $tmpPath = "$Path.download"
    Remove-Item -Path $tmpPath -ErrorAction SilentlyContinue

    Write-Host "Downloading $Description from $Url" -ForegroundColor Yellow
    Invoke-WebRequest -Uri $Url -OutFile $tmpPath -UseBasicParsing

    if (-not (Test-Path $tmpPath)) {
        throw "Download failed: $Description temporary file was not created."
    }

    Move-Item -Path $tmpPath -Destination $Path -Force
}

$versionTag = Normalize-VmaVersion -Value $Version
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$vendorRoot = Join-Path $repoRoot "Build\Native\VulkanMemoryAllocatorBridge\vendor\VulkanMemoryAllocator"
$includeDir = Join-Path $vendorRoot "include"
$headerPath = Join-Path $includeDir "vk_mem_alloc.h"
$licensePath = Join-Path $vendorRoot "LICENSE.txt"
$versionPath = Join-Path $vendorRoot "VERSION.txt"

New-Item -ItemType Directory -Path $includeDir -Force | Out-Null

$existingHeaderVersion = Get-HeaderVersion -Path $headerPath
$needsDownload = $ForceDownload -or -not (Test-Path $headerPath) -or -not (Test-Path $licensePath) -or ($existingHeaderVersion -ne $versionTag)

if ($needsDownload) {
    $baseUrl = "https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/VulkanMemoryAllocator/$versionTag"
    Save-Url -Url "$baseUrl/include/vk_mem_alloc.h" -Path $headerPath -Description "VMA $versionTag header"
    Save-Url -Url "$baseUrl/LICENSE.txt" -Path $licensePath -Description "VMA $versionTag license"
}
else {
    Write-Host "VMA $versionTag already exists at $vendorRoot (use -ForceDownload to refresh)." -ForegroundColor Green
}

$headerText = Get-Content -Path $headerPath -Raw
if (-not $headerText.Contains("Vulkan Memory Allocator") -or -not $headerText.Contains("VMA_IMPLEMENTATION")) {
    throw "Downloaded vk_mem_alloc.h does not look like the Vulkan Memory Allocator header."
}

$actualHeaderVersion = Get-HeaderVersion -Path $headerPath
if ($actualHeaderVersion -ne $versionTag) {
    throw "VMA header version mismatch. Expected $versionTag, found $actualHeaderVersion."
}

$licenseText = Get-Content -Path $licensePath -Raw
if (-not $licenseText.Contains("Permission is hereby granted")) {
    throw "VMA LICENSE.txt does not contain the expected MIT license grant."
}

Set-Content -Path $versionPath -Value $versionTag -NoNewline

Write-Host "VMA $versionTag is ready." -ForegroundColor Green
Write-Host "Header:  $headerPath"
Write-Host "License: $licensePath"
