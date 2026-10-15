[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Session,
    [Parameter(Mandatory = $true)][string]$ToolName,
    [string]$ArgumentsJson = '{}',
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 60
)

# Calls one MCP tool through a named isolated editor session's endpoint and writes its
# structured result as JSON. Use it to read diagnostics inside a timed window, for example
# get_s13a_publication_telemetry twice and take the difference.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$sessionTool = Join-Path $repoRoot 'Tools\Manage-McpEditorSession.ps1'
$view = & $sessionTool Status -Name $Session -AsJson | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$view.endpoint)) {
    throw "Session '$Session' has no MCP endpoint (state: $($view.state))."
}

$arguments = $ArgumentsJson | ConvertFrom-Json -AsHashtable
$request = @{
    jsonrpc = '2.0'
    id = [guid]::NewGuid().ToString()
    method = 'tools/call'
    params = @{ name = $ToolName; arguments = $arguments }
}
$response = Invoke-RestMethod -Uri $view.endpoint -Method Post -ContentType 'application/json' `
    -Body ($request | ConvertTo-Json -Depth 20 -Compress) -TimeoutSec $TimeoutSeconds
if ($null -ne $response.PSObject.Properties['error']) {
    throw "MCP $ToolName failed: $($response.error.message)"
}
if ($null -eq $response.PSObject.Properties['result'] -or $response.result.isError) {
    $message = if ($null -ne $response.result.content) { ($response.result.content | ForEach-Object { $_.text }) -join ' ' } else { 'No response.' }
    throw "MCP $ToolName failed: $message"
}
$response.result.structuredContent | ConvertTo-Json -Depth 20 -Compress
