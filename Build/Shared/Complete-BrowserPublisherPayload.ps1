param(
    [Parameter(Mandatory = $true)][string] $StageDirectory,
    [Parameter(Mandatory = $true)][string] $DestinationDirectory
)

$ErrorActionPreference = 'Stop'
$manifestName = 'browser-publisher-manifest.json'
$stage = [IO.Path]::GetFullPath($StageDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$destination = [IO.Path]::GetFullPath($DestinationDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)

function Get-RelativePath([string] $root, [string] $path) {
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "BrowserPublisher.PathEscaped: '$path' is outside '$root'."
    }
    return $path.Substring($prefix.Length).Replace('\', '/')
}

function Test-RegularTree([string] $root) {
    $directory = Get-Item -LiteralPath $root -Force
    if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "BrowserPublisher.LinkedPayload: '$root' must be a regular directory."
    }
    foreach ($entry in Get-ChildItem -LiteralPath $root -Recurse -Force) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "BrowserPublisher.LinkedPayload: '$($entry.FullName)' is linked."
        }
    }
}

function Test-ManifestPath([string] $path) {
    if ([string]::IsNullOrWhiteSpace($path) -or $path.Contains('\') -or
        $path.StartsWith('/') -or $path.Contains(':') -or
        $path -eq $manifestName -or
        @($path.Split('/') | Where-Object { $_ -eq '' -or $_ -eq '.' -or $_ -eq '..' }).Count -ne 0) {
        throw "BrowserPublisher.ManifestPathInvalid: '$path'."
    }
}

function Get-PayloadFiles([string] $root) {
    $files = New-Object 'System.Collections.Generic.List[object]'
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse -Force) {
        $relative = Get-RelativePath $root $file.FullName
        if ($relative -eq $manifestName) {
            continue
        }
        Test-ManifestPath $relative
        $files.Add([pscustomobject]@{
            path = $relative
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    return $files.ToArray()
}

function Assert-OwnedPayload([string] $root) {
    Test-RegularTree $root
    $manifestPath = [IO.Path]::Combine($root, $manifestName)
    if (-not [IO.File]::Exists($manifestPath)) {
        throw "BrowserPublisher.UnownedPayload: '$root' has no ownership manifest."
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or $null -eq $manifest.files) {
        throw "BrowserPublisher.ManifestInvalid: '$manifestPath'."
    }
    $expected = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        $path = [string] $entry.path
        $hash = [string] $entry.sha256
        Test-ManifestPath $path
        if ($hash -cnotmatch '^[0-9a-f]{64}$' -or $expected.ContainsKey($path)) {
            throw "BrowserPublisher.ManifestInvalid: duplicate path or digest '$path'."
        }
        $expected.Add($path, $hash)
    }
    $expectedDirectories = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $expected.Keys) {
        $separator = $path.IndexOf('/')
        while ($separator -ge 0) {
            [void] $expectedDirectories.Add($path.Substring(0, $separator))
            $separator = $path.IndexOf('/', $separator + 1)
        }
    }
    foreach ($directory in Get-ChildItem -LiteralPath $root -Directory -Recurse -Force) {
        $relative = Get-RelativePath $root $directory.FullName
        if (-not $expectedDirectories.Contains($relative)) {
            throw "BrowserPublisher.PayloadModified: unexpected directory '$relative'."
        }
    }
    $actual = Get-PayloadFiles $root
    if ($actual.Count -ne $expected.Count) {
        throw "BrowserPublisher.PayloadModified: file count differs from its ownership manifest."
    }
    foreach ($entry in $actual) {
        if (-not $expected.ContainsKey($entry.path) -or $expected[$entry.path] -cne $entry.sha256) {
            throw "BrowserPublisher.PayloadModified: '$($entry.path)' differs from its ownership manifest."
        }
    }
}

if (-not [IO.Directory]::Exists($stage)) {
    throw "BrowserPublisher.StageMissing: '$stage'."
}
if ([string]::Equals($stage, $destination, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BrowserPublisher.StageInvalid: stage and destination must differ.'
}
if (-not [string]::Equals([IO.Path]::GetDirectoryName($stage), [IO.Path]::GetDirectoryName($destination), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BrowserPublisher.StageInvalid: stage and destination must be siblings.'
}

Test-RegularTree $stage
$files = Get-PayloadFiles $stage
if ($files.Count -eq 0) {
    throw 'BrowserPublisher.PayloadEmpty: no files were staged.'
}
$paths = [string[]] @($files | ForEach-Object { $_.path })
[Array]::Sort($paths, [StringComparer]::Ordinal)
$byPath = New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $files) {
    if ($byPath.ContainsKey($entry.path)) {
        throw "BrowserPublisher.PathCollision: '$($entry.path)'."
    }
    $byPath.Add($entry.path, $entry.sha256)
}
$orderedFiles = New-Object 'System.Collections.Generic.List[object]'
foreach ($path in $paths) {
    $orderedFiles.Add([ordered]@{ path = $path; sha256 = $byPath[$path] })
}
$manifest = [ordered]@{ schema = 1; files = $orderedFiles.ToArray() }
$json = $manifest | ConvertTo-Json -Compress -Depth 4
[IO.File]::WriteAllText([IO.Path]::Combine($stage, $manifestName), $json, (New-Object Text.UTF8Encoding($false)))
Assert-OwnedPayload $stage

$backup = $destination + '.browser-publisher-backup-' + [Guid]::NewGuid().ToString('N')
$movedPrevious = $false
if ([IO.Directory]::Exists($destination)) {
    Assert-OwnedPayload $destination
    [IO.Directory]::Move($destination, $backup)
    $movedPrevious = $true
} elseif ([IO.File]::Exists($destination)) {
    throw "BrowserPublisher.DestinationInvalid: '$destination' is a file."
}

try {
    [IO.Directory]::Move($stage, $destination)
} catch {
    if ($movedPrevious -and -not [IO.Directory]::Exists($destination)) {
        [IO.Directory]::Move($backup, $destination)
    }
    throw
}
if ($movedPrevious) {
    try {
        Remove-Item -LiteralPath $backup -Recurse -Force
    } catch {
        Write-Warning "BrowserPublisher.PreviousPayloadRemains: '$backup' could not be removed: $_"
    }
}
