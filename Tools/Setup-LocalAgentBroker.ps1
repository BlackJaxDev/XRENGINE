[CmdletBinding()]
param(
    [switch]$SkipSmokeTest
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$projectPath = Join-Path $repoRoot "Tools\LocalAgentBroker\LocalAgentBroker.csproj"
$agentToolsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "Build\AgentTools"))
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $agentToolsRoot "LocalAgentBroker"))

if (Test-Path -LiteralPath $outputPath) {
    $requiredPrefix = $agentToolsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $outputPath.StartsWith(
            $requiredPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean broker output outside Build\AgentTools."
    }
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}

Write-Host "Publishing the local agent broker..."
dotnet publish $projectPath `
    --configuration Release `
    --output $outputPath `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Local agent broker publish failed with exit code $LASTEXITCODE."
}

if (-not $SkipSmokeTest) {
    & (Join-Path $PSScriptRoot "Test-LocalAgentBrokerMcp.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "Local agent broker MCP smoke test failed with exit code $LASTEXITCODE."
    }
}

Write-Host "Local agent broker is ready at $outputPath"
