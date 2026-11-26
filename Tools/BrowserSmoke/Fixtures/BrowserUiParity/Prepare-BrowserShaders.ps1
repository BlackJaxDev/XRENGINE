Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
& (Join-Path $repositoryRoot 'Tools/Cook-EngineBrowserShaders.ps1') `
    -OutputDirectory (Join-Path $PSScriptRoot 'Assets/Shaders/WebGPU')
