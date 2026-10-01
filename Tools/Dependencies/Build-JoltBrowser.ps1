[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [string]$DotNetRoot,
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$nativePin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'JoltBrowser.lock.json') -Raw | ConvertFrom-Json
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$validationRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'Build/_AgentValidation'))
$dependencyRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'Build/Dependencies/JoltBrowser'))
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not $outputRoot.StartsWith($validationRoot + [IO.Path]::DirectorySeparatorChar, $pathComparison) -and
    -not $outputRoot.StartsWith($dependencyRoot + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
    throw 'Browser native build output must be inside Build/Dependencies/JoltBrowser or a reserved Build/_AgentValidation task run.'
}
if ([string]::IsNullOrWhiteSpace($DotNetRoot)) {
    $DotNetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
}
$packsRoot = Join-Path $DotNetRoot 'packs'
$hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$hostOs = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { throw 'Unsupported native build host operating system.' }
$hostRuntime = "$hostOs-$hostArchitecture"
$packSuffix = "$($nativePin.emscriptenVersion).{0}.$hostRuntime/$($nativePin.sdkPackVersion)/tools"
$sdkRoot = Join-Path $packsRoot ('Microsoft.NET.Runtime.Emscripten.' + ($packSuffix -f 'Sdk'))
$nodeRoot = Join-Path $packsRoot ('Microsoft.NET.Runtime.Emscripten.' + ($packSuffix -f 'Node'))
$cacheRoot = Join-Path $packsRoot ('Microsoft.NET.Runtime.Emscripten.' + ($packSuffix -f 'Cache') + '/emscripten/cache')
if ($IsWindows) {
    $pythonRoot = Join-Path $packsRoot ('Microsoft.NET.Runtime.Emscripten.' + ($packSuffix -f 'Python'))
    $python = Join-Path $pythonRoot 'python.exe'
}
else {
    # Unix .NET Emscripten packs use the host Python interpreter, matching the
    # WebAssembly SDK invocation. No Python package is installed by this script.
    $pythonCommand = Get-Command python3 -ErrorAction SilentlyContinue
    $python = if ($null -ne $pythonCommand) { $pythonCommand.Source } else { '' }
}
$emcmake = Join-Path $sdkRoot 'emscripten/emcmake.py'
$node = Join-Path $nodeRoot $(if ($IsWindows) { 'bin/node.exe' } else { 'bin/node' })

if ($PlanOnly) {
    [pscustomobject]@{
        JoltCCommit = $nativePin.joltcCommit
        JoltCommit = $nativePin.joltCommit
        Emscripten = $nativePin.emscriptenVersion
        SdkPack = $nativePin.sdkPackVersion
        HostRuntime = $hostRuntime
        EmscriptenToolsAvailable = (Test-Path -LiteralPath $emcmake -PathType Leaf) -and (Test-Path -LiteralPath $node -PathType Leaf)
        ToolchainUsesPython = $true
        DesktopSupply = $nativePin.desktopSupply
        NativeLicense = $nativePin.license
        SingleThreaded = $true
        DeterminismClaimed = $false
    }
    return
}

if ([string]::IsNullOrWhiteSpace($python)) {
    throw 'Emscripten requires a host Python 3 interpreter on Unix. Install the supported toolchain prerequisite separately.'
}
foreach ($requiredTool in @($python, $emcmake, $node)) {
    if (-not (Test-Path -LiteralPath $requiredTool -PathType Leaf)) {
        throw "Required WebAssembly tool is absent: $requiredTool. Install the matching supported workload separately."
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $cacheRoot 'sysroot_install.stamp') -PathType Leaf)) {
    throw 'The pinned Emscripten cache is incomplete; its sysroot installation stamp is absent.'
}
$null = Get-Command cmake, ninja, git -ErrorAction Stop

function Invoke-CheckedNativeCommand {
    param([string]$Executable, [string[]]$Arguments)
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Native build command failed with exit code $LASTEXITCODE." }
}

function Get-PinnedNativeSource {
    param([string]$Url, [string]$Commit, [string]$Destination)
    if (-not (Test-Path -LiteralPath $Destination)) {
        Invoke-CheckedNativeCommand git @('clone', '--no-checkout', '--filter=blob:none', $Url, $Destination)
        Invoke-CheckedNativeCommand git @('-C', $Destination, 'checkout', '--detach', $Commit)
    }
    $actualCommit = (& git -C $Destination rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $Commit) { throw 'Native source checkout does not match the committed pin.' }
    $sourceChanges = @(& git -C $Destination status --porcelain --untracked-files=all)
    if ($LASTEXITCODE -ne 0 -or $sourceChanges.Count -ne 0) { throw 'Pinned native source is not clean.' }
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$sources = Join-Path $outputRoot 'sources'
New-Item -ItemType Directory -Path $sources -Force | Out-Null
$joltSource = Join-Path $sources 'JoltPhysics'
$joltCSource = Join-Path $sources 'joltc'
Get-PinnedNativeSource $nativePin.joltRepository $nativePin.joltCommit $joltSource
Get-PinnedNativeSource $nativePin.joltcRepository $nativePin.joltcCommit $joltCSource

$envNames = @('DOTNET_EMSCRIPTEN_LLVM_ROOT', 'DOTNET_EMSCRIPTEN_NODE_JS', 'DOTNET_EMSCRIPTEN_BINARYEN_ROOT', 'EMSDK_PYTHON', 'EM_CACHE', 'FROZEN_CACHE')
$previousEnvironment = @{}
foreach ($envName in $envNames) { $previousEnvironment[$envName] = [Environment]::GetEnvironmentVariable($envName, 'Process') }
try {
    [Environment]::SetEnvironmentVariable('DOTNET_EMSCRIPTEN_LLVM_ROOT', (Join-Path $sdkRoot 'bin'), 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_EMSCRIPTEN_NODE_JS', $node, 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_EMSCRIPTEN_BINARYEN_ROOT', $sdkRoot, 'Process')
    [Environment]::SetEnvironmentVariable('EMSDK_PYTHON', $python, 'Process')
    [Environment]::SetEnvironmentVariable('EM_CACHE', $cacheRoot, 'Process')
    [Environment]::SetEnvironmentVariable('FROZEN_CACHE', 'True', 'Process')
    $buildRoot = Join-Path $outputRoot 'cmake'
    $ownedCMakeRoot = Join-Path $PSScriptRoot 'JoltBrowser'
    $joltCSourceCMake = $joltCSource.Replace('\', '/')
    $joltSourceCMake = $joltSource.Replace('\', '/')
    $configure = @($emcmake, 'cmake', '-S', $ownedCMakeRoot, '-B', $buildRoot, '-G', 'Ninja',
        '-DCMAKE_BUILD_TYPE=Release', "-DXRE_JOLTC_SOURCE=$joltCSourceCMake", "-DJOLT_PHYSICS_ROOT=$joltSourceCMake", '-DJPH_BUILD_SHARED=OFF',
        '-DJPH_SAMPLES=OFF', '-DJPH_INSTALL=OFF', '-DJPH_USE_DX12=OFF', '-DJPH_USE_VK=OFF',
        '-DJPH_USE_MTL=OFF', '-DJPH_USE_CPU_COMPUTE=OFF', '-DCROSS_PLATFORM_DETERMINISTIC=OFF',
        '-DUSE_WASM_SIMD=ON', '-DINTERPROCEDURAL_OPTIMIZATION=OFF')
    Invoke-CheckedNativeCommand $python $configure
    Invoke-CheckedNativeCommand cmake @('--build', $buildRoot, '--target', 'joltc', '--parallel', '4')
    $archives = @(Get-ChildItem -LiteralPath $buildRoot -Filter '*.a' -Recurse | Where-Object { $_.Name -in @('libjoltc.a', 'libJolt.a') })
    if ($archives.Count -ne 2) { throw 'Expected both the joltc and Jolt static archives.' }
    $archiveOutput = Join-Path $outputRoot 'archives'
    New-Item -ItemType Directory -Path $archiveOutput -Force | Out-Null
    foreach ($archive in $archives) { Copy-Item -LiteralPath $archive.FullName -Destination (Join-Path $archiveOutput $archive.Name) }
    Copy-Item -LiteralPath (Join-Path $joltCSource 'LICENSE') -Destination (Join-Path $archiveOutput 'joltc-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $joltSource 'LICENSE') -Destination (Join-Path $archiveOutput 'Jolt-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'JoltBrowser.lock.json') -Destination (Join-Path $archiveOutput 'native-build-pin.json')
    Write-Output $archiveOutput
}
finally {
    foreach ($envName in $envNames) { [Environment]::SetEnvironmentVariable($envName, $previousEnvironment[$envName], 'Process') }
}
