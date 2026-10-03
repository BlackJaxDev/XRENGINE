[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectDir,
    [Parameter(Mandatory = $true)][string]$OutputFile,
    [Parameter(Mandatory = $true)][string]$BrowserManifest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $BrowserManifest -PathType Leaf)) {
    throw "Browser registration manifest is missing: $BrowserManifest"
}

    $expected = @{
        schema = 'xre.browser.registrations.v1'
        components = @{
            'xre.browser.v1.component.scene-boot' = 'XREngine.Browser.SceneBootComponent'
            'xre.browser.v1.component.mesh' = 'XREngine.Browser.BrowserMeshComponent'
            'xre.browser.v1.component.spin' = 'XREngine.Browser.BrowserSpinComponent'
        }
        transforms = @{ 'xre.browser.v1.transform.trs' = 'XREngine.Scene.Transforms.Transform' }
        resources = @{
            'xre.browser.v1.resource.mesh' = 'XREngine.Rendering.BrowserMeshData'
            'xre.browser.v1.resource.texture-rgba8' = 'XREngine.Rendering.BrowserTextureData'
            'xre.browser.v1.resource.material-unlit' = 'XREngine.Rendering.BrowserMaterialData'
            'xre.browser.v1.resource.scene-snapshot' = 'XREngine.Rendering.BrowserSceneSnapshot'
        }
        serializers = @{ 'xre.browser.v1.serializer.scene-json' = 'XREngine.Rendering.BrowserSceneSnapshot' }
        modules = @{ 'xre.browser.v1.module.webgpu' = 'XREngine.Rendering.WebGPU.WebGpuRendererBackendModule' }
    }
    $manifest = Get-Content -LiteralPath $BrowserManifest -Raw | ConvertFrom-Json
    if (@($manifest.PSObject.Properties).Count -ne $expected.Count) { throw 'Browser registration manifest differs from the approved allow-list.' }
    foreach ($category in $expected.Keys) {
        if (-not (@($manifest.PSObject.Properties.Name) -contains $category)) { throw "Browser registration manifest lacks '$category'." }
        if ($category -eq 'schema') {
            if ($manifest.schema -cne $expected.schema) { throw 'Browser registration schema differs from the approved version.' }
            continue
        }
        $actualValues = $manifest.PSObject.Properties[$category].Value
        $expectedValues = $expected[$category]
        if (@($actualValues.PSObject.Properties).Count -ne $expectedValues.Count) { throw "Browser registration '$category' differs from the approved allow-list." }
        foreach ($id in $expectedValues.Keys) {
            if (-not (@($actualValues.PSObject.Properties.Name) -contains $id) -or
                $actualValues.PSObject.Properties[$id].Value -cne $expectedValues[$id]) {
                throw "Browser registration '$id' differs from the approved type/signature allow-list."
            }
        }
    }

    $template = Join-Path $PSScriptRoot '../Build/Registration/BrowserStaticRegistrations.template.txt'
    $content = (Get-Content -LiteralPath $template -Raw).Replace("`r`n", "`n")
    if (-not $content.EndsWith("`n")) { $content += "`n" }
    $outputDirectory = Split-Path -Parent $OutputFile
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    if ((Test-Path -LiteralPath $OutputFile) -and (Get-Content -LiteralPath $OutputFile -Raw) -ceq $content) { return }
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputFile), $content, [System.Text.UTF8Encoding]::new($false))
    return
