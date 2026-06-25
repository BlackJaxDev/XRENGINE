[CmdletBinding()]
param(
    [Parameter()]
    [string]$SourceUrl = "https://gitlab.freedesktop.org/monado/monado.git",

    [Parameter()]
    [string]$Ref = "main",

    [Parameter()]
    [string]$SourceDir = "Build\Submodules\monado",

    [Parameter()]
    [string]$BuildDir = "Build\Submodules\monado\build",

    [Parameter()]
    [string]$InstallDir = "Build\Deps\Monado",

    [Parameter()]
    [string]$VcpkgDir,

    [Parameter()]
    [ValidateSet("Debug", "Release", "RelWithDebInfo")]
    [string]$Configuration = "Release",

    [Parameter()]
    [ValidateSet("AnyCPU", "x64")]
    [string]$EditorPlatform = "AnyCPU",

    [Parameter()]
    [string]$EditorConfiguration = "Debug",

    [Parameter()]
    [string]$VsVcVars64Bat,

    [Parameter()]
    [string[]]$ExtraCMakeArgs = @(),

    [Parameter()]
    [switch]$InstallPrerequisites,

    [Parameter()]
    [switch]$ForceClone,

    [Parameter()]
    [switch]$ForceVcpkg,

    [Parameter()]
    [switch]$NoFetch,

    [Parameter()]
    [switch]$Clean,

    [Parameter()]
    [switch]$SkipBuild,

    [Parameter()]
    [switch]$SkipInstall,

    [Parameter()]
    [switch]$SkipOpenXrLoaderInstall,

    [Parameter()]
    [switch]$SkipEditorLoaderInstall,

    [Parameter()]
    [switch]$SetUserEnvironment
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$script:CMakePath = $null
$script:NinjaPath = $null
$script:PythonPath = $null

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if ([System.IO.Path]::IsPathRooted($expanded)) {
        return [System.IO.Path]::GetFullPath($expanded)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $expanded))
}

function Quote-CmdArgument {
    param([string]$Argument)

    if ($null -eq $Argument -or $Argument.Length -eq 0) {
        return '""'
    }

    return '"' + ($Argument -replace '"', '\"') + '"'
}

function Join-CmdArguments {
    param([Parameter(Mandatory)][string[]]$Arguments)

    return ($Arguments | ForEach-Object { Quote-CmdArgument $_ }) -join " "
}

function Invoke-Native {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter()][string[]]$Arguments = @(),
        [Parameter()][string]$WorkingDirectory = $repoRoot
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-VsNative {
    param(
        [Parameter(Mandatory)][string]$VcVars64Bat,
        [Parameter(Mandatory)][string]$Command,
        [Parameter()][string[]]$Arguments = @(),
        [Parameter()][string]$WorkingDirectory = $repoRoot
    )

    $commandLine = "cd /d $(Quote-CmdArgument $WorkingDirectory) && call $(Quote-CmdArgument $VcVars64Bat) >nul && $(Quote-CmdArgument $Command)"
    if ($Arguments.Count -gt 0) {
        $commandLine += " " + (Join-CmdArguments $Arguments)
    }

    & cmd.exe /d /s /c $commandLine
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command $($Arguments -join ' ')"
    }
}

function Find-RequiredCommand {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter()][string]$InstallHint
    )

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    if (-not [string]::IsNullOrWhiteSpace($InstallHint)) {
        throw "Required command '$Name' was not found. $InstallHint"
    }

    throw "Required command '$Name' was not found on PATH."
}

function Install-WingetPackage {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Name
    )

    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($null -eq $winget) {
        throw "winget is not available. Install $Name manually or rerun without -InstallPrerequisites after it is on PATH."
    }

    Write-Host "Installing $Name with winget..." -ForegroundColor Yellow
    & $winget.Source install --id $Id --source winget --exact --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install $Name ($Id)."
    }
}

function Update-ProcessEnvironmentFromRegistry {
    $pathValues = @(
        $env:Path,
        [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::Machine),
        [Environment]::GetEnvironmentVariable("Path", [EnvironmentVariableTarget]::User)
    )

    $entries = [System.Collections.Generic.List[string]]::new()
    $seen = @{}
    foreach ($pathValue in $pathValues) {
        if ([string]::IsNullOrWhiteSpace($pathValue)) {
            continue
        }

        foreach ($entry in ($pathValue -split ";")) {
            if ([string]::IsNullOrWhiteSpace($entry)) {
                continue
            }

            $normalized = $entry.Trim().TrimEnd("\").ToUpperInvariant()
            if (-not $seen.ContainsKey($normalized)) {
                $seen[$normalized] = $true
                $entries.Add($entry.Trim()) | Out-Null
            }
        }
    }

    if ($entries.Count -gt 0) {
        $env:Path = $entries -join ";"
    }

    foreach ($name in @("VULKAN_SDK", "VCPKG_ROOT")) {
        $currentValue = [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::Process)
        if (-not [string]::IsNullOrWhiteSpace($currentValue)) {
            continue
        }

        $value = [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::User)
        if ([string]::IsNullOrWhiteSpace($value)) {
            $value = [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::Machine)
        }

        if (-not [string]::IsNullOrWhiteSpace($value)) {
            Set-Item -LiteralPath "Env:$name" -Value $value
        }
    }
}

function Ensure-Prerequisites {
    if ($InstallPrerequisites) {
        Update-ProcessEnvironmentFromRegistry

        if ($null -eq (Get-Command cmake -ErrorAction SilentlyContinue)) {
            Install-WingetPackage -Id "Kitware.CMake" -Name "CMake"
        }
        if ($null -eq (Get-Command ninja -ErrorAction SilentlyContinue)) {
            Install-WingetPackage -Id "Ninja-build.Ninja" -Name "Ninja"
        }
        if ($null -eq (Get-Command python -ErrorAction SilentlyContinue)) {
            Install-WingetPackage -Id "Python.Python.3.13" -Name "Python"
        }
        if ([string]::IsNullOrWhiteSpace($env:VULKAN_SDK)) {
            Install-WingetPackage -Id "KhronosGroup.VulkanSDK" -Name "Vulkan SDK"
        }

        Update-ProcessEnvironmentFromRegistry
    }

    Find-RequiredCommand -Name "git" -InstallHint "Install Git for Windows and open a new shell." | Out-Null
    $script:CMakePath = Find-RequiredCommand -Name "cmake" -InstallHint "Install CMake or rerun with -InstallPrerequisites."
    $script:NinjaPath = Find-RequiredCommand -Name "ninja" -InstallHint "Install Ninja or rerun with -InstallPrerequisites."
    $script:PythonPath = Find-RequiredCommand -Name "python" -InstallHint "Install Python 3 or rerun with -InstallPrerequisites."

    if ([string]::IsNullOrWhiteSpace($env:VULKAN_SDK)) {
        throw "VULKAN_SDK is not set. Install the Vulkan SDK, open a new shell, and rerun this script."
    }
}

function Find-VcVars64 {
    if (-not [string]::IsNullOrWhiteSpace($VsVcVars64Bat)) {
        $resolved = Resolve-FullPath $VsVcVars64Bat
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Explicit vcvars64.bat was not found: $resolved"
        }
        return $resolved
    }

    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $vsWhere = Join-Path $programFilesX86 "Microsoft Visual Studio\Installer\vswhere.exe"
        if (Test-Path -LiteralPath $vsWhere -PathType Leaf) {
            $installPath = & $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($installPath)) {
                $candidate = Join-Path $installPath "VC\Auxiliary\Build\vcvars64.bat"
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    return [System.IO.Path]::GetFullPath($candidate)
                }
            }
        }
    }

    $candidates = @(
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat",
        "$env:ProgramFiles\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
    )

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw "Could not find Visual Studio C++ vcvars64.bat. Install Visual Studio 2022 Build Tools with the Desktop development with C++ workload."
}

function Ensure-Vcpkg {
    param([Parameter(Mandatory)][string]$VcpkgRoot)

    if ($ForceVcpkg -and (Test-Path -LiteralPath $VcpkgRoot)) {
        Remove-Item -LiteralPath $VcpkgRoot -Recurse -Force
    }

    if (-not (Test-Path -LiteralPath $VcpkgRoot -PathType Container)) {
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $VcpkgRoot)) | Out-Null
        Invoke-Native -FilePath "git" -Arguments @("clone", "https://github.com/microsoft/vcpkg.git", $VcpkgRoot) | Out-Host
    }
    elseif (-not $NoFetch) {
        Invoke-Native -FilePath "git" -Arguments @("-C", $VcpkgRoot, "fetch", "--tags", "origin") | Out-Host
        Invoke-Native -FilePath "git" -Arguments @("-C", $VcpkgRoot, "pull", "--ff-only") | Out-Host
    }

    $bootstrap = Join-Path $VcpkgRoot "bootstrap-vcpkg.bat"
    $vcpkgExe = Join-Path $VcpkgRoot "vcpkg.exe"
    if ($ForceVcpkg -or -not (Test-Path -LiteralPath $vcpkgExe -PathType Leaf)) {
        Invoke-Native -FilePath $bootstrap -Arguments @("-disableMetrics") -WorkingDirectory $VcpkgRoot | Out-Host
    }

    $toolchain = Join-Path $VcpkgRoot "scripts\buildsystems\vcpkg.cmake"
    if (-not (Test-Path -LiteralPath $toolchain -PathType Leaf)) {
        throw "vcpkg toolchain file was not found: $toolchain"
    }

    return [System.IO.Path]::GetFullPath($toolchain)
}

function Remove-RepoLocalDirectoryTree {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Reason
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $repoPrefix = $repoRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove $Reason outside the repository root: $fullPath"
    }

    Write-Warning "Removing $Reason`: $fullPath"
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

function Reset-StaleCMakeBuildCache {
    param(
        [Parameter(Mandatory)][string]$BuildRoot,
        [Parameter(Mandatory)][string]$ExpectedToolchain
    )

    $cachePath = Join-Path $BuildRoot "CMakeCache.txt"
    if (-not (Test-Path -LiteralPath $cachePath -PathType Leaf)) {
        return
    }

    $cacheText = Get-Content -LiteralPath $cachePath -Raw
    $needsReset = $false
    if ($cacheText -match "Downloading https://github\.com/microsoft/vcpkg-tool") {
        $needsReset = $true
    }
    elseif ($cacheText -match "(?m)^CMAKE_MAKE_PROGRAM:FILEPATH=CMAKE_MAKE_PROGRAM-NOTFOUND$") {
        $needsReset = $true
    }
    elseif ($cacheText -match "(?m)^CMAKE_TOOLCHAIN_FILE:[^=]*=(.+)$") {
        $cachedToolchain = $Matches[1].Trim().Trim('"')
        try {
            $cachedToolchain = [System.IO.Path]::GetFullPath($cachedToolchain)
            $expectedToolchain = [System.IO.Path]::GetFullPath($ExpectedToolchain)
            if (-not [string]::Equals($cachedToolchain, $expectedToolchain, [System.StringComparison]::OrdinalIgnoreCase)) {
                $needsReset = $true
            }
        }
        catch {
            $needsReset = $true
        }
    }

    if ($needsReset) {
        Remove-RepoLocalDirectoryTree -Path $BuildRoot -Reason "stale Monado CMake build directory"
    }
}

function Sync-MonadoSource {
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$RepositoryUrl,
        [Parameter(Mandatory)][string]$Revision
    )

    if ($ForceClone -and (Test-Path -LiteralPath $SourceRoot)) {
        Remove-Item -LiteralPath $SourceRoot -Recurse -Force
    }

    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $SourceRoot)) | Out-Null
        Invoke-Native -FilePath "git" -Arguments @("clone", "--recursive", $RepositoryUrl, $SourceRoot)
    }
    elseif (-not $NoFetch) {
        Invoke-Native -FilePath "git" -Arguments @("-C", $SourceRoot, "fetch", "--tags", "origin")
    }

    Invoke-Native -FilePath "git" -Arguments @("-C", $SourceRoot, "checkout", $Revision)
    if (-not $NoFetch) {
        Invoke-Native -FilePath "git" -Arguments @("-C", $SourceRoot, "submodule", "update", "--init", "--recursive")
    }
}

function Resolve-RuntimeLibraryPath {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$LibraryPath
    )

    $expanded = [Environment]::ExpandEnvironmentVariables($LibraryPath)
    if ([System.IO.Path]::IsPathRooted($expanded)) {
        return [System.IO.Path]::GetFullPath($expanded)
    }

    return [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $ManifestPath) $expanded))
}

function Test-RuntimeManifest {
    param(
        [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter()][ref]$ManifestInfo
    )

    try {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        if ($null -eq $manifest.runtime) {
            return $false
        }

        $libraryPath = [string]$manifest.runtime.library_path
        if ([string]::IsNullOrWhiteSpace($libraryPath)) {
            return $false
        }

        $resolvedLibraryPath = Resolve-RuntimeLibraryPath -ManifestPath $ManifestPath -LibraryPath $libraryPath
        if (-not (Test-Path -LiteralPath $resolvedLibraryPath -PathType Leaf)) {
            return $false
        }

        $runtimeVersion = ""
        $apiVersionProperty = $manifest.runtime.PSObject.Properties["api_version"]
        if ($null -ne $apiVersionProperty) {
            $runtimeVersion = [string]$apiVersionProperty.Value
        }

        $ManifestInfo.Value = [pscustomobject]@{
            RuntimeJson       = [System.IO.Path]::GetFullPath($ManifestPath)
            RuntimeName       = [string]$manifest.runtime.name
            RuntimeVersion    = $runtimeVersion
            LibraryPath       = $resolvedLibraryPath
            ManifestDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($ManifestPath))
        }
        return $true
    }
    catch {
        return $false
    }
}

function Find-MonadoManifest {
    param(
        [Parameter(Mandatory)][string]$InstallRoot,
        [Parameter(Mandatory)][string]$BuildRoot
    )

    $candidates = @(
        (Join-Path $InstallRoot "share\openxr\1\openxr_monado.json"),
        (Join-Path $InstallRoot "share\openxr\1\openxr_monado-dev.json"),
        (Join-Path $InstallRoot "openxr_monado.json"),
        (Join-Path $InstallRoot "openxr_monado-dev.json"),
        (Join-Path $BuildRoot "openxr_monado.json"),
        (Join-Path $BuildRoot "openxr_monado-dev.json")
    )

    $extra = Get-ChildItem -LiteralPath $InstallRoot -Filter "openxr_monado*.json" -File -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName }
    $extra += Get-ChildItem -LiteralPath $BuildRoot -Filter "openxr_monado*.json" -File -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object { $_.FullName }

    foreach ($candidate in (($candidates + $extra) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        $info = $null
        if (Test-RuntimeManifest -ManifestPath $candidate -ManifestInfo ([ref]$info)) {
            return $info
        }
    }

    throw "Could not find a usable openxr_monado*.json manifest in $InstallRoot or $BuildRoot."
}

function Find-OpenXrLoader {
    param(
        [Parameter(Mandatory)][string]$InstallRoot,
        [Parameter(Mandatory)][string]$BuildRoot,
        [Parameter(Mandatory)][string]$VcpkgRoot
    )

    $candidates = @(
        (Join-Path $InstallRoot "bin\openxr_loader.dll"),
        (Join-Path $BuildRoot "vcpkg_installed\x64-windows\bin\openxr_loader.dll"),
        (Join-Path $BuildRoot "vcpkg_installed\x64-windows\debug\bin\openxr_loader.dll"),
        (Join-Path $VcpkgRoot "installed\x64-windows\bin\openxr_loader.dll")
    )

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

function Ensure-OpenXrLoader {
    param(
        [Parameter(Mandatory)][string]$InstallRoot,
        [Parameter(Mandatory)][string]$BuildRoot,
        [Parameter(Mandatory)][string]$VcpkgRoot
    )

    $loaderPath = Find-OpenXrLoader -InstallRoot $InstallRoot -BuildRoot $BuildRoot -VcpkgRoot $VcpkgRoot
    if (-not [string]::IsNullOrWhiteSpace($loaderPath)) {
        return $loaderPath
    }

    if ($SkipOpenXrLoaderInstall) {
        return $null
    }

    $vcpkgExe = Join-Path $VcpkgRoot "vcpkg.exe"
    if (-not (Test-Path -LiteralPath $vcpkgExe -PathType Leaf)) {
        throw "vcpkg.exe was not found while trying to install openxr-loader: $vcpkgExe"
    }

    Write-Host "Installing Khronos OpenXR loader through vcpkg..." -ForegroundColor Yellow
    Invoke-Native -FilePath $vcpkgExe -Arguments @("install", "openxr-loader:x64-windows", "--disable-metrics") -WorkingDirectory $VcpkgRoot | Out-Host

    $loaderPath = Find-OpenXrLoader -InstallRoot $InstallRoot -BuildRoot $BuildRoot -VcpkgRoot $VcpkgRoot
    if ([string]::IsNullOrWhiteSpace($loaderPath)) {
        throw "vcpkg installed openxr-loader, but openxr_loader.dll was not found under $VcpkgRoot or $BuildRoot."
    }

    return $loaderPath
}

function Install-OpenXrLoaderToEditorOutput {
    param([Parameter(Mandatory)][string]$LoaderPath)

    $editorOutput = Join-Path $repoRoot "Build\Editor\$EditorConfiguration\$EditorPlatform\$EditorConfiguration\net10.0-windows7.0"
    if (-not (Test-Path -LiteralPath $editorOutput -PathType Container)) {
        Write-Warning "Editor output directory does not exist yet; skipping openxr_loader.dll copy: $editorOutput"
        return $null
    }

    $loaderDirectory = Split-Path -Parent $LoaderPath
    foreach ($dll in Get-ChildItem -LiteralPath $loaderDirectory -Filter "*.dll" -File -ErrorAction SilentlyContinue) {
        Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $editorOutput $dll.Name) -Force
    }

    $destination = Join-Path $editorOutput "openxr_loader.dll"
    return [System.IO.Path]::GetFullPath($destination)
}

function Copy-MonadoRuntimeDependencies {
    param(
        [Parameter(Mandatory)][string]$InstallRoot,
        [Parameter(Mandatory)][string]$BuildRoot,
        [Parameter(Mandatory)][string]$VcpkgRoot,
        [string]$OpenXrLoaderPath
    )

    $installBin = Join-Path $InstallRoot "bin"
    [System.IO.Directory]::CreateDirectory($installBin) | Out-Null

    $copied = [System.Collections.Generic.List[string]]::new()
    $sourceDirectories = @(
        (Join-Path $VcpkgRoot "installed\x64-windows\bin"),
        (Join-Path $BuildRoot "vcpkg_installed\x64-windows\bin"),
        (Join-Path $BuildRoot "src\xrt\targets\service")
    )

    foreach ($sourceDirectory in ($sourceDirectories | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
            continue
        }

        foreach ($dll in Get-ChildItem -LiteralPath $sourceDirectory -Filter "*.dll" -File -ErrorAction SilentlyContinue) {
            $destination = Join-Path $installBin $dll.Name
            Copy-Item -LiteralPath $dll.FullName -Destination $destination -Force
            $copied.Add([System.IO.Path]::GetFullPath($destination))
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($OpenXrLoaderPath) -and (Test-Path -LiteralPath $OpenXrLoaderPath -PathType Leaf)) {
        $destination = Join-Path $installBin "openxr_loader.dll"
        Copy-Item -LiteralPath $OpenXrLoaderPath -Destination $destination -Force
        $copied.Add([System.IO.Path]::GetFullPath($destination))
    }

    return @($copied | Select-Object -Unique)
}

Ensure-Prerequisites

if ([string]::IsNullOrWhiteSpace($VcpkgDir)) {
    if (-not [string]::IsNullOrWhiteSpace($env:VCPKG_ROOT)) {
        $VcpkgDir = $env:VCPKG_ROOT
    }
    else {
        $VcpkgDir = "Build\Dependencies\vcpkg"
    }
}

$sourceFullPath = Resolve-FullPath $SourceDir
$buildFullPath = Resolve-FullPath $BuildDir
$installFullPath = Resolve-FullPath $InstallDir
$vcpkgFullPath = Resolve-FullPath $VcpkgDir
$vcVars64 = Find-VcVars64

if ($Clean) {
    foreach ($path in @($buildFullPath, $installFullPath)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
}

Sync-MonadoSource -SourceRoot $sourceFullPath -RepositoryUrl $SourceUrl -Revision $Ref
$toolchain = [string](Ensure-Vcpkg -VcpkgRoot $vcpkgFullPath)
if (-not (Test-Path -LiteralPath $toolchain -PathType Leaf)) {
    throw "vcpkg toolchain file was not found after setup: $toolchain"
}

Reset-StaleCMakeBuildCache -BuildRoot $buildFullPath -ExpectedToolchain $toolchain

if (-not $SkipBuild) {
    [System.IO.Directory]::CreateDirectory($buildFullPath) | Out-Null
    [System.IO.Directory]::CreateDirectory($installFullPath) | Out-Null

    # Monado uses __cplusplus to choose <filesystem>; MSVC needs this flag to report the active C++ standard.
    $cmakeArgs = @(
        "-S", $sourceFullPath,
        "-B", $buildFullPath,
        "-G", "Ninja",
        "-DCMAKE_BUILD_TYPE=$Configuration",
        "-DCMAKE_CXX_FLAGS=/DWIN32 /D_WINDOWS /EHsc /Zc:__cplusplus",
        "-DCMAKE_INSTALL_PREFIX=$installFullPath",
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
        "-DCMAKE_MAKE_PROGRAM=$script:NinjaPath",
        "-DVCPKG_TARGET_TRIPLET=x64-windows",
        "-DXRT_FEATURE_SERVICE=ON"
    ) + $ExtraCMakeArgs

    Invoke-VsNative -VcVars64Bat $vcVars64 -Command $script:CMakePath -Arguments $cmakeArgs -WorkingDirectory $repoRoot
    Invoke-VsNative -VcVars64Bat $vcVars64 -Command $script:CMakePath -Arguments @("--build", $buildFullPath, "--config", $Configuration) -WorkingDirectory $repoRoot

    if (-not $SkipInstall) {
        Invoke-VsNative -VcVars64Bat $vcVars64 -Command $script:CMakePath -Arguments @("--install", $buildFullPath, "--config", $Configuration) -WorkingDirectory $repoRoot
    }
}

$runtimeInfo = Find-MonadoManifest -InstallRoot $installFullPath -BuildRoot $buildFullPath
$loaderPath = Ensure-OpenXrLoader -InstallRoot $installFullPath -BuildRoot $buildFullPath -VcpkgRoot $vcpkgFullPath
$stagedRuntimeDlls = Copy-MonadoRuntimeDependencies -InstallRoot $installFullPath -BuildRoot $buildFullPath -VcpkgRoot $vcpkgFullPath -OpenXrLoaderPath $loaderPath
$editorLoaderPath = $null
if (-not $SkipEditorLoaderInstall -and -not [string]::IsNullOrWhiteSpace($loaderPath)) {
    $editorLoaderPath = Install-OpenXrLoaderToEditorOutput -LoaderPath $loaderPath
}

$envScript = Join-Path $installFullPath "monado-env.ps1"
@(
    "`$env:MONADO_INSTALL_DIR = '$($installFullPath.Replace("'", "''"))'",
    "`$env:MONADO_RUNTIME_JSON = '$(([string]$runtimeInfo.RuntimeJson).Replace("'", "''"))'",
    "`$env:XR_RUNTIME_JSON = '$(([string]$runtimeInfo.RuntimeJson).Replace("'", "''"))'"
) | Set-Content -LiteralPath $envScript -Encoding UTF8

if ($SetUserEnvironment) {
    [Environment]::SetEnvironmentVariable("MONADO_INSTALL_DIR", $installFullPath, [EnvironmentVariableTarget]::User)
    [Environment]::SetEnvironmentVariable("MONADO_RUNTIME_JSON", [string]$runtimeInfo.RuntimeJson, [EnvironmentVariableTarget]::User)
}

$commit = & git -C $sourceFullPath rev-parse HEAD
$installInfo = [pscustomobject]@{
    SourceUrl          = $SourceUrl
    Ref                = $Ref
    Commit             = $commit
    SourceDir          = $sourceFullPath
    BuildDir           = $buildFullPath
    InstallDir         = $installFullPath
    VcpkgDir           = $vcpkgFullPath
    RuntimeJson        = $runtimeInfo.RuntimeJson
    RuntimeName        = $runtimeInfo.RuntimeName
    RuntimeVersion     = $runtimeInfo.RuntimeVersion
    RuntimeLibraryPath = $runtimeInfo.LibraryPath
    OpenXrLoaderDll    = $loaderPath
    StagedRuntimeDlls  = $stagedRuntimeDlls
    EditorLoaderDll    = $editorLoaderPath
    EnvScript          = $envScript
}
$installInfoPath = Join-Path $installFullPath "install-info.json"
$installInfo | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $installInfoPath -Encoding UTF8

Write-Host "Monado is ready." -ForegroundColor Green
Write-Host "Runtime manifest: $($runtimeInfo.RuntimeJson)" -ForegroundColor Cyan
if (-not [string]::IsNullOrWhiteSpace($loaderPath)) {
    Write-Host "OpenXR loader: $loaderPath" -ForegroundColor Cyan
}
if (-not [string]::IsNullOrWhiteSpace($editorLoaderPath)) {
    Write-Host "Copied OpenXR loader to editor output: $editorLoaderPath" -ForegroundColor Cyan
}
Write-Host "Environment helper: $envScript" -ForegroundColor Cyan
Write-Host "Install info: $installInfoPath" -ForegroundColor Cyan

$installInfo
