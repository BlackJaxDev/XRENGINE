param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('Disabled', 'Shapes', 'Contacts', 'Joints', 'Simulation', 'All')]
    [string]$Preset = 'All',
    [int]$McpPort = 5467,
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputRoot = Join-Path $repoRoot "Build\_AgentValidation\$stamp-physics-debug-renderdoc\renderdoc"
}

$outputPath = [System.IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if (-not $outputPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain inside the repository: $outputPath"
}

$editorDll = Join-Path $repoRoot "Build\Editor\$Configuration\AnyCPU\$Configuration\net10.0-windows7.0\XREngine.Editor.dll"
if (-not (Test-Path -LiteralPath $editorDll)) {
    throw "Editor build not found: $editorDll"
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$capturePath = Join-Path $outputPath "physics-debug-$($Preset.ToLowerInvariant()).rdc"
$rdc = Get-Command rdc -ErrorAction SilentlyContinue
$renderDocCmd = 'C:\Program Files\RenderDoc\renderdoccmd.exe'
$env:XRE_PHYSICS_DEBUG_PRESET = $Preset

try {
    if ($null -ne $rdc) {
        & $rdc.Source capture -o $capturePath -- dotnet $editorDll --unit-testing --mcp --mcp-allow-all --mcp-port $McpPort
    }
    elseif (Test-Path -LiteralPath $renderDocCmd) {
        & $renderDocCmd capture -w -d $repoRoot -c $capturePath dotnet $editorDll --unit-testing --mcp --mcp-allow-all --mcp-port $McpPort
    }
    else {
        throw 'Neither rdc-cli nor RenderDoc renderdoccmd.exe is available.'
    }
}
finally {
    Remove-Item Env:\XRE_PHYSICS_DEBUG_PRESET -ErrorAction SilentlyContinue
}

Write-Host "RenderDoc capture target: $capturePath"
