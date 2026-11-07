<#
Builds the native block-compression shim (xre_bcn) from the pinned bc7enc_rdo source.
The repository and commit come from Bc7Enc.lock.json. The shim source and its CMake
project are in Tools/Dependencies/Bc7Enc/. Only the MIT/Unlicense encoders (bc7enc.cpp
and rgbcx.cpp) are compiled.
Output: Build/Dependencies/Bc7Enc/<rid>/ (library and xre_bcn.version).
#>
param(
    [ValidateSet("win-x64", "linux-x64")]
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
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
        [string[]]$Arguments
    )

    Write-Host ">> $Command $($Arguments -join ' ')" -ForegroundColor Cyan
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $Command @Arguments 2>&1 | ForEach-Object { Write-Host $_ } }
    finally { $ErrorActionPreference = $previousPreference }
    if ($LASTEXITCODE -ne 0) {
        throw "Command '$Command' failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$lock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "Bc7Enc.lock.json") | ConvertFrom-Json
$commit = ([string]$lock.commit).ToLowerInvariant()
if ($commit -notmatch '^[0-9a-f]{40}$') {
    throw "Bc7Enc.lock.json must contain a full 40-character commit."
}

$shimDir = Join-Path $PSScriptRoot "Bc7Enc"
$shimHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $shimDir "xre_bcn.cpp")).Hash.Substring(0, 16).ToLowerInvariant()
$binaryName = if ($Rid -eq "win-x64") { "xre_bcn.dll" } else { "libxre_bcn.so" }
$workRoot = Join-Path $repoRoot "Build\Dependencies\Bc7Enc"
$outputDir = Join-Path $workRoot $Rid
$targetBinary = Join-Path $outputDir $binaryName
$versionPath = Join-Path $outputDir "xre_bcn.version"
# The version records the upstream commit and the shim source, so a shim change rebuilds.
$versionText = "bc7enc_rdo $commit shim $shimHash"

if (-not $ForceBuild -and (Test-Path -LiteralPath $targetBinary) -and (Test-Path -LiteralPath $versionPath)) {
    if ((Get-Content -Raw -LiteralPath $versionPath).Trim() -eq $versionText) {
        Write-Host "xre_bcn $versionText already built for $Rid. Skipping." -ForegroundColor Green
        return
    }
}

Ensure-Tool -Name "git"
Ensure-Tool -Name "cmake"

# The upstream source lives in the ignored Build\Dependencies\Bc7Enc folder.
$sourceDir = Join-Path $workRoot "source"
$buildDir = Join-Path $workRoot "cmake-$Rid"
New-Item -ItemType Directory -Path $workRoot, $outputDir -Force | Out-Null

if (-not (Test-Path -LiteralPath (Join-Path $sourceDir ".git"))) {
    if (Test-Path -LiteralPath $sourceDir) {
        Remove-Item -LiteralPath $sourceDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $sourceDir -Force | Out-Null
    Invoke-Step -Command "git" -Arguments @("-C", $sourceDir, "init", "--quiet")
    Invoke-Step -Command "git" -Arguments @("-C", $sourceDir, "remote", "add", "origin", [string]$lock.repository)
}

$head = & git -C $sourceDir rev-parse --verify --quiet "HEAD^{commit}" 2>$null
if ($LASTEXITCODE -ne 0 -or ([string]$head).Trim() -ne $commit) {
    Invoke-Step -Command "git" -Arguments @("-C", $sourceDir, "fetch", "--depth", "1", "origin", $commit)
    Invoke-Step -Command "git" -Arguments @("-C", $sourceDir, "checkout", "--quiet", "--force", $commit)
}

$actualCommit = ([string](& git -C $sourceDir rev-parse HEAD)).Trim()
if ($actualCommit -ne $commit) {
    throw "Checked-out bc7enc_rdo commit $actualCommit does not match the locked commit $commit."
}

$configureArgs = @(
    "-S", $shimDir,
    "-B", $buildDir,
    "-DBC7ENC_SOURCE_DIR=$sourceDir",
    "-DCMAKE_BUILD_TYPE=$Configuration"
)
if ($Rid -eq "win-x64") {
    $configureArgs += @("-A", "x64")
}
Invoke-Step -Command "cmake" -Arguments $configureArgs
Invoke-Step -Command "cmake" -Arguments @("--build", $buildDir, "--target", "xre_bcn", "--config", $Configuration, "--parallel")

$artifact = Get-ChildItem -LiteralPath $buildDir -Recurse -File |
    Where-Object { $_.Name -eq $binaryName } |
    Select-Object -First 1
if (-not $artifact) {
    throw "Unable to locate the built xre_bcn library for $Rid in $buildDir."
}

Copy-Item -LiteralPath $artifact.FullName -Destination $targetBinary -Force
Copy-Item -LiteralPath (Join-Path $sourceDir "LICENSE") -Destination (Join-Path $outputDir "bc7enc_rdo.LICENSE") -Force
Set-Content -LiteralPath $versionPath -Value $versionText -Encoding ascii -NoNewline
Write-Host "Built $targetBinary ($versionText)" -ForegroundColor Green
