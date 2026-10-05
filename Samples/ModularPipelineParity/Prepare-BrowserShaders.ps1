[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'Assets/Shaders/WebGPU')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sampleRoot = Join-Path $PSScriptRoot 'Shaders/WebGPU'
$engineRoot = Join-Path $repositoryRoot 'Build/CommonAssets/Shaders/WebGPU'
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$cooker = Join-Path $repositoryRoot 'Tools/ShaderCooker/ShaderCooker.csproj'
$sampleRecipes = @('modular-gradient', 'modular-msaa-scene', 'modular-msaa-present')
$engineRecipes = @('engine-indirect-cull-primitive', 'engine-meshlets-select-lod',
    'engine-authored-rank-sources', 'engine-authored-mask-ranked-arguments')
$scratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('xr-modular-browser-shaders-' + [Guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $scratchRoot 'WebGPU'

Push-Location $repositoryRoot
try {
    # One cooker invocation publishes one manifest. Keep the sample's programs
    # and the canonical indirect companions in the same source root and catalog.
    New-Item -ItemType Directory -Path $scratchRoot | Out-Null
    Copy-Item -LiteralPath $sampleRoot -Destination $sourceRoot -Recurse
    foreach ($name in $engineRecipes) {
        Copy-Item -LiteralPath (Join-Path $engineRoot "$name.recipe.json") -Destination $sourceRoot
        Copy-Item -LiteralPath (Join-Path $engineRoot "$name.wgsl") -Destination $sourceRoot
    }

    $arguments = @('run', '--project', $cooker, '-c', 'Release', '--',
        '--source-root', $sourceRoot, '--output', $output)
    foreach ($name in ($sampleRecipes + $engineRecipes)) {
        $arguments += @('--recipe', (Join-Path $sourceRoot "$name.recipe.json"))
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Modular browser shader cooking failed with exit code $LASTEXITCODE."
    }
    $manifestPath = Join-Path $output 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'Modular browser shader cooking did not produce a manifest.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($binding in @('custom::custom-pass', 'custom::msaa-scene', 'custom::msaa-present',
        'indirect::cull-primitive', 'meshlets::select-lod',
        'authored-indexed::rank-sources', 'authored-indexed::mask-ranked-arguments')) {
        $parts = $binding -split '::'
        if (-not @($manifest.pipelineArtifacts | Where-Object {
            $_.scope -eq $parts[0] -and $_.pass -eq $parts[1]
        }).Count) {
            throw "Modular browser shader manifest is missing '$binding'."
        }
    }
}
finally {
    if (Test-Path -LiteralPath $scratchRoot) {
        Remove-Item -LiteralPath $scratchRoot -Recurse -Force
    }
    Pop-Location
}
