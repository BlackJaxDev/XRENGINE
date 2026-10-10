Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
& (Join-Path $repositoryRoot 'Tools/Cook-EngineBrowserShaders.ps1') `
    -OutputDirectory (Join-Path $PSScriptRoot 'Assets/Shaders/WebGPU')
