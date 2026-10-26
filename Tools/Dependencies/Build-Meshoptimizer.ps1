<#
Builds the pinned native meshoptimizer library from source.
The release tag and commit come from Meshoptimizer.lock.json.
Output: Build/Dependencies/Meshoptimizer/<rid>/ (library and meshoptimizer.version).
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
$lock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot "Meshoptimizer.lock.json") | ConvertFrom-Json
$tag = [string]$lock.tag
$commit = ([string]$lock.commit).ToLowerInvariant()
if ($commit -notmatch '^[0-9a-f]{40}$') {
    throw "Meshoptimizer.lock.json must contain a full 40-character commit."
}

$binaryName = if ($Rid -eq "win-x64") { "meshoptimizer.dll" } else { "libmeshoptimizer.so" }
$workRoot = Join-Path $repoRoot "Build\Dependencies\Meshoptimizer"
$outputDir = Join-Path $workRoot $Rid
$targetBinary = Join-Path $outputDir $binaryName
$versionPath = Join-Path $outputDir "meshoptimizer.version"
$versionText = "$tag $commit"

if (-not $ForceBuild -and (Test-Path -LiteralPath $targetBinary) -and (Test-Path -LiteralPath $versionPath)) {
    if ((Get-Content -Raw -LiteralPath $versionPath).Trim() -eq $versionText) {
        Write-Host "meshoptimizer $versionText already built for $Rid. Skipping." -ForegroundColor Green
        return
    }
}

Ensure-Tool -Name "git"
Ensure-Tool -Name "cmake"

# The source tree lives in the ignored Build\Dependencies\Meshoptimizer folder.
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
    throw "Checked-out meshoptimizer commit $actualCommit does not match the locked commit $commit."
}

$configureArgs = @(
    "-S", $sourceDir,
    "-B", $buildDir,
    "-DMESHOPT_BUILD_SHARED_LIBS=ON",
    "-DMESHOPT_BUILD_DEMO=OFF",
    "-DMESHOPT_BUILD_GLTFPACK=OFF",
    "-DCMAKE_BUILD_TYPE=$Configuration"
)
if ($Rid -eq "win-x64") {
    $configureArgs += @("-A", "x64")
}
else {
    $configureArgs += "-DCMAKE_POSITION_INDEPENDENT_CODE=ON"
}
Invoke-Step -Command "cmake" -Arguments $configureArgs
Invoke-Step -Command "cmake" -Arguments @("--build", $buildDir, "--target", "meshoptimizer", "--config", $Configuration, "--parallel")

$artifact = Get-ChildItem -LiteralPath $buildDir -Recurse -File |
    Where-Object { $_.Name -eq $binaryName -or ($Rid -eq "linux-x64" -and $_.Name -like "libmeshoptimizer.so*") } |
    Sort-Object { $_.Name -eq $binaryName } -Descending |
    Select-Object -First 1
if (-not $artifact) {
    throw "Unable to locate the built meshoptimizer library for $Rid in $buildDir."
}

Copy-Item -LiteralPath $artifact.FullName -Destination $targetBinary -Force
Set-Content -LiteralPath $versionPath -Value $versionText -Encoding ascii -NoNewline
Write-Host "Built $targetBinary ($versionText)" -ForegroundColor Green
