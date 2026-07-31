[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$brokerDll = Join-Path $repoRoot "Build\AgentTools\LocalAgentBroker\XREngine.LocalAgentBroker.dll"
$launcherPath = Join-Path $PSScriptRoot "Invoke-LocalAgentBroker.ps1"
if (-not (Test-Path -LiteralPath $brokerDll -PathType Leaf)) {
    throw "Broker output is missing. Run Tools\Setup-LocalAgentBroker.ps1 first."
}

try {
    $requests = @(
        '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"xrengine-smoke","version":"1.0"}}}'
        '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "powershell"
    $startInfo.Arguments =
        "-NoProfile -ExecutionPolicy Bypass -File `"$launcherPath`""
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "Unable to start the local agent broker process."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $requestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(
        ($requests -join "`n") + "`n")
    $process.StandardInput.BaseStream.Write($requestBytes, 0, $requestBytes.Length)
    $process.StandardInput.BaseStream.Flush()
    $process.StandardInput.BaseStream.Close()

    if (-not $process.WaitForExit(15000)) {
        $process.Kill($true)
        throw "Broker did not finish the initialize/list-tools smoke test within 15 seconds."
    }

    if ($process.ExitCode -ne 0) {
        $stderr = $stderrTask.GetAwaiter().GetResult()
        throw "Broker exited with code $($process.ExitCode). $stderr"
    }

    $responseLines = @(
        $stdoutTask.GetAwaiter().GetResult() -split "\r?\n" |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($responseLines.Count -ne 2) {
        throw "Expected exactly two JSON-RPC response lines and no stdout banners; received $($responseLines.Count)."
    }

    $initialize = $responseLines[0] | ConvertFrom-Json
    $toolList = $responseLines[1] | ConvertFrom-Json
    if ($initialize.id -ne 1 -or $initialize.result.serverInfo.name -ne "XREngine.LocalAgentBroker") {
        throw "The broker initialize response did not match the expected server identity: $($responseLines[0])"
    }

    $expectedTools = @(
        "recommend_agent_route"
        "start_agent_run"
        "get_agent_run"
        "cancel_agent_run"
        "list_agent_runs"
    )
    $actualTools = @($toolList.result.tools | ForEach-Object { $_.name })
    foreach ($expectedTool in $expectedTools) {
        if ($actualTools -notcontains $expectedTool) {
            throw "The broker did not advertise required tool '$expectedTool'."
        }
    }

    Write-Host "Local agent broker MCP initialize/list-tools smoke test passed."
}
finally {
    if ($null -ne $process) {
        $process.Dispose()
    }
}
