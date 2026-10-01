[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,
    [string]$SourceDirectory,
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'JoltBrowser.lock.json') -Raw | ConvertFrom-Json
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$validationRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'Build/_AgentValidation'))
$dependencyRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'Build/Dependencies/JoltBrowser'))
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not $outputRoot.StartsWith($validationRoot + [IO.Path]::DirectorySeparatorChar, $pathComparison) -and
    -not $outputRoot.StartsWith($dependencyRoot + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
    throw 'Browser managed-source staging must be inside Build/Dependencies/JoltBrowser or a reserved Build/_AgentValidation task run.'
}

if ($PlanOnly) {
    [pscustomobject]@{
        Repository = $pin.managedRepository
        Commit = $pin.managedCommit
        Version = $pin.managedVersion
        License = $pin.license
        Patch = $pin.managedPatch
        DesktopSupply = $pin.desktopSupply
    }
    return
}

function Invoke-CheckedGit {
    param([string[]]$Arguments)
    & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Managed source Git command failed with exit code $LASTEXITCODE." }
}

function Assert-FileHash {
    param([string]$Path, [string]$Expected)
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected) {
        throw "The pinned managed source or patch has changed: $([IO.Path]::GetFileName($Path))."
    }
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = Join-Path $outputRoot 'sources/JoltPhysicsSharp'
    if (-not (Test-Path -LiteralPath $SourceDirectory)) {
        Invoke-CheckedGit @('clone', '--no-checkout', '--filter=blob:none', $pin.managedRepository, $SourceDirectory)
        Invoke-CheckedGit @('-C', $SourceDirectory, 'checkout', '--detach', $pin.managedCommit)
    }
}
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
$actualCommit = (& git -C $SourceDirectory rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $pin.managedCommit) {
    throw 'Managed source checkout does not match the committed browser pin.'
}
$sourceChanges = @(& git -C $SourceDirectory status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $sourceChanges.Count -ne 0) {
    throw 'Pinned managed source must be pristine; only the staged copy receives the reviewed patch.'
}

$licensePath = Join-Path $SourceDirectory 'LICENSE'
$apiRelativePath = 'src/JoltPhysicsSharp/JoltApi.cs'
Assert-FileHash $licensePath $pin.managedLicenseSha256
Assert-FileHash (Join-Path $SourceDirectory $apiRelativePath) $pin.managedApiSha256
$lifetimePatch = Join-Path $PSScriptRoot 'JoltBrowser/managed-lifetime.patch'
Assert-FileHash $lifetimePatch $pin.managedLifetimePatchSha256
$callbackAbiPatch = Join-Path $PSScriptRoot 'JoltBrowser/managed-callback-abi.patch'
Assert-FileHash $callbackAbiPatch $pin.managedCallbackAbiPatchSha256
foreach ($sourcePin in $pin.managedLifetimeSources) {
    Assert-FileHash (Join-Path $SourceDirectory $sourcePin.path) $sourcePin.originalSha256
}

$stagedRoot = Join-Path $outputRoot 'staged'
New-Item -ItemType Directory -Path $stagedRoot -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $SourceDirectory 'src/JoltPhysicsSharp') -Filter '*.cs' -Recurse -File)
if ($sources.Count -eq 0) { throw 'The pinned managed binding has no source files.' }
foreach ($source in $sources) {
    $relativePath = [IO.Path]::GetRelativePath($SourceDirectory, $source.FullName)
    $destination = Join-Path $stagedRoot $relativePath
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source.FullName -Destination $destination
}
Copy-Item -LiteralPath $licensePath -Destination (Join-Path $stagedRoot 'LICENSE')

$stagedApi = Join-Path $stagedRoot $apiRelativePath
$api = [IO.File]::ReadAllText($stagedApi)
$incorrectSignature = 'public static partial nint JPH_ContactListener_SetProcs(in JPH_ContactListener_ProcsDouble procs);'
$correctSignature = 'public static partial void JPH_ContactListener_SetProcs(in JPH_ContactListener_ProcsDouble procs);'
if ([regex]::Matches($api, [regex]::Escape($incorrectSignature)).Count -ne 1) {
    throw 'The reviewed contact-listener return-type patch does not match exactly one source declaration.'
}
[IO.File]::WriteAllText($stagedApi, $api.Replace($incorrectSignature, $correctSignature), [Text.UTF8Encoding]::new($false))
Assert-FileHash $stagedApi $pin.managedContactListenerPatchedApiSha256

# Preserve typed callbacks while passing pointer-sized tokens across the browser native ABI.
$stagedRelative = [IO.Path]::GetRelativePath($repositoryRoot, $stagedRoot).Replace('\', '/')
Invoke-CheckedGit @('-C', $repositoryRoot, 'apply', '--check', "--directory=$stagedRelative", $callbackAbiPatch)
Invoke-CheckedGit @('-C', $repositoryRoot, 'apply', "--directory=$stagedRelative", $callbackAbiPatch)
Assert-FileHash $stagedApi $pin.managedPatchedApiSha256

# Apply only the separately reviewed lifetime correction to the staged tree.
# The pristine source checkout and the desktop NuGet supply remain untouched.
Invoke-CheckedGit @('-C', $repositoryRoot, 'apply', '--check', "--directory=$stagedRelative", $lifetimePatch)
Invoke-CheckedGit @('-C', $repositoryRoot, 'apply', "--directory=$stagedRelative", $lifetimePatch)
foreach ($sourcePin in $pin.managedLifetimeSources) {
    Assert-FileHash (Join-Path $stagedRoot $sourcePin.path) $sourcePin.patchedSha256
}

$stagedSources = @(Get-ChildItem -LiteralPath (Join-Path $stagedRoot 'src/JoltPhysicsSharp') -Filter '*.cs' -Recurse -File)
if ($stagedSources.Count -ne $sources.Count) {
    throw 'The staged managed-source tree contains stale files; use a fresh reserved output directory.'
}
$manifest = [ordered]@{
    Repository = $pin.managedRepository
    Commit = $pin.managedCommit
    Version = $pin.managedVersion
    License = $pin.license
    LicenseSha256 = $pin.managedLicenseSha256
    Patch = $pin.managedPatch
    PatchedApiSha256 = $pin.managedPatchedApiSha256
    CallbackAbiPatchSha256 = $pin.managedCallbackAbiPatchSha256
    LifetimePatchSha256 = $pin.managedLifetimePatchSha256
    SourceFiles = @($stagedSources | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            Path = [IO.Path]::GetRelativePath($stagedRoot, $_.FullName).Replace('\', '/')
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stagedRoot 'managed-source-pin.json')
$sourceItems = [Collections.Generic.List[string]]::new()
$sourceItems.Add('<Project><ItemGroup>')
foreach ($sourceFile in $manifest.SourceFiles) {
    $include = [Security.SecurityElement]::Escape(('$(MSBuildThisFileDirectory)' + $sourceFile.Path))
    $sourceItems.Add("  <JoltBrowserManagedSource Include=`"$include`"><ExpectedSha256>$($sourceFile.Sha256)</ExpectedSha256></JoltBrowserManagedSource>")
}
$sourceItems.Add('</ItemGroup></Project>')
[IO.File]::WriteAllLines((Join-Path $stagedRoot 'managed-source-files.props'), $sourceItems, [Text.UTF8Encoding]::new($false))
Write-Output $stagedRoot
