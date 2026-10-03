[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
$allowed = [IO.Path]::GetFullPath((Join-Path $root 'Build/_AgentValidation')) + [IO.Path]::DirectorySeparatorChar
$relative = [IO.Path]::GetRelativePath($allowed, $output).Replace('\', '/')
if (-not $output.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or
    $relative -notmatch '^[^/]+/reports(?:/|$)') {
    throw 'OutputDirectory must be Build/_AgentValidation/<run>/reports/ (or a child directory).'
}

$build = Join-Path (Split-Path $output -Parent) 'temp-build/native-subsystem-inventory'
New-Item -ItemType Directory -Force $output, $build | Out-Null
$project = Join-Path $PSScriptRoot 'NativeSubsystemInventory/NativeSubsystemInventory.csproj'
dotnet run --project $project -p:BaseIntermediateOutputPath="$build/obj/" -p:BaseOutputPath="$build/bin/" -- $root $output
if ($LASTEXITCODE -ne 0) { throw "Native subsystem inventory failed ($LASTEXITCODE)." }
