[CmdletBinding()]
param(
    [switch]$LibraryOnly,
    [switch]$SkipSmokeTest,
    [string]$PackagePath,
    [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Dependencies/LocalAgentBroker.lock.json') -Raw | ConvertFrom-Json
if ($pin.version -notmatch '^\d+\.\d+\.\d+$' -or
    $pin.packageSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
    $pin.librarySha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
    $pin.archiveSha256 -notmatch '^[A-Fa-f0-9]{64}$') {
    throw 'The broker release pin is invalid.'
}
$dependencyRoot = Join-Path $repoRoot 'Build/Dependencies/LocalAgentBroker'
$libraryRoot = Join-Path $dependencyRoot $pin.version
$libraryPath = Join-Path $libraryRoot 'lib/net10.0/LocalAgentBroker.Orchestration.dll'
$deploymentRoot = Join-Path $repoRoot 'Build/_AgentValidation/00000000-000000-shared/agent-tools'
$pointerPath = Join-Path $deploymentRoot 'LocalAgentBroker.current'
$releaseUrl = "https://github.com/BlackJaxDev/local-agent-broker/releases/download/v$($pin.version)"

function Test-FileHash([string]$Path, [string]$Expected) {
    return (Test-Path -LiteralPath $Path -PathType Leaf) -and
        ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Expected)
}

function Get-ReleaseAsset([string]$Name, [string]$ExpectedHash, [string]$LocalPath) {
    if (-not [string]::IsNullOrWhiteSpace($LocalPath)) {
        $resolved = [IO.Path]::GetFullPath($LocalPath)
        if (-not (Test-FileHash $resolved $ExpectedHash)) {
            throw "The supplied release asset failed SHA-256 verification: $Name"
        }
        return $resolved
    }
    $cache = Join-Path $dependencyRoot "downloads/$($pin.version)"
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    $asset = Join-Path $cache $Name
    if (-not (Test-FileHash $asset $ExpectedHash)) {
        $partial = "$asset.download-$PID"
        try {
            Invoke-WebRequest -UseBasicParsing -Uri "$releaseUrl/$Name" -OutFile $partial
            if (-not (Test-FileHash $partial $ExpectedHash)) {
                throw "The downloaded release asset failed SHA-256 verification: $Name"
            }
            Move-Item -LiteralPath $partial -Destination $asset -Force
        }
        finally {
            if (Test-Path -LiteralPath $partial) {
                Remove-Item -LiteralPath $partial -Force
            }
        }
    }
    return $asset
}

function Expand-VerifiedArchive([string]$Archive, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $prefix = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        foreach ($entry in $zip.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName))
            if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'A release archive entry is outside the install directory.'
            }
            if ([string]::IsNullOrEmpty($entry.Name)) {
                [IO.Directory]::CreateDirectory($target) | Out-Null
                continue
            }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
        }
    }
    finally {
        $zip.Dispose()
    }
}

$hasher = [Security.Cryptography.SHA256]::Create()
try {
    $rootHash = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($repoRoot.ToUpperInvariant()))).Replace('-', '')
}
finally {
    $hasher.Dispose()
}
$mutex = [Threading.Mutex]::new($false, "Local\XREngine-BrokerInstall-$rootHash")
$locked = $false
try {
    try { $locked = $mutex.WaitOne(300000) }
    catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { throw 'Timed out waiting for the broker installer.' }

    if (-not (Test-FileHash $libraryPath $pin.librarySha256)) {
        if (Test-Path -LiteralPath $libraryRoot) {
            throw 'The installed orchestration package is incomplete or changed. Remove its version directory and run setup again.'
        }
        $package = Get-ReleaseAsset $pin.package $pin.packageSha256 $PackagePath
        $stage = Join-Path $dependencyRoot "$($pin.version)-stage-$([Guid]::NewGuid().ToString('N'))"
        Expand-VerifiedArchive $package $stage
        if (-not (Test-FileHash (Join-Path $stage 'lib/net10.0/LocalAgentBroker.Orchestration.dll') $pin.librarySha256)) {
            throw 'The extracted orchestration library failed SHA-256 verification.'
        }
        Move-Item -LiteralPath $stage -Destination $libraryRoot
    }
    if ($LibraryOnly) { return }

    $archive = Get-ReleaseAsset $pin.archive $pin.archiveSha256 $ArchivePath
    $deploymentName = "LocalAgentBroker-$(Get-Date -Format 'yyyyMMddHHmmssfff')"
    $deploymentPath = Join-Path $deploymentRoot $deploymentName
    Expand-VerifiedArchive $archive $deploymentPath
    if (-not (Test-Path -LiteralPath (Join-Path $deploymentPath 'LocalAgentBroker.dll'))) {
        throw 'The broker release does not contain its executable assembly.'
    }
    if (-not $SkipSmokeTest) {
        & (Join-Path $PSScriptRoot 'Test-LocalAgentBrokerMcp.ps1') -BrokerPath (Join-Path $deploymentPath 'LocalAgentBroker.dll')
    }
    # Publish the pointer only after the new deployment passes its smoke check.
    $temporaryPointer = "$pointerPath.tmp-$PID"
    [IO.File]::WriteAllText($temporaryPointer, $deploymentName, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPointer -Destination $pointerPath -Force
    Write-Host "Local Agent Broker $($pin.version) is installed. Restart existing MCP connections to use it."
}
finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
