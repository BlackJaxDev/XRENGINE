[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$shaderRoot = Join-Path $repositoryRoot 'Build/CommonAssets/Shaders'
$canonicalRoot = Join-Path $shaderRoot 'WebGPU'
$output = [System.IO.Path]::GetFullPath($OutputDirectory, $repositoryRoot)
$cooker = Join-Path $repositoryRoot 'Tools/ShaderCooker/ShaderCooker.csproj'
# The production recipe directory is the shared inventory. Probe recipes belong
# only to renderer diagnostics and must not enter a project's cooked catalog.
$recipes = @(Get-ChildItem -LiteralPath $canonicalRoot -Filter 'engine-*.recipe.json' -File |
    Where-Object { $_.Name -notlike '*-probe.recipe.json' } |
    Sort-Object Name)
if ($recipes.Count -eq 0) {
    throw 'The canonical engine WebGPU recipe inventory is missing.'
}

$scratchRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('xr-engine-browser-shaders-' + [Guid]::NewGuid().ToString('N'))
$sourceRoot = Join-Path $scratchRoot 'WebGPU'
Push-Location $repositoryRoot
try {
    # Compose the two production source locations without changing the recipes or
    # duplicating the backend-owned WGSL kernel in tracked source. The stable
    # WebGPU directory name preserves relative dependency identities in recipes.
    New-Item -ItemType Directory -Path $scratchRoot | Out-Null
    Copy-Item -LiteralPath $canonicalRoot -Destination $sourceRoot -Recurse
    # Impostor and unlit provenance resolves the authored GLSL and snippet
    # closure relative to WebGPU's parent, just as in the canonical source tree.
    foreach ($directory in @('Common', 'Scene3D', 'Snippets')) {
        Copy-Item -LiteralPath (Join-Path $shaderRoot $directory) `
            -Destination (Join-Path $scratchRoot $directory) -Recurse
    }
    $computeDirectory = Join-Path $sourceRoot 'Assets'
    New-Item -ItemType Directory -Path $computeDirectory | Out-Null
    foreach ($kernel in @('gpu-skinning.wgsl', 'gpu-luminance.wgsl',
        'gpu-luminance-2d.wgsl', 'gpu-luminance-mipmap.wgsl')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "XREngine.Runtime.Rendering.WebGPU/Assets/$kernel") `
            -Destination (Join-Path $computeDirectory $kernel)
    }
    $arguments = @('run', '--project', $cooker, '-c', 'Release', '--', '--source-root', $sourceRoot, '--output', $output)
    foreach ($recipe in $recipes) {
        $arguments += @('--recipe', (Join-Path $sourceRoot $recipe.Name))
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Engine browser shader cooking failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $output 'manifest.json') -PathType Leaf)) {
        throw 'Engine browser shader cooking did not produce a manifest.'
    }
}
finally {
    if (Test-Path -LiteralPath $scratchRoot) {
        Remove-Item -LiteralPath $scratchRoot -Recurse -Force
    }
    Pop-Location
}
