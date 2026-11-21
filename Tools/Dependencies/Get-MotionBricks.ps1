<#!
.SYNOPSIS
    Downloads and verifies NVIDIA MotionBricks SOMA ONNX weights, manifest, and Smart Locomotion presets.

.DESCRIPTION
    Stages the SOMA human motion model assets from NVlabs/motionbricks into
    Build\Dependencies\MotionBricks by default (ignored by Git).

    By default, the script downloads:
    - soma_n1.json (model contract manifest)
    - mmm_v6_soma_fp16.onnx (mixed FP16 SOMA model, ~363 MB, verified by SHA-256)
    - SOMA Smart Locomotion primitive definitions (linear, directional, and crouch)

    Use -Precision fp32 or -Precision both to also stage mmm_v6_soma_fp32.onnx (~723 MB).
    Use -LocalDir to copy and verify files from an existing local NVlabs/motionbricks checkout.

.PARAMETER Precision
    Model precision variant to stage: fp16 (default), fp32, or both.

.PARAMETER OutputDir
    Target staging directory. Defaults to Build\Dependencies\MotionBricks.

.PARAMETER LocalDir
    Optional path to an existing local NVlabs/motionbricks checkout or asset folder.

.PARAMETER Force
    Re-download or recopy files even when they already exist and pass verification.

.PARAMETER SkipPresets
    Skip staging the Smart Locomotion primitive JSON files.

.EXAMPLE
    pwsh Tools\Dependencies\Get-MotionBricks.ps1

.EXAMPLE
    pwsh Tools\Dependencies\Get-MotionBricks.ps1 -Precision both
#>
[CmdletBinding()]
param(
    [ValidateSet("fp16", "fp32", "both")]
    [string]$Precision = "fp16",
    [string]$OutputDir,
    [string]$LocalDir,
    [switch]$Force,
    [switch]$SkipPresets
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "Build\Dependencies\MotionBricks"
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

$rawBaseUrl = "https://raw.githubusercontent.com/NVlabs/motionbricks/main"
$lfsBaseUrl = "https://media.githubusercontent.com/media/NVlabs/motionbricks/main"

$artifacts = @(
    [pscustomobject]@{
        RelativeTarget = "soma\soma_n1.json"
        UpstreamPath   = "motionbricks/assets/onnx/soma/soma_n1.json"
        Url            = "$rawBaseUrl/motionbricks/assets/onnx/soma/soma_n1.json"
        ExpectedSha256 = $null
        MinBytes       = 512
    }
)

if ($Precision -eq "fp16" -or $Precision -eq "both") {
    $artifacts += [pscustomobject]@{
        RelativeTarget = "soma\mmm_v6_soma_fp16.onnx"
        UpstreamPath   = "motionbricks/assets/onnx/soma/mmm_v6_soma_fp16.onnx"
        Url            = "$lfsBaseUrl/motionbricks/assets/onnx/soma/mmm_v6_soma_fp16.onnx"
        ExpectedSha256 = "6c9f78e23decbda8e1cfd1b0cc96980ea4b24b3ca2c808d03b058f48f82fc9ef"
        MinBytes       = 100000000
    }
}

if ($Precision -eq "fp32" -or $Precision -eq "both") {
    $artifacts += [pscustomobject]@{
        RelativeTarget = "soma\mmm_v6_soma_fp32.onnx"
        UpstreamPath   = "motionbricks/assets/onnx/soma/mmm_v6_soma_fp32.onnx"
        Url            = "$lfsBaseUrl/motionbricks/assets/onnx/soma/mmm_v6_soma_fp32.onnx"
        ExpectedSha256 = "856419dc72aeb14e591eebf4dd0ea05734a2506ddb00bcd39dd6dac2ad60f4fb"
        MinBytes       = 100000000
    }
}

if (-not $SkipPresets) {
    $presetFiles = @(
        "locomotion_bs1_linear.smart_primitive.json",
        "locomotion_bs2_directional.smart_primitive.json",
        "locomotion_bs2_directional_crouch.smart_primitive.json"
    )
    foreach ($preset in $presetFiles) {
        $artifacts += [pscustomobject]@{
            RelativeTarget = "smart_primitives\soma\$preset"
            UpstreamPath   = "assets/smart_primitives/soma/$preset"
            Url            = "$rawBaseUrl/assets/smart_primitives/soma/$preset"
            ExpectedSha256 = $null
            MinBytes       = 64
        }
    }
}

function Test-StagedFile {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string]$ExpectedSha256,
        [long]$MinBytes
    )

    if (-not (Test-Path -LiteralPath $FilePath)) {
        return $false
    }

    $info = Get-Item -LiteralPath $FilePath
    if ($info.Length -lt $MinBytes) {
        return $false
    }

    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        $actual = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()
        return $actual -eq $ExpectedSha256.ToLowerInvariant()
    }

    return $true
}

function Resolve-LocalCandidate {
    param(
        [Parameter(Mandatory)][string]$RootDir,
        [Parameter(Mandatory)][string]$UpstreamPath,
        [Parameter(Mandatory)][string]$RelativeTarget
    )

    $candidates = @(
        (Join-Path $RootDir ($UpstreamPath -replace "/", "\")),
        (Join-Path $RootDir $RelativeTarget),
        (Join-Path $RootDir (Split-Path $RelativeTarget -Leaf))
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return $null
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

foreach ($item in $artifacts) {
    $targetPath = Join-Path $OutputDir $item.RelativeTarget
    $targetDir = Split-Path -Parent $targetPath
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null

    if (-not $Force -and (Test-StagedFile -FilePath $targetPath -ExpectedSha256 $item.ExpectedSha256 -MinBytes $item.MinBytes)) {
        Write-Host "[OK] Already staged: $($item.RelativeTarget)" -ForegroundColor Green
        continue
    }

    $tempPath = "$targetPath.download"
    if (Test-Path -LiteralPath $tempPath) {
        Remove-Item -LiteralPath $tempPath -Force
    }

    if (-not [string]::IsNullOrWhiteSpace($LocalDir)) {
        $localCandidate = Resolve-LocalCandidate -RootDir $LocalDir -UpstreamPath $item.UpstreamPath -RelativeTarget $item.RelativeTarget
        if ($null -eq $localCandidate) {
            throw "Could not find $($item.UpstreamPath) under LocalDir '$LocalDir'."
        }

        Write-Host "[COPY] $($item.RelativeTarget) from $localCandidate" -ForegroundColor Cyan
        Copy-Item -LiteralPath $localCandidate -Destination $tempPath -Force
    }
    else {
        Write-Host "[GET]  $($item.RelativeTarget)" -ForegroundColor Cyan
        $curl = Get-Command "curl.exe" -ErrorAction SilentlyContinue
        if ($null -ne $curl) {
            & $curl.Source -fL --retry 3 --retry-delay 2 -o $tempPath $item.Url
            if ($LASTEXITCODE -ne 0) {
                throw "curl.exe failed with exit code $LASTEXITCODE while downloading $($item.Url)."
            }
        }
        else {
            Invoke-WebRequest -Uri $item.Url -OutFile $tempPath -UseBasicParsing
        }
    }

    if (-not (Test-StagedFile -FilePath $tempPath -ExpectedSha256 $item.ExpectedSha256 -MinBytes $item.MinBytes)) {
        $actualSize = (Get-Item -LiteralPath $tempPath).Length
        $actualHash = (Get-FileHash -LiteralPath $tempPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Remove-Item -LiteralPath $tempPath -Force
        throw "Verification failed for $($item.RelativeTarget). Size=$actualSize bytes, SHA256=$actualHash, Expected=$($item.ExpectedSha256)."
    }

    Move-Item -LiteralPath $tempPath -Destination $targetPath -Force
    Write-Host "[OK]   Staged and verified: $($item.RelativeTarget)" -ForegroundColor Green
}

Write-Host ""
Write-Host "MotionBricks SOMA assets are staged at: $OutputDir" -ForegroundColor Green
Write-Host "License notice: MotionBricks code is Apache-2.0; pretrained weights use the NVIDIA Open Model Agreement." -ForegroundColor Yellow
