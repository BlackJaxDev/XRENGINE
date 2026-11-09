<#
.SYNOPSIS
Downloads a portable ffmpeg build that has the gfxcapture and ddagrab capture filters.

.DESCRIPTION
Downloads the BtbN FFmpeg-Builds nightly (win64, GPL) from GitHub, which is about 200 MB,
and extracts it to Build/_AgentValidation/00000000-000000-shared/tools/ffmpeg. The script
installs nothing and does not change PATH. Tools/Record-WindowVideo.ps1 finds this copy.

Agents: ask the user before you run this script, because it downloads a file.
#>
param(
    [string]$Url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$toolsRoot = Join-Path $repoRoot 'Build\_AgentValidation\00000000-000000-shared\tools'
$target = Join-Path $toolsRoot 'ffmpeg'
$existing = Get-ChildItem -Path $target -Filter ffmpeg.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if ($existing -and -not $Force)
{
    "Portable ffmpeg already exists: $($existing.FullName). Use -Force to download it again."
    return
}

New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
$zip = Join-Path $toolsRoot 'ffmpeg-download.zip'
"Downloading $Url"
Invoke-WebRequest -Uri $Url -OutFile $zip -UseBasicParsing
if (Test-Path $target)
{
    Remove-Item -Recurse -Force $target
}
Expand-Archive -Path $zip -DestinationPath $target
Remove-Item -Force $zip

$ffmpeg = Get-ChildItem -Path $target -Filter ffmpeg.exe -Recurse | Select-Object -First 1
if (-not $ffmpeg)
{
    throw "The archive did not contain ffmpeg.exe."
}
$filters = & $ffmpeg.FullName -hide_banner -filters 2>$null
foreach ($filter in 'gfxcapture', 'ddagrab')
{
    if (-not ($filters | Select-String -SimpleMatch $filter))
    {
        Write-Warning "This ffmpeg build has no '$filter' filter."
    }
}
"Portable ffmpeg: $($ffmpeg.FullName)"
