[CmdletBinding()]
param(
    [Parameter()]
    [string]$DownloadDirectory,

    [Parameter()]
    [switch]$DownloadOnly,

    [Parameter()]
    [switch]$ForceDownload,

    [Parameter()]
    [switch]$ForceInstall,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$InstallerArguments = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "The LunarG Windows Vulkan SDK installer can only run on Windows."
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
if ([string]::IsNullOrWhiteSpace($DownloadDirectory)) {
    $DownloadDirectory = Join-Path $repoRoot "Build\Dependencies\VulkanSdkInstaller"
}
elseif (-not [System.IO.Path]::IsPathRooted($DownloadDirectory)) {
    $DownloadDirectory = Join-Path $repoRoot $DownloadDirectory
}
$DownloadDirectory = [System.IO.Path]::GetFullPath($DownloadDirectory)

$latestVersionUri = "https://vulkan.lunarg.com/sdk/latest/windows.json"
$latest = Invoke-RestMethod -Uri $latestVersionUri -UseBasicParsing
$version = [string]$latest.windows
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "LunarG returned an invalid latest Windows SDK version: '$version'."
}

function Test-VulkanSdkRoot {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedVersion
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return $false
    }

    $manifestPath = Join-Path $Path "Bin\VkLayer_khronos_validation.json"
    $headerPath = Join-Path $Path "Include\vulkan\vulkan.h"
    $libraryPath = Join-Path $Path "Lib\vulkan-1.lib"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $headerPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $libraryPath -PathType Leaf)) {
        return $false
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    return [string]$manifest.layer.name -eq "VK_LAYER_KHRONOS_validation" -and
        [string]$manifest.layer.api_version -eq (($ExpectedVersion -split '\.')[0..2] -join '.')
}

function Find-InstalledVulkanSdk {
    param([Parameter(Mandatory)][string]$ExpectedVersion)

    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($scope in @("Process", "User", "Machine")) {
        $value = [Environment]::GetEnvironmentVariable("VULKAN_SDK", $scope)
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $candidates.Add($value)
        }
    }

    foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
        if (-not [string]::IsNullOrWhiteSpace($drive.Root)) {
            $candidates.Add((Join-Path $drive.Root "VulkanSDK\$ExpectedVersion"))
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $fullPath = [System.IO.Path]::GetFullPath($candidate)
        if (Test-VulkanSdkRoot -Path $fullPath -ExpectedVersion $ExpectedVersion) {
            return $fullPath
        }
    }

    return $null
}

$installedSdk = Find-InstalledVulkanSdk -ExpectedVersion $version
if (-not [string]::IsNullOrWhiteSpace($installedSdk) -and -not $ForceInstall -and -not $DownloadOnly) {
    Write-Host "Latest Vulkan SDK $version is already installed." -ForegroundColor Green
    Write-Host "SDK root: $installedSdk" -ForegroundColor Cyan
    Write-Host "Set VULKAN_SDK=$installedSdk in the shell that launches XRENGINE validation." -ForegroundColor Cyan
    exit 0
}

[System.IO.Directory]::CreateDirectory($DownloadDirectory) | Out-Null
$installerPath = Join-Path $DownloadDirectory "vulkansdk-windows-X64-$version.exe"
$temporaryPath = "$installerPath.download"
$downloadUri = "https://sdk.lunarg.com/sdk/download/$version/windows/vulkan_sdk.exe"
$shaUri = "https://sdk.lunarg.com/sdk/sha/$version/windows/vulkan_sdk.exe.json"
$shaMetadata = Invoke-RestMethod -Uri $shaUri -UseBasicParsing
$expectedSha256 = ([string]$shaMetadata.sha).Trim().ToUpperInvariant()
if ($expectedSha256 -notmatch '^[0-9A-F]{64}$') {
    throw "LunarG returned an invalid SHA-256 value for Vulkan SDK $version."
}

$downloadRequired = $ForceDownload -or -not (Test-Path -LiteralPath $installerPath -PathType Leaf)
if (-not $downloadRequired) {
    $existingSha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
    $downloadRequired = $existingSha256 -ne $expectedSha256
}

if ($downloadRequired) {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    Write-Host "Downloading Vulkan SDK $version from LunarG..." -ForegroundColor Yellow
    Invoke-WebRequest -Uri $downloadUri -OutFile $temporaryPath -UseBasicParsing

    $downloadedSha256 = (Get-FileHash -LiteralPath $temporaryPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($downloadedSha256 -ne $expectedSha256) {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        throw "Vulkan SDK checksum mismatch. Expected $expectedSha256, received $downloadedSha256."
    }

    Move-Item -LiteralPath $temporaryPath -Destination $installerPath -Force
}

$actualSha256 = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actualSha256 -ne $expectedSha256) {
    throw "Vulkan SDK installer checksum mismatch after download."
}

$signature = Get-AuthenticodeSignature -LiteralPath $installerPath
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "Vulkan SDK installer Authenticode signature is not valid: $($signature.StatusMessage)"
}

Write-Host "Verified Vulkan SDK $version installer." -ForegroundColor Green
Write-Host "Installer: $installerPath"
Write-Host "SHA-256:  $actualSha256"

if ($DownloadOnly) {
    exit 0
}

Write-Host "Launching the LunarG installer. Complete its interactive prompts to continue." -ForegroundColor Yellow
$startParameters = @{
    FilePath = $installerPath
    Wait = $true
    PassThru = $true
}
if ($InstallerArguments.Count -gt 0) {
    $startParameters.ArgumentList = $InstallerArguments
}

$installer = Start-Process @startParameters
if ($installer.ExitCode -ne 0) {
    throw "Vulkan SDK installer exited with code $($installer.ExitCode)."
}

$installedSdk = Find-InstalledVulkanSdk -ExpectedVersion $version
if ([string]::IsNullOrWhiteSpace($installedSdk)) {
    Write-Warning "The installer succeeded, but Vulkan SDK $version was not found through VULKAN_SDK or <drive>:\VulkanSDK\$version. Open a new shell and verify the selected install location."
    exit 0
}

Write-Host "Vulkan SDK $version installed and validated at $installedSdk." -ForegroundColor Green
Write-Host "Open a new shell so VULKAN_SDK and PATH reflect the installation." -ForegroundColor Cyan
