[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Session,
    [Parameter(Mandatory = $true)][string]$TypeName,
    [Parameter(Mandatory = $true)][string]$MethodName,
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 60
)

# Calls one static method through a named isolated editor session's MCP endpoint and
# writes the result as JSON. Use it to read diagnostics inside a timed window, for example
# GPUPhysicsChainDispatcher.CaptureStageTimings twice and take the difference.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sessionTool = Join-Path $repoRoot 'Tools\Manage-McpEditorSession.ps1'
$view = & $sessionTool Status -Name $Session -AsJson | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$view.endpoint)) {
    throw "Session '$Session' has no MCP endpoint (state: $($view.state))."
}

$request = @{
    jsonrpc = '2.0'
    id = [guid]::NewGuid().ToString()
    method = 'tools/call'
    params = @{
        name = 'invoke_method'
        arguments = @{ type_name = $TypeName; method_name = $MethodName; arguments = @() }
    }
}
$response = Invoke-RestMethod -Uri $view.endpoint -Method Post -ContentType 'application/json' `
    -Body ($request | ConvertTo-Json -Depth 10 -Compress) -TimeoutSec $TimeoutSeconds
if ($null -ne $response.PSObject.Properties['error']) {
    throw "MCP invoke_method failed: $($response.error.message)"
}
if ($null -eq $response.PSObject.Properties['result'] -or $response.result.isError) {
    $message = if ($null -ne $response.result.content) { ($response.result.content | ForEach-Object { $_.text }) -join ' ' } else { 'No response.' }
    throw "MCP invoke_method failed: $message"
}
$response.result.structuredContent.result | ConvertTo-Json -Depth 8 -Compress
