<#
.SYNOPSIS
    Downloads / copies Ultralight runtime resources (icudt67l.dat, cacert.pem)
    into the expected location for XREngine.

.DESCRIPTION
    The UltralightNet NuGet packages ship native DLLs but NOT the resource files
    required for full Unicode text rendering and HTTPS certificate validation.
    These resource files come from the official Ultralight SDK.

    This script will:
      1. Check if resources are already installed.
      2. If an SDK path is provided (-SdkPath), copy them from that location.
      3. Otherwise, print manual download instructions.

.PARAMETER SdkPath
    Path to an extracted Ultralight SDK directory containing a "resources" subfolder.
    Example: -SdkPath "C:\ultralight-sdk-1.3.0-win-x64"

.EXAMPLE
    .\Get-UltralightResources.ps1
    .\Get-UltralightResources.ps1 -SdkPath "C:\ultralight-sdk"
#>
[CmdletBinding()]
param(
    [string]$SdkPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$targetDir = Join-Path $repoRoot 'Build\Dependencies\Ultralight\resources'
$requiredFile = 'icudt67l.dat'
$optionalFiles = @('cacert.pem')

function Test-ResourcesInstalled {
    $datFile = Join-Path $targetDir $requiredFile
    return (Test-Path $datFile)
}

function Copy-ResourceFiles {
    param([string]$SourceDir)
    
    if (-not (Test-Path $SourceDir)) {
        Write-Error "Source directory not found: $SourceDir"
        return $false
    }
    
    $datSource = Join-Path $SourceDir $requiredFile
    if (-not (Test-Path $datSource)) {
        # Try looking inside a 'resources' subdirectory
        $altSource = Join-Path $SourceDir 'resources'
        $datAlt = Join-Path $altSource $requiredFile
        if (Test-Path $datAlt) {
            $SourceDir = $altSource
        } else {
            Write-Error "Could not find $requiredFile in: $SourceDir (or $altSource)"
            return $false
        }
    }
    
    if (-not (Test-Path $targetDir)) {
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        Write-Host "  Created: $targetDir"
    }
    
    # Copy required file
    $src = Join-Path $SourceDir $requiredFile
    $dst = Join-Path $targetDir $requiredFile
    Copy-Item -Path $src -Destination $dst -Force
    Write-Host "  Copied: $requiredFile"
    
    # Copy optional files if they exist
    foreach ($file in $optionalFiles) {
        $src = Join-Path $SourceDir $file
        if (Test-Path $src) {
            $dst = Join-Path $targetDir $file
            Copy-Item -Path $src -Destination $dst -Force
            Write-Host "  Copied: $file"
        }
    }
    
    return $true
}

# ── Main ───────────────────────────────────────────────────────────────────────

Write-Host ''
Write-Host '=== XREngine: Ultralight Resource Setup ===' -ForegroundColor Cyan
Write-Host ''

if (Test-ResourcesInstalled) {
    Write-Host "[OK] Ultralight resources are already installed at:" -ForegroundColor Green
    Write-Host "     $targetDir"
    Write-Host ''
    
    Get-ChildItem -Path $targetDir -File | ForEach-Object {
        Write-Host "     - $($_.Name) ($([math]::Round($_.Length / 1KB, 1)) KB)"
    }
    Write-Host ''
    return
}

Write-Host "[!] Ultralight resources are NOT installed." -ForegroundColor Yellow
Write-Host "    Expected location: $targetDir"
Write-Host ''

if ($SdkPath) {
    Write-Host "Copying resources from: $SdkPath" -ForegroundColor Cyan
    $ok = Copy-ResourceFiles -SourceDir $SdkPath
    if ($ok) {
        Write-Host ''
        Write-Host "[OK] Resources installed successfully!" -ForegroundColor Green
    }
    return
}

# No SDK path provided — print manual instructions
Write-Host '--- Manual Setup Instructions ---' -ForegroundColor Cyan
Write-Host ''
Write-Host 'The Ultralight SDK includes resource files needed for full text rendering.'
Write-Host 'The UltralightNet NuGet packages do NOT include these files.'
Write-Host ''
Write-Host '  Steps:' -ForegroundColor White
Write-Host '  1. Go to https://ultralig.ht/download and sign up / log in.'
Write-Host '  2. Download the Ultralight SDK for Windows x64.'
Write-Host '  3. Extract the archive.'
Write-Host '  4. Run this script again with -SdkPath pointing to the extracted folder:'
Write-Host ''
Write-Host "     .\Get-UltralightResources.ps1 -SdkPath ""C:\path\to\ultralight-sdk""" -ForegroundColor Yellow
Write-Host ''
Write-Host '  Or manually copy these files into:' -ForegroundColor White
Write-Host "     $targetDir"
Write-Host ''
Write-Host "     Required:  $requiredFile  (ICU Unicode data)" 
Write-Host '     Optional:  cacert.pem     (CA certificate bundle for HTTPS)'
Write-Host ''
Write-Host 'NOTE: XREngine will still start without these resources, but some web' -ForegroundColor DarkYellow
Write-Host '      content may not render correctly (especially international text).' -ForegroundColor DarkYellow
Write-Host ''
