[CmdletBinding()]
param(
    # Named isolated MCP editor session. Never reuse a session you did not start.
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$Name = 'aot-parity',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # Parity mode applied to the editor process through XRE_AOT_PARITY.
    [ValidateSet('warn', 'error')]
    [string]$Mode = 'error',

    # World asset to load before entering play mode. Defaults to the MonkeyBall sample world.
    [string]$WorldAssetPath = 'Samples/MonkeyBallVR/Assets/Worlds/MonkeyBallWorld.asset',

    [string]$ProjectPath = 'Samples/MonkeyBallVR/MonkeyBallVR.xrproj',

    # Seconds to remain in play mode before exiting and collecting logs.
    [ValidateRange(1, 3600)][int]$PlaySeconds = 10,

    # Directory that receives the archived session log and the summary JSON.
    [string]$OutputDirectory = '',

    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Enters MonkeyBall play mode in an isolated editor session with development parity
# diagnostics enabled, then archives the session log. In error mode a reflective
# fallback on the player path throws AotParityViolationException inside the editor,
# which the log records; the smoke reports whether any such line appeared.

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manage = Join-Path $PSScriptRoot 'Manage-McpEditorSession.ps1'
$invoke = Join-Path $PSScriptRoot 'Invoke-Mcp.ps1'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "Build/_AgentValidation/00000000-000000-shared/aot-parity/$((Get-Date).ToString('yyyyMMdd-HHmmss'))-$Name"
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$environmentFile = Join-Path $OutputDirectory 'session-environment.json'
@{ XRE_AOT_PARITY = $Mode } | ConvertTo-Json | Set-Content -LiteralPath $environmentFile -Encoding UTF8

$startArgs = @{ Action = 'Start'; Name = $Name; Configuration = $Configuration; SessionEnvironmentFile = $environmentFile; NoUnitTesting = $true; AsJson = $true }
if ($NoBuild) { $startArgs.NoBuild = $true }

function Invoke-CheckedMcp([string]$Method, [hashtable]$Parameters) {
    $response = & $invoke -Session $Name -Method $Method -Params $Parameters -TimeoutSec 600 | ConvertFrom-Json
    if ($null -eq $response -or $response.PSObject.Properties.Name -contains 'error') {
        throw "MCP $Method failed: $($response | ConvertTo-Json -Depth 8 -Compress)"
    }
    if ($response.result.PSObject.Properties.Name -contains 'isError' -and $response.result.isError) {
        throw "MCP tool failed: $($response.result | ConvertTo-Json -Depth 8 -Compress)"
    }
    return $response.result
}

function Wait-PlayState([bool]$Expected) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $resource = Invoke-CheckedMcp 'resources/read' @{ uri = 'xrengine://engine/state' }
        $state = $resource.contents[0].text | ConvertFrom-Json
        if ([bool]$state.inPlayMode -eq $Expected -and ($Expected -or [bool]$state.inEditMode)) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Play-mode transition did not complete (expected inPlayMode=$Expected)."
}

$violationLines = @()
$sessionJson = $null
$failure = $null
try {
    $sessionJson = & $manage @startArgs | ConvertFrom-Json
    if (-not $sessionJson.McpReady) { throw 'Parity editor session did not become ready.' }

    $projectFullPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $ProjectPath))
    Invoke-CheckedMcp 'tools/call' @{ name = 'load_game_project'; arguments = @{ project_path = $projectFullPath } } | Out-Null
    Invoke-CheckedMcp 'tools/call' @{ name = 'compile_game_scripts'; arguments = @{ config = $Configuration; platform = 'Any CPU' } } | Out-Null

    $worldFullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $WorldAssetPath))
    Invoke-CheckedMcp 'tools/call' @{ name = 'load_world'; arguments = @{ asset_path = $worldFullPath } } | Out-Null
    Invoke-CheckedMcp 'tools/call' @{ name = 'enter_play_mode'; arguments = @{} } | Out-Null
    Wait-PlayState $true
    Start-Sleep -Seconds $PlaySeconds
    Wait-PlayState $true
    Invoke-CheckedMcp 'tools/call' @{ name = 'exit_play_mode'; arguments = @{} } | Out-Null
    Wait-PlayState $false
}
catch { $failure = $_.Exception.Message }
finally {
    if ($null -ne $sessionJson) {
        try { & $manage -Action Stop -Name $Name | Out-Null }
        catch { if ($null -eq $failure) { $failure = $_.Exception.Message } }
    }
}

$logSource = if ($null -ne $sessionJson) { [string]$sessionJson.Logs } else { '' }
if (-not [string]::IsNullOrWhiteSpace($logSource) -and (Test-Path -LiteralPath $logSource)) {
    Copy-Item -LiteralPath $logSource -Destination (Join-Path $OutputDirectory 'logs') -Recurse -Force
    $violationLines = @(Get-ChildItem -LiteralPath (Join-Path $OutputDirectory 'logs') -Recurse -File |
        Select-String -Pattern '\[AotParity\]|AotParityViolationException' |
        ForEach-Object { $_.Line })
    if (@(Get-ChildItem -LiteralPath (Join-Path $OutputDirectory 'logs') -Recurse -File).Count -eq 0) {
        $failure = 'Parity smoke log directory was empty; the run cannot pass.'
    }
}
elseif ($null -eq $failure) { $failure = 'Parity smoke produced no session logs; the run cannot pass.' }

$summary = [pscustomobject]@{
    session = $Name
    mode = $Mode
    world = $WorldAssetPath
    project = $ProjectPath
    playSeconds = $PlaySeconds
    violationCount = $violationLines.Count
    violations = $violationLines
    failure = $failure
    outputDirectory = $OutputDirectory
}
$summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'summary.json') -Encoding UTF8
$summary | ConvertTo-Json -Depth 4

if ($violationLines.Count -gt 0 -or $null -ne $failure) {
    exit 1
}
