[CmdletBinding()]
param([Parameter(Mandatory)] [string] $RepositoryRoot, [Parameter(Mandatory)] [string] $Output)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The Windows PowerShell compiler prerequisite must run on Windows.' }
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$directory = Join-Path $root 'Tools/BrowserSmoke/network-walkthrough'
$wrapper = Join-Path $directory 'Invoke-NetworkWalkthrough.ps1'
$parseTokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($wrapper, [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'The walkthrough wrapper did not pass PowerShell AST parsing.' }
# Inspect literal AST values only. Never dot-source/invoke the wrapper, execute
# extracted PowerShell, construct the compiled class, or call any native method.
$compilations = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Add-Type'
}, $true))
if ($compilations.Count -ne 1) { throw 'Expected exactly one literal owned-job compilation.' }
$literals = @($compilations[0].CommandElements | Where-Object {
    $_ -is [Management.Automation.Language.StringConstantExpressionAst] -and $_.Value.Contains('public sealed class WalkthroughOwnedJob')
})
if ($literals.Count -ne 1) { throw 'The owned-job C# source must be one literal string.' }
$cleanupLiterals = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
    $node.Value.Contains('New-SelfSignedCertificate') -and $node.Value.Contains('$mode = $args[0]')
}, $true))
if ($cleanupLiterals.Count -ne 1) { throw 'Expected exactly one literal certificate/cleanup helper.' }
$null = [Management.Automation.Language.Parser]::ParseInput($cleanupLiterals[0].Value, [ref]$parseTokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'The generated helper did not pass PowerShell AST parsing.' }
Add-Type -TypeDefinition $literals[0].Value -ErrorAction Stop
foreach ($file in @('admit-network-walkthrough.mjs', 'run-real-network-walkthrough.mjs')) {
    & node --check (Join-Path $directory $file)
    if ($LASTEXITCODE -ne 0) { throw 'JavaScript syntax verification failed.' }
}
$paths = @('.github/workflows/browser-network-walkthrough-once.yml') + @(
    'Invoke-NetworkWalkthrough.ps1', 'run-real-network-walkthrough.mjs', 'admit-network-walkthrough.mjs',
    'Test-NetworkWalkthroughStatic.ps1', 'producer.json', 'README.md'
) | ForEach-Object { if ($_.StartsWith('.github/')) { $_ } else { 'Tools/BrowserSmoke/network-walkthrough/' + $_ } }
$hashes = [ordered]@{}
foreach ($relative in $paths) { $hashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $root $relative) -Algorithm SHA256).Hash.ToLowerInvariant() }
$result = [ordered]@{ result = 'passed'; powershellParsed = $true; embeddedPowerShellParsed = $true;
    ownedJobCompiled = $true; certificateMutationReached = $false; serviceStarted = $false; browserStarted = $false;
    files = $hashes; powershellVersion = $PSVersionTable.PSVersion.ToString() }
New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force | Out-Null
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Output -Encoding utf8NoBOM
