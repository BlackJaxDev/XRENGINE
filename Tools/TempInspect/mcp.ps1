# Helper to call the XREngine MCP HTTP JSON-RPC server.
# Usage:
#   .\mcp.ps1 -Method tools/list
#   .\mcp.ps1 -Method tools/call -Params @{ name='list_worlds'; arguments=@{} }
param(
    [Parameter(Mandatory = $true)][string]$Method,
    [hashtable]$Params,
    [string]$Url = "http://localhost:5467/mcp/",
    [int]$TimeoutSec = 60
)

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
    $resp | ConvertTo-Json -Depth 20
}
catch {
    Write-Output ("ERROR: " + $_.Exception.Message)
}
