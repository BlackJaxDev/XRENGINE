Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sourceRoot = Join-Path $PSScriptRoot 'Shaders/WebGPU'
$output = Join-Path $PSScriptRoot 'Assets/Shaders/WebGPU'
$cooker = Join-Path $repositoryRoot 'Tools/ShaderCooker/ShaderCooker.csproj'

& dotnet run --project $cooker -c Release -- `
    --recipe (Join-Path $sourceRoot 'modular-gradient.recipe.json') `
    --recipe (Join-Path $sourceRoot 'modular-msaa-scene.recipe.json') `
    --recipe (Join-Path $sourceRoot 'modular-msaa-present.recipe.json') `
    --source-root $sourceRoot --output $output
if ($LASTEXITCODE -ne 0) {
    throw "Modular browser shader cooking failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath (Join-Path $output 'manifest.json') -PathType Leaf)) {
    throw 'Modular browser shader cooking did not produce a manifest.'
}
