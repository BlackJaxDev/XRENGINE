param(
    [string]$Version = "latest",
    [string]$OutputDir,
    [switch]$Force,
    [switch]$NoCopyToRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "Build\Dependencies\YoutubeDL"
}

$exePath = Join-Path $OutputDir "yt-dlp.exe"
$tmpPath = "$exePath.download"

if ((Test-Path $exePath) -and -not $Force) {
    Write-Host "yt-dlp already exists at $exePath (use -Force to re-download)." -ForegroundColor Green
}
else {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

    $downloadUrl = if ($Version -eq "latest") {
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"
    }
    else {
        "https://github.com/yt-dlp/yt-dlp/releases/download/$Version/yt-dlp.exe"
    }

    Write-Host "Downloading yt-dlp from $downloadUrl" -ForegroundColor Yellow
    Invoke-WebRequest -Uri $downloadUrl -OutFile $tmpPath -UseBasicParsing

    if (-not (Test-Path $tmpPath)) {
        throw "Download failed: temporary file was not created."
    }

    Move-Item -Path $tmpPath -Destination $exePath -Force
    Write-Host "Downloaded yt-dlp to $exePath" -ForegroundColor Green
}

if (-not $NoCopyToRoot) {
    $rootCopyPath = Join-Path $repoRoot "yt-dlp.exe"
    Copy-Item -Path $exePath -Destination $rootCopyPath -Force
    Write-Host "Copied yt-dlp to $rootCopyPath" -ForegroundColor Green
}

$versionOutput = & $exePath --version
if ($LASTEXITCODE -ne 0) {
    throw "yt-dlp failed validation with exit code $LASTEXITCODE."
}

Write-Host "yt-dlp version: $versionOutput" -ForegroundColor Cyan
