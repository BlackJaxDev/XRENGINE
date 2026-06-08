param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$RestoreVma,
    [switch]$ForceDownload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Find-MSBuild {
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $vsWhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path $vsWhere) {
            $path = & $vsWhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
            if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path $path)) {
                return $path
            }
        }

        $fallbacks = @(
            "Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
            "Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
            "Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe",
            "Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
            "Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
            "Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe"
        )

        foreach ($relativePath in $fallbacks) {
            $candidate = Join-Path $programFilesX86 $relativePath
            if (Test-Path $candidate) {
                return $candidate
            }
        }
    }

    return $null
}

function Invoke-Checked {
    param(
        [string]$Command,
        [string[]]$Arguments
    )

    Write-Host ">> $Command $($Arguments -join ' ')" -ForegroundColor Cyan
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command"
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$projectPath = Join-Path $repoRoot "Build\Native\VulkanMemoryAllocatorBridge\VulkanMemoryAllocatorBridge.vcxproj"
$headerPath = Join-Path $repoRoot "Build\Native\VulkanMemoryAllocatorBridge\vendor\VulkanMemoryAllocator\include\vk_mem_alloc.h"
$dependencyScript = Join-Path $repoRoot "Tools\Dependencies\Get-VulkanMemoryAllocator.ps1"
$nativeOutput = Join-Path $repoRoot "XREngine.Runtime.Rendering\runtimes\win-x64\native\VulkanMemoryAllocatorBridge.Native.dll"

if (-not (Test-Path $projectPath)) {
    throw "Native VMA bridge project was not found: $projectPath"
}

if ($RestoreVma -or $ForceDownload -or -not (Test-Path $headerPath)) {
    $dependencyArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $dependencyScript)
    if ($ForceDownload) {
        $dependencyArgs += "-ForceDownload"
    }

    Invoke-Checked -Command "powershell" -Arguments $dependencyArgs
}

if (-not (Test-Path $headerPath)) {
    throw "VMA header is missing. Run Tools\Dependencies\Get-VulkanMemoryAllocator.ps1 and try again."
}

if ([string]::IsNullOrWhiteSpace($env:VULKAN_SDK)) {
    throw "VULKAN_SDK is not set. Install the LunarG Vulkan SDK, open a new shell, and try again."
}

$vulkanHeader = Join-Path $env:VULKAN_SDK "Include\vulkan\vulkan.h"
$vulkanLib = Join-Path $env:VULKAN_SDK "Lib\vulkan-1.lib"
if (-not (Test-Path $vulkanHeader) -or -not (Test-Path $vulkanLib)) {
    throw "VULKAN_SDK does not contain Include\vulkan\vulkan.h and Lib\vulkan-1.lib: $env:VULKAN_SDK"
}

$msbuild = Find-MSBuild
if ([string]::IsNullOrWhiteSpace($msbuild)) {
    throw "Could not find Visual Studio MSBuild.exe. Install Visual Studio 2022 Build Tools with the 'Desktop development with C++' workload."
}

Invoke-Checked -Command $msbuild -Arguments @(
    $projectPath,
    "/m",
    "/nologo",
    "/p:Configuration=$Configuration;Platform=x64"
)

if (-not (Test-Path $nativeOutput)) {
    throw "Native bridge build completed, but expected DLL was not produced: $nativeOutput"
}

Write-Host "Built VulkanMemoryAllocatorBridge.Native.dll" -ForegroundColor Green
Write-Host "Output: $nativeOutput"
