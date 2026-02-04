param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [switch]$ForceBuild
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$sourceDir = Join-Path $root "Build\ThirdParty\fastgltf"

if (-not (Test-Path $sourceDir)) {
    Write-Host "fastgltf source not found; cloning into '$sourceDir'." -ForegroundColor Yellow
    $sourceRoot = Split-Path -Parent $sourceDir
    if (-not (Test-Path $sourceRoot)) {
        New-Item -ItemType Directory -Path $sourceRoot | Out-Null
    }
    git clone --depth 1 https://github.com/spnda/fastgltf.git $sourceDir
    if ($LASTEXITCODE -ne 0) { throw "fastgltf clone failed." }
}

$buildDir = Join-Path $sourceDir "build-$Rid-$Configuration"
$runtimeDir = Join-Path $root "XRENGINE\runtimes\$Rid\native"

if (-not (Test-Path $runtimeDir)) {
    New-Item -ItemType Directory -Path $runtimeDir | Out-Null
}

$cmakeArgs = @(
    "-S", $sourceDir,
    "-B", $buildDir,
    "-DFASTGLTF_ENABLE_TESTS=OFF",
    "-DFASTGLTF_ENABLE_EXAMPLES=OFF",
    "-DFASTGLTF_ENABLE_DOCS=OFF",
    "-DBUILD_SHARED_LIBS=ON"
)

if ($ForceBuild -or -not (Test-Path $buildDir)) {
    & cmake @cmakeArgs
    if ($LASTEXITCODE -ne 0) { throw "CMake configure failed." }
}

& cmake --build $buildDir --config $Configuration
if ($LASTEXITCODE -ne 0) { throw "CMake build failed." }

$dllPath = Join-Path $buildDir "$Configuration\fastgltf.dll"
if (-not (Test-Path $dllPath)) {
    $dllPath = Join-Path $buildDir "fastgltf.dll"
}

if (-not (Test-Path $dllPath)) {
    throw "fastgltf.dll not found in build output at '$buildDir'."
}

Copy-Item -Path $dllPath -Destination (Join-Path $runtimeDir "fastgltf.dll") -Force
Write-Host "Copied fastgltf.dll to $runtimeDir" -ForegroundColor Green
