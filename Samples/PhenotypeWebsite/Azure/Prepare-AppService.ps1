[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedSite,
    [Parameter(Mandatory)][string]$OutputZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$site = (Resolve-Path -LiteralPath $PublishedSite).Path
if (-not (Test-Path -LiteralPath $site -PathType Container)) {
    throw 'PublishedSite must be the complete engine player output directory.'
}
foreach ($relative in @('index.html', 'player.js', 'browser-publish.json', 'webgpu/shaders/manifest.json', '_framework')) {
    if (-not (Test-Path -LiteralPath (Join-Path $site $relative))) {
        throw "Published player output is missing $relative."
    }
}
if (Test-Path -LiteralPath (Join-Path $site 'main.js')) {
    throw 'The developer harness is not a published native UI website. Use the editor project publisher.'
}
$launch = Get-Content -LiteralPath (Join-Path $site 'browser-publish.json') -Raw | ConvertFrom-Json
if ($launch.schema -ne 1 -or [string]::IsNullOrWhiteSpace($launch.world)) {
    throw 'The launch descriptor must select a cooked project world.'
}

$destination = [System.IO.Path]::GetFullPath($OutputZip)
if ([System.IO.Path]::GetExtension($destination) -ne '.zip') {
    throw 'OutputZip must end with .zip.'
}
if (Test-Path -LiteralPath $destination) {
    throw 'OutputZip already exists. Select a new output filename.'
}
$sitePrefix = $site.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if ($destination.StartsWith($sitePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputZip must be outside PublishedSite.'
}

$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('phenotype-site-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $staging | Out-Null
    Get-ChildItem -LiteralPath $site -Force | Copy-Item -Destination $staging -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'web.config') -Destination $staging -Force
    New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $destination)
    Write-Output "Prepared Windows App Service package: $destination"
    Write-Output 'This checks package structure only. Verify native UI rendering and input before deployment.'
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
