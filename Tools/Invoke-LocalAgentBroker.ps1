[CmdletBinding()]
param(
    [string]$BrokerPath = "",
    [string]$ConfigPath = "",
    [string]$StateRoot = "",
    [string]$SessionsRoot = "",
    [string]$ApiKeyEnvironmentVariable = "",
    [string]$AnthropicApiKeyEnvironmentVariable = "",
    [string]$EditorAuthTokenEnvironmentVariable = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$agentToolsRoot = Join-Path $repoRoot "Build\_AgentValidation\00000000-000000-shared\agent-tools"
$deploymentPointerPath = Join-Path $agentToolsRoot "LocalAgentBroker.current"
$brokerDll = Join-Path $agentToolsRoot "LocalAgentBroker\LocalAgentBroker.dll"

if (-not [string]::IsNullOrWhiteSpace($BrokerPath)) {
    $brokerDll = [System.IO.Path]::GetFullPath($BrokerPath)
}
elseif (Test-Path -LiteralPath $deploymentPointerPath -PathType Leaf) {
    $deploymentName = ([System.IO.File]::ReadAllText($deploymentPointerPath)).Trim()
    if ($deploymentName -notmatch '^LocalAgentBroker-[0-9]{17}$') {
        throw "The local agent broker deployment pointer is invalid."
    }

    $brokerDll = Join-Path $agentToolsRoot "$deploymentName\LocalAgentBroker.dll"
}

if (-not (Test-Path -LiteralPath $brokerDll -PathType Leaf)) {
    throw "Local agent broker is not installed. Run Tools\Setup-LocalAgentBroker.ps1 from the repository root."
}

$brokerVersion = [System.Reflection.AssemblyName]::GetAssemblyName($brokerDll).Version
if ($brokerVersion -lt [version]'0.2.0') {
    throw "Local agent broker $brokerVersion does not support the generic config. Run Tools\Setup-LocalAgentBroker.ps1 from the repository root."
}

# Keep old XREngine settings active without overriding generic broker settings.
$settingSuffixes = @(
    'CONFIG',
    'REPO_ROOT',
    'STATE_ROOT',
    'SESSIONS_ROOT',
    'API_KEY_ENV',
    'ANTHROPIC_API_KEY_ENV',
    'EDITOR_AUTH_ENV',
    'MAX_CONCURRENCY',
    'MAX_RUNS',
    'RETENTION_MINUTES',
    'TRACE',
    'LAUNCH_TRAY'
)
foreach ($suffix in $settingSuffixes) {
    $genericName = "LOCAL_AGENT_BROKER_$suffix"
    $legacyName = "XRE_LOCAL_AGENT_BROKER_$suffix"
    if ($null -ne [System.Environment]::GetEnvironmentVariable($genericName)) {
        continue
    }

    $legacyValue = [System.Environment]::GetEnvironmentVariable($legacyName)
    if ($null -ne $legacyValue) {
        [System.Environment]::SetEnvironmentVariable($genericName, $legacyValue)
    }
}

$resolvedConfigPath = if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath
}
elseif (-not [string]::IsNullOrWhiteSpace($env:LOCAL_AGENT_BROKER_CONFIG)) {
    $env:LOCAL_AGENT_BROKER_CONFIG
}
else {
    Join-Path $PSScriptRoot 'Dependencies/LocalAgentBroker.config.json'
}

if (-not (Test-Path -LiteralPath $resolvedConfigPath -PathType Leaf)) {
    throw "Local agent broker config was not found: $resolvedConfigPath"
}

$brokerArguments = @(
    $brokerDll
    '--config'
    ([System.IO.Path]::GetFullPath($resolvedConfigPath))
    '--repo-root'
    $repoRoot
)
if (-not [string]::IsNullOrWhiteSpace($StateRoot)) {
    $brokerArguments += @('--state-root', $StateRoot)
}
if (-not [string]::IsNullOrWhiteSpace($SessionsRoot)) {
    $brokerArguments += @('--sessions-root', $SessionsRoot)
}
if (-not [string]::IsNullOrWhiteSpace($ApiKeyEnvironmentVariable)) {
    $brokerArguments += @('--api-key-env', $ApiKeyEnvironmentVariable)
}
if (-not [string]::IsNullOrWhiteSpace($AnthropicApiKeyEnvironmentVariable)) {
    $brokerArguments += @('--anthropic-api-key-env', $AnthropicApiKeyEnvironmentVariable)
}
if (-not [string]::IsNullOrWhiteSpace($EditorAuthTokenEnvironmentVariable)) {
    $brokerArguments += @('--editor-auth-env', $EditorAuthTokenEnvironmentVariable)
}

& dotnet @brokerArguments
exit $LASTEXITCODE
