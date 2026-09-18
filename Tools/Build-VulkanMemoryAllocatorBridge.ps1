param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$RestoreVma,
    [switch]$ForceDownload,
    [switch]$StageRuntime
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Find-MSBuild {
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $vsWhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path $vsWhere) {
            # MSBuild-only installations cannot build the native C++ bridge.
            $path = & $vsWhere -latest -products * -requires Microsoft.Component.MSBuild Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find "MSBuild\Current\Bin\MSBuild.exe" | Select-Object -First 1
            if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path $path)) {
                return $path
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
$nativeOutput = Join-Path $repoRoot "Build\_AgentValidation\00000000-000000-shared\tools\bin\VulkanMemoryAllocatorBridge\$Configuration\VulkanMemoryAllocatorBridge.Native.dll"
$packagedOutput = Join-Path $repoRoot "XREngine.Runtime.Rendering.Vulkan\runtimes\win-x64\native\VulkanMemoryAllocatorBridge.Native.dll"

if ($StageRuntime -and $Configuration -ne "Release") {
    throw "Only Release builds may be staged into the runtime package."
}

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
    throw "Could not find Visual Studio MSBuild with C++ tools. Install Visual Studio or Build Tools with the 'Desktop development with C++' workload and the MSVC v143 x64/x86 build tools."
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

if ($StageRuntime) {
    Copy-Item -Path $nativeOutput -Destination $packagedOutput -Force
    Write-Host "Staged Release bridge into the runtime package" -ForegroundColor Green
    Write-Host "Package: $packagedOutput"
}

Write-Host "Built VulkanMemoryAllocatorBridge.Native.dll" -ForegroundColor Green
Write-Host "Output: $nativeOutput"
