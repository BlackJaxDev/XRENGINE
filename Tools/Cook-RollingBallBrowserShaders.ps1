Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repositoryRoot 'Build/CommonAssets/Shaders/WebGPU'
$output = Join-Path $repositoryRoot 'Samples/RollingBall/Assets/Shaders/WebGPU'
$cooker = Join-Path $repositoryRoot 'Tools/ShaderCooker/ShaderCooker.csproj'
$recipes = @(
    'engine-standard-lit-color.recipe.json',
    'engine-standard-lit-color-directional-shadow.recipe.json',
    'engine-shadow-depth.recipe.json',
    'engine-debug-point.recipe.json',
    'engine-debug-line.recipe.json',
    'engine-debug-triangle.recipe.json',
    'engine-tonemap.recipe.json'
)

$arguments = @('run', '--project', $cooker, '-c', 'Release', '--', '--source-root', $sourceRoot, '--output', $output)
foreach ($recipe in $recipes) {
    $arguments += @('--recipe', (Join-Path $sourceRoot $recipe))
}

Push-Location $repositoryRoot
try {
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Rolling Ball browser shader cooking failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $output 'manifest.json') -PathType Leaf)) {
        throw 'Rolling Ball browser shader cooking did not publish a manifest.'
    }
}
finally {
    Pop-Location
}
