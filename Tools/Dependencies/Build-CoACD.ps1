param(
    [ValidateSet("win-x64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string]$Rid = "win-x64",
    [string]$Ref = "1.0.7",
    [string]$Configuration = "Release",
    [switch]$ForceClone,
    [switch]$ForceBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Ensure-Tool {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required tool '$Name' was not found on PATH. Install it and try again."
    }
}

function Invoke-Step {
    param(
        [string]$Command,
        [string[]]$Arguments,
        [string]$WorkingDirectory = $null
    )

    if ($WorkingDirectory) {
        Push-Location $WorkingDirectory
    }

    try {
        Write-Host ">> $Command $($Arguments -join ' ')" -ForegroundColor Cyan
        & $Command @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command '$Command' failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        if ($WorkingDirectory) {
            Pop-Location
        }
    }
}

function Install-CoACDFromArchive {
    param(
        [string]$Destination,
        [string]$Ref,
        [string]$DownloadsDir
    )

    $archiveName = "CoACD-$Ref.zip"
    $archivePath = Join-Path $DownloadsDir $archiveName
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

    try {
        $scopes = @("tags", "heads")
        $downloaded = $false
        foreach ($scope in $scopes) {
            $url = "https://github.com/SarahWeiii/CoACD/archive/refs/$scope/$Ref.zip"
            try {
                Write-Host "Downloading $url" -ForegroundColor Yellow
                Invoke-WebRequest -Uri $url -OutFile $archivePath -UseBasicParsing
                $downloaded = $true
                break
            }
            catch {
                Remove-Item -Path $archivePath -ErrorAction SilentlyContinue
            }
        }

        if (-not $downloaded) {
            throw "Unable to download source archive for ref $Ref."
        }

        Write-Host "Expanding $archivePath" -ForegroundColor Yellow
        Expand-Archive -Path $archivePath -DestinationPath $tempDir -Force
        $extractedRoot = Join-Path $tempDir "CoACD-$Ref"
        if (-not (Test-Path $extractedRoot)) {
            $extractedRoot = (Get-ChildItem -Directory -Path $tempDir | Select-Object -First 1).FullName
        }

        Move-Item -Path $extractedRoot -Destination $Destination
    }
    finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-CoACDSubmodules {
    param(
        [string]$SourceDir,
        [string]$Ref,
        [string]$DownloadsDir
    )

    $cdtHeader = Join-Path $SourceDir "3rd/cdt/CDT/include/CDT.h"
    if (Test-Path $cdtHeader) {
        return
    }

    $targetDir = Join-Path $SourceDir "3rd/cdt"
    Write-Host "Restoring CDT submodule contents for ref $Ref" -ForegroundColor Yellow
    $apiUrl = "https://api.github.com/repos/SarahWeiii/CoACD/contents/3rd/cdt?ref=$Ref"
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $archivePath = Join-Path $DownloadsDir "cdt-$Ref.zip"

    try {
        $response = Invoke-RestMethod -Uri $apiUrl -Headers @{ "User-Agent" = "XREngine-CoACD-Build" }
        if (-not $response.sha) {
            throw "GitHub API response did not include a SHA for submodule path."
        }
        $sha = $response.sha
        $shortSha = $sha.Substring(0, 12)
        $archiveUrl = "https://github.com/artem-ogre/CDT/archive/$sha.zip"
        Write-Host "Downloading CDT submodule ($shortSha) from $archiveUrl" -ForegroundColor Yellow
        Invoke-WebRequest -Uri $archiveUrl -OutFile $archivePath -UseBasicParsing
        Expand-Archive -Path $archivePath -DestinationPath $tempDir -Force
        $extractedRoot = Get-ChildItem -Directory -Path $tempDir | Select-Object -First 1
        if (-not $extractedRoot) {
            throw "Failed to extract CDT archive."
        }
        if (Test-Path $targetDir) {
            Remove-Item -Path $targetDir -Recurse -Force
        }
        Move-Item -Path $extractedRoot.FullName -Destination $targetDir
    }
    finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Ensure-FetchContentSource {
    param(
        [string]$Name,
        [string]$Ref,
        [string[]]$UrlTemplates,
        [string]$CacheRoot
    )

    $targetDir = Join-Path $CacheRoot $Name
    if (Test-Path $targetDir) {
        return $targetDir
    }

    New-Item -ItemType Directory -Path $CacheRoot -Force | Out-Null
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString())
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    $archivePath = Join-Path $CacheRoot "$Name-$Ref.zip"
    $downloaded = $false

    try {
        foreach ($template in $UrlTemplates) {
            $url = $template.Replace("{ref}", $Ref)
            try {
                Write-Host "Downloading dependency $Name ($Ref) from $url" -ForegroundColor Yellow
                Invoke-WebRequest -Uri $url -OutFile $archivePath -UseBasicParsing
                $downloaded = $true
                break
            }
            catch {
                Remove-Item -Path $archivePath -ErrorAction SilentlyContinue
            }
        }

        if (-not $downloaded) {
            throw "Unable to download archive for dependency $Name ($Ref)."
        }

        Expand-Archive -Path $archivePath -DestinationPath $tempDir -Force
        $extractedRoot = (Get-ChildItem -Directory -Path $tempDir | Select-Object -First 1)
        if (-not $extractedRoot) {
            throw "Failed to expand archive for dependency $Name ($Ref)."
        }

        Move-Item -Path $extractedRoot.FullName -Destination $targetDir
        return $targetDir
    }
    finally {
        Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Ensure-Tool -Name "git"
Ensure-Tool -Name "cmake"

$ridMap = @{
    "win-x64" = @{ Binary = "lib_coacd.dll"; ConfigureArgs = @("-G", "Visual Studio 17 2022", "-A", "x64", "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded", "-DOPENVDB_CORE_SHARED=OFF", "-DTBB_TEST=OFF", "-DCMAKE_CXX_FLAGS=/MT /EHsc") }
    "linux-x64" = @{ Binary = "lib_coacd.so"; ConfigureArgs = @("-DCMAKE_CXX_FLAGS=-fPIC", "-DOPENVDB_CORE_SHARED=OFF", "-DTBB_TEST=OFF") }
    "linux-arm64" = @{ Binary = "lib_coacd.so"; ConfigureArgs = @("-DCMAKE_CXX_FLAGS=-fPIC", "-DOPENVDB_CORE_SHARED=OFF", "-DTBB_TEST=OFF", "-DCMAKE_SYSTEM_PROCESSOR=arm64") }
    "osx-x64" = @{ Binary = "lib_coacd.dylib"; ConfigureArgs = @("-DCMAKE_OSX_ARCHITECTURES=x86_64", "-DOPENVDB_CORE_SHARED=OFF", "-DTBB_TEST=OFF") }
    "osx-arm64" = @{ Binary = "lib_coacd.dylib"; ConfigureArgs = @("-DCMAKE_OSX_ARCHITECTURES=arm64", "-DOPENVDB_CORE_SHARED=OFF", "-DTBB_TEST=OFF") }
}

if (-not $ridMap.ContainsKey($Rid)) {
    throw "Unsupported RID: $Rid"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$sourceDir = Join-Path $repoRoot "Build\Submodules\CoACD"
$buildDir = Join-Path $sourceDir "build\$Rid"
$downloadsDir = Join-Path $repoRoot "Build\Dependencies\CoACD"
$targetRuntimeDir = Join-Path $repoRoot "XRENGINE\runtimes\$Rid\native"
$metadataPath = Join-Path $downloadsDir "build-info-$Rid.json"
$dependencyCache = Join-Path $downloadsDir "sources"
$repoUrl = "https://github.com/SarahWeiii/CoACD.git"
$binaryName = $ridMap[$Rid].Binary
$targetBinary = Join-Path $targetRuntimeDir $binaryName

New-Item -ItemType Directory -Path $downloadsDir -Force | Out-Null
New-Item -ItemType Directory -Path $targetRuntimeDir -Force | Out-Null

if ($ForceClone -and (Test-Path $sourceDir)) {
    Write-Host "Removing existing source tree because -ForceClone was supplied."
    Remove-Item $sourceDir -Recurse -Force
}

if (-not (Test-Path $sourceDir)) {
    Write-Host "Cloning CoACD ($Ref) into $sourceDir"
    try {
        Invoke-Step -Command "git" -Arguments @("clone", "--branch", $Ref, "--depth", "1", "--recurse-submodules", $repoUrl, $sourceDir)
    }
    catch {
        Write-Warning "git clone failed ($($_.Exception.Message)). Falling back to source archive."
        Remove-Item -Path $sourceDir -Recurse -Force -ErrorAction SilentlyContinue
        Install-CoACDFromArchive -Destination $sourceDir -Ref $Ref -DownloadsDir $downloadsDir
    }
}
else {
    $gitDir = Join-Path $sourceDir ".git"
    if (Test-Path $gitDir) {
        Write-Host "Updating existing CoACD clone in $sourceDir"
        Invoke-Step -Command "git" -Arguments @("-C", $sourceDir, "fetch", "--tags", "origin")
        Invoke-Step -Command "git" -Arguments @("-C", $sourceDir, "checkout", $Ref)
        Invoke-Step -Command "git" -Arguments @("-C", $sourceDir, "submodule", "update", "--init", "--recursive")
    }
    else {
        Write-Host "Using previously extracted CoACD source at $sourceDir"
    }
}

Ensure-CoACDSubmodules -SourceDir $sourceDir -Ref $Ref -DownloadsDir $downloadsDir

$gitFolder = Join-Path $sourceDir ".git"
if (Test-Path $gitFolder) {
    $commitHash = (& git -C $sourceDir rev-parse HEAD).Trim()
}
else {
    $commitHash = $Ref
}

if (-not $ForceBuild -and (Test-Path $targetBinary) -and (Test-Path $metadataPath)) {
    $metadata = Get-Content $metadataPath -Raw | ConvertFrom-Json
    if ($metadata.Ref -eq $Ref -and $metadata.Commit -eq $commitHash) {
        Write-Host "CoACD $Ref (commit $commitHash) already built for $Rid. Skipping." -ForegroundColor Green
        return
    }
}

$fetchDependencies = @(
    @{ Name = "zlib"; Ref = "v1.2.11"; Urls = @("https://github.com/madler/zlib/archive/refs/tags/{ref}.zip"); Var = "FETCHCONTENT_SOURCE_DIR_ZLIB" },
    @{ Name = "spdlog"; Ref = "v1.8.2"; Urls = @("https://github.com/gabime/spdlog/archive/refs/tags/{ref}.zip"); Var = "FETCHCONTENT_SOURCE_DIR_SPDLOG" },
    @{ Name = "openvdb"; Ref = "v8.2.0"; Urls = @("https://github.com/AcademySoftwareFoundation/openvdb/archive/refs/tags/{ref}.zip"); Var = "FETCHCONTENT_SOURCE_DIR_OPENVDB" },
    @{ Name = "tbb"; Ref = "v2022.0.0"; Urls = @("https://github.com/oneapi-src/oneTBB/archive/refs/tags/{ref}.zip"); Var = "FETCHCONTENT_SOURCE_DIR_TBB" },
    @{ Name = "eigen"; Ref = "3.4.0"; Urls = @("https://gitlab.com/libeigen/eigen/-/archive/{ref}/eigen-{ref}.zip"); Var = "FETCHCONTENT_SOURCE_DIR_EIGEN" }
)

$fetchOverrides = @{}
foreach ($dep in $fetchDependencies) {
    $path = Ensure-FetchContentSource -Name $dep.Name -Ref $dep.Ref -UrlTemplates $dep.Urls -CacheRoot $dependencyCache
    if ($dep.Name -eq "openvdb") {
        $patchSourceDir = Join-Path $sourceDir "cmake"
        $corePatch = Join-Path $patchSourceDir "openvdb_CMakeLists.txt"
        $cmdPatch = Join-Path $patchSourceDir "openvdb_cmd_CMakeLists.txt"
        $coreTarget = Join-Path $path "openvdb/openvdb/CMakeLists.txt"
        $cmdTarget = Join-Path $path "openvdb/openvdb/cmd/CMakeLists.txt"
        if ((Test-Path $corePatch) -and (Test-Path $coreTarget)) {
            Copy-Item -Path $corePatch -Destination $coreTarget -Force
        }
        if ((Test-Path $cmdPatch) -and (Test-Path $cmdTarget)) {
            Copy-Item -Path $cmdPatch -Destination $cmdTarget -Force
        }
    }
    $fetchOverrides[$dep.Var] = $path
}

$configureArgs = @(
    "-S", $sourceDir,
    "-B", $buildDir,
    "-DCMAKE_BUILD_TYPE=$Configuration",
    "-DWITH_3RD_PARTY_LIBS=ON",
    "-DCMAKE_POLICY_VERSION_MINIMUM=3.5"
) + $ridMap[$Rid].ConfigureArgs
foreach ($entry in $fetchOverrides.GetEnumerator()) {
    $fullPath = [System.IO.Path]::GetFullPath($entry.Value)
    $configureArgs += "-D$($entry.Key)=$fullPath"
}
Invoke-Step -Command "cmake" -Arguments $configureArgs

$buildArgs = @("--build", $buildDir, "--target", "_coacd", "--config", $Configuration, "--parallel")
Invoke-Step -Command "cmake" -Arguments $buildArgs

$candidatePaths = @(
    [System.IO.Path]::Combine($buildDir, $Configuration, $binaryName),
    [System.IO.Path]::Combine($buildDir, $binaryName),
    [System.IO.Path]::Combine($buildDir, "bin", $Configuration, $binaryName),
    [System.IO.Path]::Combine($buildDir, "bin", $binaryName)
)

$artifactPath = $null
foreach ($candidate in $candidatePaths) {
    if (Test-Path $candidate) {
        $artifactPath = $candidate
        break
    }
}

if (-not $artifactPath) {
    throw "Unable to locate built CoACD binary for $Rid in $buildDir."
}

Copy-Item -Path $artifactPath -Destination $targetBinary -Force
Write-Host "Copied $binaryName to $targetRuntimeDir" -ForegroundColor Green

$metadata = [ordered]@{
    Ref = $Ref
    Commit = $commitHash
    BuiltAtUtc = (Get-Date).ToUniversalTime().ToString("o")
    Rid = $Rid
    Configuration = $Configuration
}
$metadata | ConvertTo-Json | Set-Content -Path $metadataPath
Write-Host "Recorded build metadata at $metadataPath"
