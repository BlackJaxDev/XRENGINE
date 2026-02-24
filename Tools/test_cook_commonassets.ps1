param(
    [string]$SourceDirectory = "Build/CommonAssets",
    [string]$OutputArchive = "Build/Game/Content/GameContent.pak",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $toolsDir ".."))

if ([System.IO.Path]::IsPathRooted($OutputArchive)) {
    $archivePath = [System.IO.Path]::GetFullPath($OutputArchive)
}
else {
    $archivePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputArchive))
}

if ([System.IO.Path]::IsPathRooted($SourceDirectory)) {
    $sourcePath = [System.IO.Path]::GetFullPath($SourceDirectory)
}
else {
    $sourcePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $SourceDirectory))
}

if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
    throw "Source directory does not exist: $sourcePath"
}

$archiveDir = Split-Path -Parent $archivePath
if (-not [string]::IsNullOrWhiteSpace($archiveDir)) {
    New-Item -ItemType Directory -Path $archiveDir -Force | Out-Null
}

Push-Location $repoRoot
try {
    Write-Host "Packing Build/CommonAssets into archive:" -ForegroundColor Cyan
    Write-Host "  Source: $sourcePath" -ForegroundColor Cyan
    Write-Host "  Output: $archivePath" -ForegroundColor Cyan

    $editorDll = Join-Path $repoRoot "Build/Editor/Debug/AnyCPU/Debug/net10.0-windows7.0/XREngine.Editor.dll"
    $editorCsproj = Join-Path $repoRoot "XREngine.Editor/XREngine.Editor.csproj"

    $editorArgs = @(
        "--cook-common-assets"
        "--cook-source"
        $sourcePath
        "--cook-output"
        $archivePath
    )

    if (-not (Test-Path -LiteralPath $editorDll -PathType Leaf)) {
        if ($NoBuild) {
            throw "Editor DLL not found at '$editorDll'. Re-run without -NoBuild to build it."
        }

        Write-Host "Editor DLL not found; building editor..." -ForegroundColor Yellow
        & dotnet build $editorCsproj -c Debug
        if ($LASTEXITCODE -ne 0) {
            throw "Editor build failed with exit code $LASTEXITCODE."
        }
    }

    & dotnet $editorDll @editorArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Cook command failed with exit code $LASTEXITCODE."
    }

    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw "Cook command completed but archive was not created: $archivePath"
    }

    $archiveFile = Get-Item -LiteralPath $archivePath
    if ($archiveFile.Length -le 0) {
        throw "Cook command completed but archive is empty: $archivePath"
    }

    Write-Host "Cooked archive ready at: $archivePath" -ForegroundColor Green
}
finally {
    Pop-Location
}
