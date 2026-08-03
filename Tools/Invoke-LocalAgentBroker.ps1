[CmdletBinding()]
param(
    [string]$ApiKeyEnvironmentVariable = "",
    [string]$EditorAuthTokenEnvironmentVariable = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$agentToolsRoot = Join-Path $repoRoot "Build\AgentTools"
$deploymentPointerPath = Join-Path $agentToolsRoot "LocalAgentBroker.current"
$brokerDll = Join-Path $agentToolsRoot "LocalAgentBroker\XREngine.LocalAgentBroker.dll"
if (Test-Path -LiteralPath $deploymentPointerPath -PathType Leaf) {
    $deploymentName = ([System.IO.File]::ReadAllText($deploymentPointerPath)).Trim()
    if ($deploymentName -notmatch '^LocalAgentBroker-[0-9]{17}$') {
        throw "The local agent broker deployment pointer is invalid."
    }
    $brokerDll = Join-Path $agentToolsRoot "$deploymentName\XREngine.LocalAgentBroker.dll"
}
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
$inheritedApiKey = [System.Environment]::GetEnvironmentVariable(
    $resolvedApiKeyEnvironmentVariable,
    [System.EnvironmentVariableTarget]::Process)
$isWindowsHost = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
if ([string]::IsNullOrWhiteSpace($inheritedApiKey) -and $isWindowsHost) {
    $userApiKey = [System.Environment]::GetEnvironmentVariable(
        $resolvedApiKeyEnvironmentVariable,
        [System.EnvironmentVariableTarget]::User)
    if (-not [string]::IsNullOrWhiteSpace($userApiKey)) {
        [System.Environment]::SetEnvironmentVariable(
            $resolvedApiKeyEnvironmentVariable,
            $userApiKey,
            [System.EnvironmentVariableTarget]::Process)
    }
    $userApiKey = $null
}
$inheritedApiKey = $null
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
