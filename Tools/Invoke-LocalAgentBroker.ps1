[CmdletBinding()]
param(
    [string]$ApiKeyEnvironmentVariable = "",
    [string]$EditorAuthTokenEnvironmentVariable = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$brokerDll = Join-Path $repoRoot "Build\AgentTools\LocalAgentBroker\XREngine.LocalAgentBroker.dll"
$resolvedApiKeyEnvironmentVariable = if ([string]::IsNullOrWhiteSpace($ApiKeyEnvironmentVariable)) {
    if ([string]::IsNullOrWhiteSpace($env:XRE_LOCAL_AGENT_BROKER_API_KEY_ENV)) {
        "OPENAI_API_KEY"
    }
    else {
        $env:XRE_LOCAL_AGENT_BROKER_API_KEY_ENV
    }
}
else {
    $ApiKeyEnvironmentVariable
}
$resolvedEditorAuthEnvironmentVariable =
    if ([string]::IsNullOrWhiteSpace($EditorAuthTokenEnvironmentVariable)) {
        $env:XRE_LOCAL_AGENT_BROKER_EDITOR_AUTH_ENV
    }
    else {
        $EditorAuthTokenEnvironmentVariable
    }

if (-not (Test-Path -LiteralPath $brokerDll -PathType Leaf)) {
    [Console]::Error.WriteLine(
        "Local agent broker is not built. Run Tools\Setup-LocalAgentBroker.ps1 from the repository root.")
    exit 1
}

$brokerArguments = @(
    $brokerDll
    "--repo-root"
    $repoRoot
    "--api-key-env"
    $resolvedApiKeyEnvironmentVariable
)
if (-not [string]::IsNullOrWhiteSpace($resolvedEditorAuthEnvironmentVariable)) {
    $brokerArguments += @(
        "--editor-auth-env"
        $resolvedEditorAuthEnvironmentVariable
    )
}

& dotnet @brokerArguments
exit $LASTEXITCODE
