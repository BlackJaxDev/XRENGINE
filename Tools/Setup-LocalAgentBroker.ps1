[CmdletBinding()]
param(
    [switch]$SkipSmokeTest
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repoRoot "Tools\LocalAgentBroker\LocalAgentBroker.csproj"
$agentToolsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "Build\_AgentValidation\00000000-000000-shared\agent-tools"))
$deploymentName = "LocalAgentBroker-$(Get-Date -Format 'yyyyMMddHHmmssfff')"
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $agentToolsRoot $deploymentName))
$pointerPath = Join-Path $agentToolsRoot "LocalAgentBroker.current"

$requiredPrefix = $agentToolsRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith(
        $requiredPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to publish broker output outside Build\_AgentValidation\00000000-000000-shared\agent-tools."
}

Write-Host "Publishing the local agent broker..."
dotnet publish $projectPath `
    --configuration Release `
    --output $outputPath `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Local agent broker publish failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Force $agentToolsRoot | Out-Null
$pointerTemporaryPath = "$pointerPath.tmp-$PID"
[System.IO.File]::WriteAllText(
    $pointerTemporaryPath,
    $deploymentName,
    [System.Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $pointerTemporaryPath -Destination $pointerPath -Force

if (-not $SkipSmokeTest) {
    & (Join-Path $PSScriptRoot "Test-LocalAgentBrokerMcp.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "Local agent broker MCP smoke test failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Local agent broker is ready at $outputPath"
Write-Host (
    "Restart any Codex task that was already running before this publish. " +
    "An existing stdio MCP transport remains attached to its old broker process " +
    "and cannot hot-rebind to the new deployment pointer.")

$staleDeployments = @(
    Get-ChildItem -LiteralPath $agentToolsRoot -Directory -Filter "LocalAgentBroker-*" |
        Where-Object { $_.Name -ne $deploymentName } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -Skip 1
)
foreach ($staleDeployment in $staleDeployments) {
    $stalePath = [System.IO.Path]::GetFullPath($staleDeployment.FullName)
    if (-not $stalePath.StartsWith(
            $requiredPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        continue
    }
    try {
        Remove-Item -LiteralPath $stalePath -Recurse -Force -ErrorAction Stop
    }
    catch [System.UnauthorizedAccessException], [System.IO.IOException] {
        Write-Warning "Could not remove in-use stale broker deployment '$stalePath'."
    }
}
