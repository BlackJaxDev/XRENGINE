<#
Build the pinned GLFW 3.4 Win32 library with the active-window fix.
The script writes only to the ignored Build/Dependencies/Glfw directory.
#>
param(
    [ValidateSet("win-x64")]
    [string]$Rid = "win-x64",
    [switch]$ForceBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-RequiredTool {
    param(
        [string]$Name,
        [string[]]$Arguments
    )

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required tool '$Name' was not found on PATH. Install it and try again."
    }

    & $Name @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$Name' failed with exit code $LASTEXITCODE."
    }
}

if (-not $IsWindows) {
    throw "The patched GLFW build supports Windows x64 only."
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$lock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "Glfw.lock.json") | ConvertFrom-Json
$archiveHash = ([string]$lock.archiveSha256).ToUpperInvariant()
if ($archiveHash -notmatch '^[0-9A-F]{64}$') {
    throw "Glfw.lock.json must contain a SHA-256 source archive hash."
}

$patchPath = Join-Path $PSScriptRoot ([string]$lock.patch)
$patchHash = (Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash
$workRoot = Join-Path $repoRoot "Build\Dependencies\Glfw"
$outputDir = Join-Path $workRoot $Rid
$targetBinary = Join-Path $outputDir "glfw3.dll"
$versionPath = Join-Path $outputDir "glfw.version.json"
$archivePath = Join-Path $workRoot "glfw-$($lock.version).zip"
New-Item -ItemType Directory -Path $workRoot, $outputDir -Force | Out-Null

if (-not $ForceBuild -and (Test-Path -LiteralPath $targetBinary) -and (Test-Path -LiteralPath $versionPath)) {
    $built = Get-Content -Raw -LiteralPath $versionPath | ConvertFrom-Json
    $binaryHash = (Get-FileHash -LiteralPath $targetBinary -Algorithm SHA256).Hash
    if ($built.version -eq $lock.version -and
        $built.archiveSha256 -eq $archiveHash -and
        $built.patchSha256 -eq $patchHash -and
        $built.binarySha256 -eq $binaryHash) {
        Write-Host "Patched GLFW $($lock.version) is ready for $Rid."
        return
    }
}

if ((-not (Test-Path -LiteralPath $archivePath)) -or
    ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $archiveHash)) {
    $downloadPath = Join-Path $workRoot "glfw-$($lock.version).download"
    Invoke-WebRequest -Uri ([string]$lock.sourceUrl) -OutFile $downloadPath
    if ((Get-FileHash -LiteralPath $downloadPath -Algorithm SHA256).Hash -ne $archiveHash) {
        Remove-Item -LiteralPath $downloadPath -Force
        throw "The GLFW source archive hash does not match Glfw.lock.json."
    }
    Move-Item -LiteralPath $downloadPath -Destination $archivePath -Force
}

$buildRoot = Join-Path $workRoot ([guid]::NewGuid().ToString("N"))
Expand-Archive -LiteralPath $archivePath -DestinationPath $buildRoot
$sourceDir = Join-Path $buildRoot "glfw-$($lock.version)"
$cmakeDir = Join-Path $buildRoot "cmake-win-x64"
if (-not (Test-Path -LiteralPath (Join-Path $sourceDir "src\win32_window.c"))) {
    throw "The pinned GLFW archive does not contain src/win32_window.c."
}

Invoke-RequiredTool -Name "git" -Arguments @("-C", $sourceDir, "apply", "--check", $patchPath)
Invoke-RequiredTool -Name "git" -Arguments @("-C", $sourceDir, "apply", $patchPath)
# Do not pin a generator. CMake selects the newest installed Visual Studio, so the
# script works with Visual Studio 2022 and 2026. The output stays at src/Release.
Invoke-RequiredTool -Name "cmake" -Arguments @(
    "-S", $sourceDir, "-B", $cmakeDir,
    "-A", "x64",
    "-DBUILD_SHARED_LIBS=ON",
    "-DGLFW_BUILD_EXAMPLES=OFF",
    "-DGLFW_BUILD_TESTS=OFF",
    "-DGLFW_BUILD_DOCS=OFF",
    "-DGLFW_INSTALL=OFF"
)
Invoke-RequiredTool -Name "cmake" -Arguments @(
    "--build", $cmakeDir, "--config", "Release", "--target", "glfw", "--parallel", "2"
)

$builtBinary = Join-Path $cmakeDir "src\Release\glfw3.dll"
if (-not (Test-Path -LiteralPath $builtBinary)) {
    throw "The GLFW build did not produce src/Release/glfw3.dll."
}

# Remove a prior output before copying. This also breaks a hardlink to another file.
if (Test-Path -LiteralPath $targetBinary) {
    Remove-Item -LiteralPath $targetBinary -Force
}
Copy-Item -LiteralPath $builtBinary -Destination $targetBinary
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Glfw\LICENSE.md") -Destination (Join-Path $outputDir "LICENSE.md") -Force
$binaryHash = (Get-FileHash -LiteralPath $targetBinary -Algorithm SHA256).Hash
$version = [ordered]@{
    version = [string]$lock.version
    archiveSha256 = $archiveHash
    patchSha256 = $patchHash
    binarySha256 = $binaryHash
}
$version | ConvertTo-Json | Set-Content -LiteralPath $versionPath -Encoding ascii
Write-Host "Built patched GLFW $($lock.version) for $Rid at $targetBinary."
