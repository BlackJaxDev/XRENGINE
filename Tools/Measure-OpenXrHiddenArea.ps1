# Measures the OpenXR hidden-area mask of the connected headset for the Advanced hidden-area tile
# reject. The tool runs one short isolated editor session on the active OpenXR runtime (for
# example SteamVR with the headset connected) and prints the per-eye lines of
# advanced-hidden-area.log:
#   maskHiddenFraction     = fraction of the eye image that the runtime mask hides
#   ellipseOutsideFraction = fraction of the eye image that the tile reject can skip
#
# Before you run it: start the OpenXR runtime with the headset connected, make it the active
# OpenXR runtime, and close every XRENGINE editor. Run it from the repository root:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Measure-OpenXrHiddenArea.ps1
#
# The session uses a copy of Assets/UnitTestingWorldSettings.jsonc with Vulkan, the OpenXR launch
# mode and RVC eyes in SinglePassStereo. The tool does not change the original settings file.
[CmdletBinding()]
param(
    [ValidateRange(30, 1800)][int]$Seconds = 90,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$SessionName = 'hidden-area-openxr',
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location $repoRoot
$manage = Join-Path $PSScriptRoot 'Manage-McpEditorSession.ps1'

$busy = Get-Process -Name 'XREngine.Editor', 'monado-service' -ErrorAction SilentlyContinue
if ($busy)
{
    throw "Close these processes first: $(($busy | ForEach-Object { "$($_.ProcessName)($($_.Id))" }) -join ', ')"
}
if (-not (Get-Process -Name 'vrserver' -ErrorAction SilentlyContinue))
{
    Write-Warning 'SteamVR (vrserver) is not running. Another OpenXR runtime must then be active.'
}

$sourceSettings = Join-Path $repoRoot 'Assets\UnitTestingWorldSettings.jsonc'
if (-not (Test-Path $sourceSettings))
{
    throw 'Assets/UnitTestingWorldSettings.jsonc does not exist. Run Tools/Generate-UnitTestingWorldSettings.ps1 first.'
}

# Replace the first value of a key after a marker, so that the VR group keys change and the
# Foveation "Mode" and the desktop "RenderPipeline" keys stay as they are.
function Set-JsoncValueAfter([string]$Text, [string]$Marker, [string]$Key, [string]$Value)
{
    $start = $Text.IndexOf($Marker)
    if ($start -lt 0) { throw "Settings marker $Marker not found." }
    $pattern = [regex]::new('"' + $Key + '":\s*"[^"]*"')
    $match = $pattern.Match($Text, $start)
    if (-not $match.Success) { throw "Settings key $Key not found after $Marker." }
    return $Text.Substring(0, $match.Index) + '"' + $Key + '": "' + $Value + '"' + $Text.Substring($match.Index + $match.Length)
}

$text = Get-Content $sourceSettings -Raw
$text = Set-JsoncValueAfter $text '"Rendering": {' 'RenderBackend' 'Vulkan'
$text = Set-JsoncValueAfter $text '"VR": {' 'Mode' 'OpenXR'
$text = Set-JsoncValueAfter $text '"VR": {' 'ViewRenderMode' 'SinglePassStereo'
$text = Set-JsoncValueAfter $text '"VR": {' 'RenderPipeline' 'Rvc'
$workDirectory = Join-Path $repoRoot 'Build\_AgentValidation\00000000-000000-shared\hidden-area'
[IO.Directory]::CreateDirectory($workDirectory) | Out-Null
$settings = Join-Path $workDirectory 'hidden-area-openxr.jsonc'
Set-Content -Path $settings -Value $text -Encoding UTF8

$startArgs = @{
    Name = $SessionName
    Configuration = $Configuration
    StartupTimeoutSeconds = 1800
    SessionEnvironment = @{ XRE_UNIT_TEST_WORLD_SETTINGS_PATH = $settings }
}
if ($NoBuild) { $startArgs['NoBuild'] = $true }
$started = Get-Date
& $manage Start @startArgs | Out-Host
try
{
    Write-Host "Waiting $Seconds s. Keep the headset awake so that the OpenXR session renders the eyes."
    Start-Sleep -Seconds $Seconds
    $sessionsRoot = Join-Path $repoRoot 'Build\_AgentValidation\00000000-000000-shared\mcp-sessions'
    $log = Get-ChildItem -Path $sessionsRoot -Recurse -Filter 'advanced-hidden-area.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $started } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $log)
    {
        Write-Warning 'No advanced-hidden-area.log was written. The OpenXR eyes did not render, or the session did not start.'
    }
    else
    {
        Write-Host "== $($log.FullName)"
        Get-Content $log.FullName | Out-Host
    }
}
finally
{
    & $manage Stop -Name $SessionName | Out-Host
}
