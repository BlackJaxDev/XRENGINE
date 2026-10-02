Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'Cook-EngineBrowserShaders.ps1') `
    -OutputDirectory 'Samples/RollingBall/Assets/Shaders/WebGPU'
