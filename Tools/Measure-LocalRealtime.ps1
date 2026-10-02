[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 3600)][int]$WarmupSeconds = 10,
    [ValidateRange(1, 3600)][int]$MeasureSeconds = 30,
    [ValidateRange(1024, 65532)][int]$BasePort = 25000,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$manage = Join-Path $PSScriptRoot 'Manage-McpEditorSession.ps1'
$prefix = 'runtime-net-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$sessions = [Collections.Generic.List[object]]::new()
$attemptedNames = [Collections.Generic.List[string]]::new()
$report = [ordered]@{ version = 1; warmupSeconds = $WarmupSeconds; measureSeconds = $MeasureSeconds; sessions = @(); failure = $null }

function Invoke-NetworkTool($Session, [string]$Tool, [hashtable]$Arguments = @{}) {
    $body = @{ jsonrpc = '2.0'; id = [Guid]::NewGuid().ToString(); method = 'tools/call'; params = @{ name = $Tool; arguments = $Arguments } } | ConvertTo-Json -Depth 6
    $reply = Invoke-RestMethod -Uri $Session.Endpoint -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 60
    if ($reply.PSObject.Properties.Name -contains 'error' -or $reply.result.isError) {
        throw "Network measurement request failed: $($reply | ConvertTo-Json -Depth 8 -Compress)"
    }
    return $reply.result.structuredContent
}

try {
    foreach ($role in @('server', 'sender', 'receiver')) {
        $name = "$prefix-$role"
        $environment = @{
            XRE_WORLD_MODE = 'UnitTesting'; XRE_UNIT_TEST_WORLD_KIND = 'NetworkingPose'; XRE_NETWORKING_POSE_ROLE = $role
            XRE_NET_MODE = $(if ($role -eq 'server') { 'Server' } else { 'Client' })
            XRE_UDP_SERVER_BIND_PORT = "$BasePort"; XRE_UDP_SERVER_SEND_PORT = "$BasePort"
            XRE_UDP_BIND_PORT = "$BasePort"; XRE_UDP_ADVERTISED_PORT = "$BasePort"; XRE_UDP_MULTICAST_PORT = "$BasePort"
            XRE_UDP_CLIENT_RECEIVE_PORT = "$(if ($role -eq 'sender') { $BasePort + 1 } else { $BasePort + 2 })"
            XRE_POSE_ENTITY_ID = '4242'; XRE_POSE_BROADCAST_ENABLED = $(if ($role -eq 'sender') { '1' } else { '0' })
            XRE_POSE_RECEIVE_ENABLED = $(if ($role -eq 'sender') { '0' } else { '1' })
        }
        $environmentPath = Join-Path $outputRoot "$role-environment.json"
        $environment | ConvertTo-Json | Set-Content -LiteralPath $environmentPath -Encoding utf8
        $attemptedNames.Add($name)
        $session = & $manage -Action Start -Name $name -Configuration $Configuration -SessionEnvironmentFile $environmentPath -AsJson | ConvertFrom-Json
        if (-not $session.McpReady) { throw "$role session did not become ready." }
        $sessions.Add($session)
        Invoke-NetworkTool $session 'set_network_runtime_measurements' @{ enabled = $true } | Out-Null
    }
    Start-Sleep -Seconds $WarmupSeconds
    $before = @($sessions | ForEach-Object { Invoke-NetworkTool $_ 'get_network_runtime_measurements' })
    if ($before[0].connectedPlayers -lt 2 -or -not $before[1].gameplayReady -or -not $before[2].gameplayReady) {
        throw 'Both clients must finish admission before measurement.'
    }
    Start-Sleep -Seconds $MeasureSeconds
    $after = @($sessions | ForEach-Object { Invoke-NetworkTool $_ 'get_network_runtime_measurements' })
    for ($i = 0; $i -lt $sessions.Count; $i++) {
        $deltas = [ordered]@{}
        foreach ($scope in @('send', 'receive', 'poseApply', 'poseRelay')) {
            $deltas[$scope] = [ordered]@{}
            foreach ($metric in @('samples', 'payloadBytes', 'allocatedBytes', 'milliseconds')) {
                $deltas[$scope][$metric] = $after[$i].$scope.$metric - $before[$i].$scope.$metric
            }
        }
        $report.sessions += @{ name = $sessions[$i].Name; before = $before[$i]; after = $after[$i]; delta = $deltas }
    }
    if ($report.sessions[0].delta.poseRelay.samples -eq 0 -or $report.sessions[2].delta.poseApply.samples -eq 0) {
        throw 'No server pose relay or receiver pose application was observed; the run cannot pass.'
    }
}
catch {
    $report.failure = $_.Exception.Message
}
finally {
    foreach ($name in $attemptedNames) {
        try { & $manage -Action Stop -Name $name | Out-Null }
        catch { if ($null -eq $report.failure) { $report.failure = $_.Exception.Message } }
    }
    foreach ($session in $sessions) {
        if (Test-Path -LiteralPath $session.Logs -PathType Container) {
            Copy-Item -LiteralPath $session.Logs -Destination (Join-Path $outputRoot $session.Name) -Recurse -Force
        }
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $outputRoot 'local-realtime.json') -Encoding utf8
}
if ($null -ne $report.failure) { throw $report.failure }
