[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Session,
    [ValidateRange(1, 600)][int]$Seconds = 20,
    [ValidateRange(50, 5000)][int]$IntervalMilliseconds = 250,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

# Samples the GPU time of the latest completed Vulkan frame command buffer
# (get_render_profiler_stats vulkan.frame_lifecycle.gpu_command_buffer_timing) and the GPU clocks
# (nvidia-smi) during a timed window, and prints the mean, median, and p95. Each completed frame is
# counted once, by its sequence number. Lock the GPU clocks first (nvidia-smi -lgc, elevated): a
# CPU-limited frame lets the driver lower the clocks, and the GPU time then grows.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$view = & (Join-Path $repoRoot 'Tools\Manage-McpEditorSession.ps1') Status -Name $Session -AsJson | ConvertFrom-Json
$endpoint = [string]$view.endpoint
if ([string]::IsNullOrWhiteSpace($endpoint)) {
    throw "Session '$Session' has no MCP endpoint (state: $($view.state))."
}
$body = @{ jsonrpc = '2.0'; id = '1'; method = 'tools/call'; params = @{ name = 'get_render_profiler_stats'; arguments = @{} } } |
    ConvertTo-Json -Depth 5 -Compress

$seen = @{}
$rows = New-Object System.Collections.Generic.List[object]
$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
while ([DateTime]::UtcNow -lt $deadline) {
    try {
        $response = Invoke-RestMethod -Uri $endpoint -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 10
        $last = $response.result.structuredContent.vulkan.frame_lifecycle.gpu_command_buffer_timing.last_completed
        $clocks = (& nvidia-smi --query-gpu=clocks.gr,pstate,temperature.gpu --format=csv,noheader,nounits) -split ',\s*'
        if ($null -ne $last -and -not $seen.ContainsKey([string]$last.sequence)) {
            $seen[[string]$last.sequence] = $true
            $rows.Add([pscustomobject]@{
                sequence = [long]$last.sequence
                gpuMs = [double]$last.elapsed_nanoseconds / 1e6
                graphicsMHz = [int]$clocks[0]
                pstate = $clocks[1]
                temperatureC = [int]$clocks[2]
            })
        }
    }
    catch {
        Write-Warning $_.Exception.Message
    }
    Start-Sleep -Milliseconds $IntervalMilliseconds
}

$rows | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
if ($rows.Count -eq 0) {
    'no samples'
    return
}

$ms = @($rows | ForEach-Object gpuMs | Sort-Object)
$mhz = @($rows | ForEach-Object graphicsMHz | Sort-Object)
'samples {0}; gpu ms mean {1:N2} median {2:N2} p95 {3:N2}; graphics MHz min {4} median {5} max {6}; pstates {7}; max temp {8} C' -f `
    $ms.Count, ($ms | Measure-Object -Average).Average, $ms[[int]($ms.Count / 2)],
    $ms[[Math]::Min($ms.Count - 1, [int]($ms.Count * 0.95))],
    $mhz[0], $mhz[[int]($mhz.Count / 2)], $mhz[-1],
    (($rows | ForEach-Object pstate | Sort-Object -Unique) -join ','),
    ($rows | ForEach-Object temperatureC | Measure-Object -Maximum).Maximum
