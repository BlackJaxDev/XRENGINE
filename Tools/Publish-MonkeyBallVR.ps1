param(
    [string]$PackagePath = "",
    [switch]$NoClean,
    [switch]$NoSmoke,
    [switch]$NoEditorBuild,
    [switch]$AllowAotWarnings,
    [int]$SmokeTimeoutSeconds = 30
)

function Get-LatestNonReferenceAssembly {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SearchRoot,
        [Parameter(Mandatory = $true)]
        [string]$AssemblyName
    )

    if (-not (Test-Path -LiteralPath $SearchRoot -PathType Container)) {
        throw "Assembly search root was not produced: $SearchRoot"
    }

    $candidates = @(
        Get-ChildItem -LiteralPath $SearchRoot -Recurse -File -Filter $AssemblyName |
            Where-Object { $_.FullName -notmatch '\\ref(?:int)?\\' }
    )
    if ($candidates.Count -eq 0) {
        throw "Assembly '$AssemblyName' was not found under '$SearchRoot'."
    }

    $candidates |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Assert-MonkeyBallRendererInputHashes {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [string]$GameProjectRoot,
        [Parameter(Mandatory = $true)]
        [string]$PublishedRoot
    )

    $launcherBuildRoot = Join-Path $GameProjectRoot "Intermediate\MonkeyBallVR\Launcher\Build\x64\Release"
    $rendererProjects = @(
        "XREngine.Runtime.Rendering",
        "XREngine.Runtime.Rendering.OpenGL",
        "XREngine.Runtime.Rendering.Vulkan"
    )
    $manifest = @()
    foreach ($rendererProject in $rendererProjects) {
        $assemblyName = "$rendererProject.dll"
        $sourceRoot = Join-Path $RepositoryRoot "$rendererProject\bin\x64\Release"
        $sourceAssembly = Get-LatestNonReferenceAssembly `
            -SearchRoot $sourceRoot `
            -AssemblyName $assemblyName
        $launcherAssembly = Get-LatestNonReferenceAssembly `
            -SearchRoot $launcherBuildRoot `
            -AssemblyName $assemblyName
        $sourceHash = (Get-FileHash -LiteralPath $sourceAssembly.FullName -Algorithm SHA256).Hash
        $launcherHash = (Get-FileHash -LiteralPath $launcherAssembly.FullName -Algorithm SHA256).Hash
        if (-not [string]::Equals($sourceHash, $launcherHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Published launcher renderer input '$assemblyName' does not match its just-built source output."
        }

        Write-Host "  Renderer input verified: $assemblyName $sourceHash"
        $manifest += [pscustomobject]@{
            assembly = $assemblyName
            sha256 = $sourceHash
        }
    }

    $reportPath = Join-Path $RepositoryRoot "Build\Reports\aot-final-game-renderer-hashes.json"
    $metadataDirectory = Join-Path $PublishedRoot "Metadata"
    $packagedManifestPath = Join-Path $metadataDirectory "RenderingAssemblyHashes.json"
    New-Item -ItemType Directory -Path $metadataDirectory -Force | Out-Null
    $manifest | ConvertTo-Json | Set-Content -LiteralPath $reportPath -Encoding UTF8
    Copy-Item -LiteralPath $reportPath -Destination $packagedManifestPath -Force
}

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
Assert-MonkeyBallRendererInputHashes `
    -RepositoryRoot $repoRoot `
    -GameProjectRoot $projectRoot `
    -PublishedRoot $publishRoot

if (-not $NoSmoke) {
    $smokeLog = Join-Path $repoRoot "Build\Reports\aot-final-game-smoke.log"
    if (-not (Test-Path -LiteralPath $smokeLog -PathType Leaf)) {
        throw "MonkeyBall VR runtime smoke log was not produced at '$smokeLog'."
    }

    $smokeText = Get-Content -Raw -LiteralPath $smokeLog
    if ($smokeText -notmatch 'MonkeyBall runtime validation event=runtime-validation-passed') {
        throw "MonkeyBall VR smoke did not report successful live runtime validation. See $smokeLog"
    }

    if ($smokeText -notmatch 'AOT runtime smoke passed\.') {
        throw "MonkeyBall VR smoke did not reach the post-engine completion marker. See $smokeLog"
    }

    Write-Host "  Live runtime validation: passed"
}

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
