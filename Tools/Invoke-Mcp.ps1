[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Method,
    [hashtable]$Params,
    [string]$Url = "http://localhost:5467/mcp/",
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$Session,
    [int]$TimeoutSec = 60,
    [int]$OutputDepth = 20
)

# Helper to call the XREngine MCP HTTP JSON-RPC server.
#
# Examples:
#   pwsh Tools/Invoke-Mcp.ps1 -Method tools/list
#   pwsh Tools/Invoke-Mcp.ps1 -Session agent-rendering -Method ping
#   pwsh Tools/Invoke-Mcp.ps1 -Method tools/call -Params @{ name='list_worlds'; arguments=@{} }

$ErrorActionPreference = "Stop"

if (-not [string]::IsNullOrWhiteSpace($Session)) {
    if ($PSBoundParameters.ContainsKey('Url')) {
        throw 'Use either -Session or -Url, not both.'
    }

    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $manifestPath = Join-Path $repoRoot "Build\_AgentValidation\mcp-sessions\$Session\session.json"
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "MCP editor session '$Session' does not exist. Start it with Tools/Manage-McpEditorSession.ps1."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$manifest.endpoint)) {
        throw "MCP editor session '$Session' has no endpoint in '$manifestPath'."
    }
    $Url = [string]$manifest.endpoint
}

$body = @{
    jsonrpc = "2.0"
    id      = [guid]::NewGuid().ToString()
    method  = $Method
}
if ($PSBoundParameters.ContainsKey('Params') -and $null -ne $Params) {
    $body.params = $Params
}

$json = $body | ConvertTo-Json -Depth 12 -Compress
try {
    $resp = Invoke-RestMethod -Uri $Url -Method Post -Body $json -ContentType "application/json" -TimeoutSec $TimeoutSec
    $resp | ConvertTo-Json -Depth $OutputDepth
}
catch {
    Write-Error ("MCP request failed: " + $_.Exception.Message)
    exit 1
}
