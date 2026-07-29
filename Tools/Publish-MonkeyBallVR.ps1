param(
    [string]$PackagePath = "",
    [switch]$NoClean,
    [switch]$NoSmoke,
    [switch]$NoEditorBuild,
    [switch]$AllowAotWarnings,
    [int]$SmokeTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectRoot = Join-Path $repoRoot "Samples\MonkeyBallVR"
$projectPath = Join-Path $projectRoot "MonkeyBallVR.xrproj"
$publishRoot = Join-Path $projectRoot "Build\Publish"
$publishScript = Join-Path $PSScriptRoot "Publish-AotFinalGame.ps1"

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $projectRoot "Build\Packages\MonkeyBallVR-win-x64.zip"
} elseif (-not [System.IO.Path]::IsPathRooted($PackagePath)) {
    $PackagePath = Join-Path $repoRoot $PackagePath
}

$publishArguments = @{
    ProjectPath = $projectPath
    BuildConfiguration = "Release"
    BuildPlatform = "Windows64"
    OutputSubfolder = "Publish"
    LauncherName = "MonkeyBallVR.exe"
    SmokeTimeoutSeconds = $SmokeTimeoutSeconds
}
if ($NoClean) {
    $publishArguments.NoClean = $true
}
if ($NoEditorBuild) {
    $publishArguments.NoEditorBuild = $true
}
if ($NoSmoke) {
    $publishArguments.NoSmoke = $true
}
if ($AllowAotWarnings) {
    $publishArguments.AllowAotWarnings = $true
}

& $publishScript @publishArguments

if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
    throw "MonkeyBall VR publish directory was not produced at '$publishRoot'."
}

$resolvedProjectRoot = [System.IO.Path]::GetFullPath($projectRoot).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$resolvedPackagePath = [System.IO.Path]::GetFullPath($PackagePath)
$packageDirectory = Split-Path -Parent $resolvedPackagePath

if ($resolvedPackagePath.StartsWith(
        $resolvedProjectRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase) -and
    (Test-Path -LiteralPath $resolvedPackagePath)) {
    Remove-Item -LiteralPath $resolvedPackagePath -Force
}
elseif (Test-Path -LiteralPath $resolvedPackagePath) {
    throw "Refusing to overwrite package outside the MonkeyBall VR project: $resolvedPackagePath"
}

New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
Compress-Archive `
    -Path (Join-Path $publishRoot "*") `
    -DestinationPath $resolvedPackagePath `
    -CompressionLevel Optimal

Write-Host "MonkeyBall VR release package created."
Write-Host "  Package: $resolvedPackagePath"
