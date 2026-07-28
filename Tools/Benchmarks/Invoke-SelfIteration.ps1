[CmdletBinding()]
param(
    [string]$Config = 'XREngine.Benchmarks\SelfIteration\Examples\render-pipeline-self-iteration.jsonc',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$ValidateOnly,
    [switch]$BaselineOnly,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$projectPath = Join-Path $repoRoot 'XREngine.Benchmarks\XREngine.Benchmarks.csproj'
$configPath = if ([System.IO.Path]::IsPathRooted($Config)) {
    [System.IO.Path]::GetFullPath($Config)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Config))
}
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw "Self-iteration configuration does not exist: $configPath"
}

$arguments = [System.Collections.Generic.List[string]]::new()
$arguments.Add('run')
$arguments.Add('--project')
$arguments.Add($projectPath)
$arguments.Add('--configuration')
$arguments.Add($Configuration)
if ($NoBuild) {
    $arguments.Add('--no-build')
}
$arguments.Add('--')
$arguments.Add('--self-iterate')
$arguments.Add('--config')
$arguments.Add($configPath)
if ($ValidateOnly) {
    $arguments.Add('--validate-only')
}
if ($BaselineOnly) {
    $arguments.Add('--baseline-only')
}

Push-Location $repoRoot
try {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Self-iteration benchmark exited with code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
