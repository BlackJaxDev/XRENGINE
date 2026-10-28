<#
.SYNOPSIS
Installs the pinned Windows Slang release and returns the path of its compiler.
.DESCRIPTION
The script downloads the pinned archive, checks its SHA-256 hash, and expands it
into the destination directory. The destination must not exist. The script
writes the full path of the one slangc.exe in the toolchain to the output.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$uri = 'https://github.com/shader-slang/slang/releases/download/v2026.8/slang-2026.8-windows-x86_64.zip'
$expectedHash = '7a3ffcbd267dbddb4818f68ade13da37e2d394dd305c7276fef964933388f184'

if (Test-Path -LiteralPath $Destination) { throw "The Slang toolchain destination must start empty: $Destination" }
$archive = "$Destination.zip"
New-Item -ItemType Directory -Path (Split-Path -Parent $archive) -Force | Out-Null
Invoke-WebRequest -Uri $uri -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) {
    throw 'The pinned Windows Slang archive hash does not match.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $Destination
$compilers = @(Get-ChildItem -LiteralPath $Destination -Filter 'slangc.exe' -File -Recurse)
if ($compilers.Count -ne 1) { throw 'Expected one compiler in the verified Slang archive.' }
Write-Output $compilers[0].FullName
