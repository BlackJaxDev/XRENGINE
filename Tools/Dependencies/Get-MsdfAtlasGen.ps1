[CmdletBinding()]
param(
    [string]$Version = "latest",
    [string]$OutputDir,
    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "Build\Dependencies\MsdfAtlasGen"
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$downloadDir = Join-Path $OutputDir "downloads"
$tmpDir = Join-Path $OutputDir "_tmp_extract"
$exePath = Join-Path $OutputDir "msdf-atlas-gen.exe"

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null

if ((Test-Path -LiteralPath $exePath) -and -not $Force) {
    Write-Host "msdf-atlas-gen already exists at $exePath (use -Force to re-download)." -ForegroundColor Green
}
else {
    $apiUrl = if ($Version -eq "latest") {
        "https://api.github.com/repos/Chlumsky/msdf-atlas-gen/releases/latest"
    }
    else {
        "https://api.github.com/repos/Chlumsky/msdf-atlas-gen/releases/tags/$Version"
    }

    Write-Host "Querying release metadata from $apiUrl" -ForegroundColor Yellow
    $release = Invoke-RestMethod -Uri $apiUrl -UseBasicParsing
    if ($null -eq $release) {
        throw "Failed to load msdf-atlas-gen release metadata from GitHub."
    }

    $asset = $release.assets |
        Where-Object {
            $_.name -match 'win' -and $_.name -match '\.zip$'
        } |
        Sort-Object @{ Expression = { if ($_.name -match 'win64|x64') { 0 } else { 1 } } }, name |
        Select-Object -First 1

    if ($null -eq $asset) {
        throw "Could not find a Windows ZIP asset in release '$($release.tag_name)'."
    }

    $archivePath = Join-Path $downloadDir $asset.name
    if ((-not (Test-Path -LiteralPath $archivePath)) -or $Force) {
        Write-Host "Downloading $($asset.name)" -ForegroundColor Yellow
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $archivePath -UseBasicParsing
    }
    else {
        Write-Host "Reusing existing archive at $archivePath" -ForegroundColor Green
    }

    if (Test-Path -LiteralPath $tmpDir) {
        Remove-Item -LiteralPath $tmpDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $tmpDir)

        $downloadedExe = Get-ChildItem -Path $tmpDir -Recurse -File -Filter "msdf-atlas-gen.exe" |
            Sort-Object FullName |
            Select-Object -First 1

        if ($null -eq $downloadedExe) {
            throw "Could not locate msdf-atlas-gen.exe inside the downloaded archive."
        }

        Copy-Item -LiteralPath $downloadedExe.FullName -Destination $exePath -Force
        Write-Host "Installed msdf-atlas-gen to $exePath" -ForegroundColor Green
    }
    finally {
        if (Test-Path -LiteralPath $tmpDir) {
            Remove-Item -LiteralPath $tmpDir -Recurse -Force
        }
    }
}

$versionOutput = & $exePath -version 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "msdf-atlas-gen failed validation with exit code $LASTEXITCODE."
}

Write-Host "msdf-atlas-gen version: $versionOutput" -ForegroundColor Cyan
