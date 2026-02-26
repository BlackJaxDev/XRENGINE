[CmdletBinding()]
param(
    [string]$ArchivePath,
    [string]$DownloadUrl,
    [string]$Version = "latest",
    [string]$OutputDir,
    [switch]$Force,
    [switch]$NoCopyToRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "Build\Dependencies\NvComp"
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$tmpDir = Join-Path $OutputDir "_tmp_extract"
$downloadDir = Join-Path $OutputDir "downloads"
$canonicalDownloadDir = Join-Path $repoRoot "Build\Dependencies\NvComp\downloads"

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
New-Item -ItemType Directory -Path $downloadDir -Force | Out-Null
New-Item -ItemType Directory -Path $canonicalDownloadDir -Force | Out-Null

if (-not [string]::IsNullOrWhiteSpace($DownloadUrl) -and [string]::IsNullOrWhiteSpace($ArchivePath)) {
    $targetName = if ($Version -eq "latest") { "nvcomp-latest.zip" } else { "nvcomp-$Version.zip" }
    $ArchivePath = Join-Path $downloadDir $targetName

    if ((-not (Test-Path -LiteralPath $ArchivePath)) -or $Force) {
        Write-Host "Downloading nvCOMP from $DownloadUrl" -ForegroundColor Yellow
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $ArchivePath -UseBasicParsing
    }
    else {
        Write-Host "Reusing existing archive at $ArchivePath (use -Force to re-download)." -ForegroundColor Green
    }
}

if ([string]::IsNullOrWhiteSpace($ArchivePath) -and [string]::IsNullOrWhiteSpace($DownloadUrl)) {
    $searchRoots = @($downloadDir)
    if (-not [string]::Equals($canonicalDownloadDir, $downloadDir, [System.StringComparison]::OrdinalIgnoreCase)) {
        $searchRoots += $canonicalDownloadDir
    }

    $localArchive = Get-ChildItem -Path $searchRoots -File |
        Where-Object {
            $_.Name -match '^nvcomp-.*\.(zip|tgz)$' -or
            $_.Name -match '^nvcomp-.*\.tar\.gz$'
        } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -ne $localArchive) {
        $ArchivePath = $localArchive.FullName
        Write-Host "Using local nvCOMP archive: $ArchivePath" -ForegroundColor Yellow
    }
}

if ([string]::IsNullOrWhiteSpace($ArchivePath)) {
    throw "No nvCOMP archive specified. Provide -ArchivePath <zip/tar.gz> or -DownloadUrl <url>, or place nvcomp-*.zip/tar.gz in $downloadDir."
}

$ArchivePath = [System.IO.Path]::GetFullPath($ArchivePath)
if (-not (Test-Path -LiteralPath $ArchivePath)) {
    throw "Archive not found: $ArchivePath"
}

$isWindows = $env:OS -eq "Windows_NT"
if (-not $isWindows) {
    throw "Get-NvComp.ps1 currently supports Windows DLL installs only (nvcomp*.dll / cudart64_*.dll)."
}

if (Test-Path -LiteralPath $tmpDir) {
    Remove-Item -LiteralPath $tmpDir -Recurse -Force
}

New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null

try {
    $archivePathLower = $ArchivePath.ToLowerInvariant()
    $ext = [System.IO.Path]::GetExtension($ArchivePath).ToLowerInvariant()

    if ($ext -eq ".zip") {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $tmpDir)
    }
    elseif ($archivePathLower.EndsWith(".tar.gz") -or $archivePathLower.EndsWith(".tgz")) {
        if (-not (Get-Command tar -ErrorAction SilentlyContinue)) {
            throw "tar is required to extract '$ArchivePath' but was not found on PATH."
        }

        tar -xzf $ArchivePath -C $tmpDir
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to extract tar archive: $ArchivePath"
        }
    }
    else {
        throw "Unsupported archive format for nvCOMP: $ArchivePath"
    }

    # Deterministically pick the Windows loader-facing nvCOMP DLL.
    # Prefer exact 'nvcomp.dll', then fallback to any nvcomp*.dll.
    $nvcompCandidates = Get-ChildItem -Path $tmpDir -Recurse -File -Filter "nvcomp*.dll" |
        Sort-Object FullName
    $nvcompDll = $nvcompCandidates |
        Where-Object { $_.Name -ieq "nvcomp.dll" } |
        Select-Object -First 1
    if ($null -eq $nvcompDll) {
        $nvcompDll = $nvcompCandidates | Select-Object -First 1
    }

    if ($null -eq $nvcompDll) {
        throw "Could not locate nvcomp*.dll in extracted archive."
    }

    # Prefer the highest cudart version if multiple are present.
    $cudartDll = Get-ChildItem -Path $tmpDir -Recurse -File -Filter "cudart64_*.dll" |
        Sort-Object Name -Descending |
        Select-Object -First 1

    $nvcompOutPath = Join-Path $OutputDir $nvcompDll.Name
    Copy-Item -LiteralPath $nvcompDll.FullName -Destination $nvcompOutPath -Force
    if (-not (Test-Path -LiteralPath $nvcompOutPath)) {
        throw "Failed to copy $($nvcompDll.Name) to $OutputDir"
    }
    Write-Host "Copied $($nvcompDll.Name) to $OutputDir" -ForegroundColor Green

    $cudartOutPath = $null
    if ($null -ne $cudartDll) {
        $cudartOutPath = Join-Path $OutputDir $cudartDll.Name
        Copy-Item -LiteralPath $cudartDll.FullName -Destination $cudartOutPath -Force
        if (-not (Test-Path -LiteralPath $cudartOutPath)) {
            throw "Failed to copy $($cudartDll.Name) to $OutputDir"
        }
        Write-Host "Copied $($cudartDll.Name) to $OutputDir" -ForegroundColor Green
    }
    else {
        Write-Host "No cudart64_*.dll found in archive. Ensure CUDA runtime is installed and on PATH." -ForegroundColor Yellow
    }

    if (-not $NoCopyToRoot) {
        $nvcompRootCopy = Join-Path $repoRoot $nvcompDll.Name
        Copy-Item -LiteralPath $nvcompOutPath -Destination $nvcompRootCopy -Force
        Write-Host "Copied $($nvcompDll.Name) to repo root for local execution." -ForegroundColor Green

        if ($null -ne $cudartOutPath) {
            $cudartRootCopy = Join-Path $repoRoot $cudartDll.Name
            Copy-Item -LiteralPath $cudartOutPath -Destination $cudartRootCopy -Force
            Write-Host "Copied $($cudartDll.Name) to repo root for local execution." -ForegroundColor Green
        }
    }

    Write-Host "nvCOMP dependency setup complete." -ForegroundColor Cyan
    Write-Host "Output directory: $OutputDir" -ForegroundColor Cyan
}
finally {
    if (Test-Path -LiteralPath $tmpDir) {
        Remove-Item -LiteralPath $tmpDir -Recurse -Force
    }
}
