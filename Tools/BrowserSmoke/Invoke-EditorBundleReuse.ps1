param(
    [Parameter(Mandatory)] [ValidateSet('Select', 'Download')] [string] $Mode,
    [string] $Kind,
    [string] $Destination
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
$script:selectionWatch = $null

$repository = 'BlackJaxDev/XRENGINE'
$branch = 'codex/webgpu-readiness-audit'
$workflowPath = '.github/workflows/portable-browser-compile.yml'
$jobName = 'Publish RollingBall with the Windows Editor CLI'
$bundleNames = @(
    'windows-editor-rollingball-bundle',
    'windows-editor-rendering-parity-bundle',
    'windows-editor-advanced-rendering-parity-bundle',
    'windows-editor-advanced-shadow-parity-bundle',
    'windows-editor-advanced-shadow-parity-off-bundle',
    'windows-editor-ui-parity-bundle',
    'windows-editor-modular-pipeline-parity-bundle',
    'windows-editor-static-meshlet-parity-bundle',
    'windows-editor-static-meshlet-parity-cpu-bundle'
)
$producerSteps = @(
    'Initialize pinned Editor project submodules',
    'Stage the pinned Editor shader compiler',
    'Install WebAssembly workload',
    'Prepare pinned browser physics sources',
    'Publish RollingBall through the Editor CLI',
    'Verify activated game bundle and unchanged canonical inputs',
    'Preserve exact Editor-published game bundle',
    'Publish RenderingParity through the Editor CLI',
    'Preserve exact Editor-published RenderingParity bundle',
    'Publish Advanced rendering through the Editor CLI',
    'Preserve exact Editor-published Advanced rendering bundle',
    'Publish Advanced shadow comparison through the Editor CLI',
    'Verify Advanced shadow startup settings through the cooked codec',
    'Bind Advanced shadow source and published payload evidence',
    'Preserve exact Editor-published Advanced shadow ON bundle',
    'Preserve exact Editor-published Advanced shadow OFF reference',
    'Publish shared UI through the Editor CLI',
    'Preserve exact Editor-published shared UI bundle',
    'Publish modular pipeline through the Editor CLI',
    'Preserve exact Editor-published modular pipeline bundle',
    'Publish static meshlet GPU and CPU worlds through the Editor CLI',
    'Preserve exact Editor-published static meshlet GPU bundle',
    'Preserve exact Editor-published static meshlet CPU reference'
)

function Require([bool] $Condition, [string] $Reason) {
    if (-not $Condition) { throw $Reason }
}
function Budget([TimeSpan] $Maximum) {
    if ($null -eq $script:selectionWatch) { return $Maximum }
    $remaining = [TimeSpan]::FromMinutes(5) - $script:selectionWatch.Elapsed
    Require ($remaining -gt [TimeSpan]::Zero) 'Editor reuse preflight exceeded five minutes.'
    if ($remaining -lt $Maximum) { return $remaining }
    return $Maximum
}
function Assert-Budget {
    [void] (Budget ([TimeSpan]::FromMinutes(5)))
}
function Id([object] $Value) {
    $text = [string] $Value
    Require ($text -match '^[1-9][0-9]{0,18}$') 'Invalid numeric identity.'
    return $text
}
function Sha([object] $Value) {
    $text = [string] $Value
    Require ($text -cmatch '^[0-9a-f]{40}$') 'Invalid source SHA.'
    return $text
}
function Run-Git([string[]] $Arguments, [int] $Limit = 16777216) {
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.WorkingDirectory = (Get-Location).ProviderPath
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void] $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $limitTime = [Threading.CancellationTokenSource]::new((Budget ([TimeSpan]::FromSeconds(45))))
    try {
        $errorTask = $process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null, 65536, $limitTime.Token)
        $output = [IO.MemoryStream]::new()
        try {
            $buffer = [byte[]]::new(65536)
            while (($count = $process.StandardOutput.BaseStream.ReadAsync($buffer, 0, $buffer.Length, $limitTime.Token).GetAwaiter().GetResult()) -gt 0) {
                Require ($output.Length + $count -le $Limit) 'Git output exceeds its byte bound.'
                $output.Write($buffer, 0, $count)
            }
            [void] $process.WaitForExitAsync($limitTime.Token).GetAwaiter().GetResult()
            [void] $errorTask.GetAwaiter().GetResult()
            return @{ code = $process.ExitCode; bytes = $output.ToArray() }
        } finally { $output.Dispose() }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.Dispose()
        $limitTime.Dispose()
    }
}
function Utf8([byte[]] $Bytes) {
    return ([Text.UTF8Encoding]::new($false, $true)).GetString($Bytes)
}
function Git-Text([string[]] $Arguments) {
    $result = Run-Git $Arguments 4096
    Require ($result.code -eq 0) 'Git source inspection failed.'
    return (Utf8 $result.bytes).Trim()
}
function Allowed-Diff([string] $Base, [string] $Head) {
    $ancestor = Run-Git @('merge-base', '--is-ancestor', $Base, $Head) 1024
    if ($ancestor.code -ne 0 -or $Base -ceq $Head) { return $false }
    $result = Run-Git @('diff', '--raw', '-z', '--no-renames', '--no-ext-diff', '--no-textconv', '--abbrev=40', '--ignore-submodules=none', $Base, $Head, '--')
    if ($result.code -ne 0 -or $result.bytes.Length -eq 0 -or $result.bytes[-1] -ne 0) { return $false }
    $fields = [Collections.Generic.List[string]]::new()
    $start = 0
    for ($i = 0; $i -lt $result.bytes.Length; $i++) {
        if ($result.bytes[$i] -ne 0) { continue }
        $count = $i - $start
        if ($count -eq 0) { return $false }
        $part = [byte[]]::new($count)
        [Array]::Copy($result.bytes, $start, $part, 0, $count)
        $fields.Add((Utf8 $part))
        $start = $i + 1
    }
    if ($start -ne $result.bytes.Length -or $fields.Count -eq 0 -or $fields.Count % 2 -ne 0) { return $false }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($i = 0; $i -lt $fields.Count; $i += 2) {
        if ($fields[$i] -cnotmatch '^:100644 100644 [0-9a-f]{40} [0-9a-f]{40} M$') { return $false }
        $path = $fields[$i + 1]
        if (-not $seen.Add($path)) { return $false }
        if ($path -match '[\\:*?"<>|\x00-\x1f\x7f]') { return $false }
        foreach ($part in $path.Split('/')) {
            if (-not $part -or $part -in @('.', '..') -or $part.StartsWith(' ') -or
                $part.EndsWith(' ') -or $part.EndsWith('.')) { return $false }
        }
        if ($path -cnotin @('Tools/BrowserSmoke/advanced-shadow-parity-game.mjs',
            'Tools/BrowserSmoke/shadow-image-math.mjs', 'Tools/BrowserSmoke/shadow-image-worker.mjs') -and
            $path -cnotmatch '^docs/work/(?:[^/\x00-\x1f.][^/\x00-\x1f]*/)*[^/\x00-\x1f.][^/\x00-\x1f]*\.md$') { return $false }
    }
    return $true
}
function Get-Api([string] $Path) {
    Require ($Path -match '^repos/BlackJaxDev/XRENGINE(?:/[A-Za-z0-9_./?=&%-]+)?$') 'Invalid API path.'
    $limitTime = [Threading.CancellationTokenSource]::new((Budget ([TimeSpan]::FromSeconds(60))))
    try {
        $response = $api.GetAsync("https://api.github.com/$Path", [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $limitTime.Token).GetAwaiter().GetResult()
        try {
            Require $response.IsSuccessStatusCode 'GitHub evidence is unavailable.'
            $stream = $response.Content.ReadAsStreamAsync($limitTime.Token).GetAwaiter().GetResult()
            $memory = [IO.MemoryStream]::new()
            try {
                $buffer = [byte[]]::new(65536)
                while (($count = $stream.ReadAsync($buffer, 0, $buffer.Length, $limitTime.Token).GetAwaiter().GetResult()) -gt 0) {
                    Require ($memory.Length + $count -le 4194304) 'GitHub evidence exceeds its byte bound.'
                    $memory.Write($buffer, 0, $count)
                }
                return (Utf8 $memory.ToArray()) | ConvertFrom-Json -AsHashtable
            } finally { $memory.Dispose(); $stream.Dispose() }
        } finally { $response.Dispose() }
    } finally { $limitTime.Dispose() }
}
function Safe-Time([object] $Value) {
    $time = [DateTimeOffset]::MinValue
    Require ([DateTimeOffset]::TryParse([string] $Value, [ref] $time)) 'Invalid GitHub timestamp.'
    return $time
}
function Get-Producer([object] $Run, [string] $RepositoryId) {
    $runId = Id $Run.id
    $jobs = Get-Api "repos/BlackJaxDev/XRENGINE/actions/runs/$runId/attempts/1/jobs?per_page=100"
    if ($jobs.total_count -gt 100 -or @($jobs.jobs).Count -ne $jobs.total_count) { return $null }
    $matches = @($jobs.jobs | Where-Object { $_.name -ceq $jobName })
    if ($matches.Count -ne 1) { return $null }
    $job = $matches[0]
    if ((Id $job.run_id) -cne $runId -or
        $job.status -cne 'completed' -or $job.conclusion -cne 'success' -or
        (Sha $job.head_sha) -cne (Sha $Run.head_sha)) { return $null }
    $steps = @($job.steps)
    foreach ($required in $producerSteps) {
        $found = @($steps | Where-Object { $_.name -ceq $required })
        if ($found.Count -ne 1 -or $found[0].status -cne 'completed' -or $found[0].conclusion -cne 'success') { return $null }
    }
    return $job
}
function Get-Bundles([object] $Run, [object] $Job, [string] $RepositoryId) {
    $runId = Id $Run.id
    $data = Get-Api "repos/BlackJaxDev/XRENGINE/actions/runs/$runId/artifacts?per_page=100"
    if ($data.total_count -gt 100 -or @($data.artifacts).Count -ne $data.total_count) { return $null }
    $start = Safe-Time $Job.started_at
    $end = Safe-Time $Job.completed_at
    if ($end -lt $start) { return $null }
    $bundles = [ordered]@{}
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $bundleNames) {
        $found = @($data.artifacts | Where-Object { $_.name -ceq $name })
        if ($found.Count -ne 1) { return $null }
        $artifact = $found[0]
        if ($artifact.expired -ne $false -or $artifact.digest -cnotmatch '^sha256:[0-9a-f]{64}$' -or
            [long] $artifact.size_in_bytes -lt 1 -or [long] $artifact.size_in_bytes -gt 268435456 -or
            (Id $artifact.workflow_run.id) -cne $runId -or
            (Id $artifact.workflow_run.repository_id) -cne $RepositoryId -or
            (Id $artifact.workflow_run.head_repository_id) -cne $RepositoryId -or
            (Sha $artifact.workflow_run.head_sha) -cne (Sha $Run.head_sha) -or
            $artifact.workflow_run.head_branch -cne $branch) { return $null }
        $created = Safe-Time $artifact.created_at
        $updated = Safe-Time $artifact.updated_at
        $expires = Safe-Time $artifact.expires_at
        if ($created -lt $start -or $updated -lt $created -or $updated -gt $end -or
            $expires -le [DateTimeOffset]::UtcNow -or -not $ids.Add((Id $artifact.id))) { return $null }
        $bundles[$name] = [ordered]@{ id = (Id $artifact.id); sha256 = $artifact.digest.Substring(7);
            size = [long] $artifact.size_in_bytes; createdAt = $artifact.created_at; updatedAt = $artifact.updated_at }
    }
    return $bundles
}
function Assert-NoReparse([string] $Path) {
    $part = [IO.Path]::GetFullPath($Path)
    while ($part) {
        if ([IO.Directory]::Exists($part) -or [IO.File]::Exists($part)) {
            $attributes = [IO.File]::GetAttributes($part)
            Require (($attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Archive destination has a reparse ancestor.'
        }
        $parent = [IO.Path]::GetDirectoryName($part)
        if (-not $parent -or $parent -ceq $part) { break }
        $part = $parent
    }
}
function Get-Archive([object] $Artifact, [string] $ArchivePath) {
    $id = Id $Artifact.id
    Require (-not (Test-Path -LiteralPath $ArchivePath)) 'Archive output must start empty.'
    $redirectTime = [Threading.CancellationTokenSource]::new((Budget ([TimeSpan]::FromSeconds(60))))
    try {
        $response = $api.GetAsync("https://api.github.com/repos/BlackJaxDev/XRENGINE/actions/artifacts/$id/zip", [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $redirectTime.Token).GetAwaiter().GetResult()
        try {
            Require ([int] $response.StatusCode -eq 302) 'The artifact download redirect is unavailable.'
            $location = $response.Headers.Location
            Require ($null -ne $location -and $location.IsAbsoluteUri -and $location.Scheme -ceq 'https' -and
                -not $location.UserInfo -and ($location.Host -match '(^|\.)(?:blob\.core\.windows\.net|amazonaws\.com|githubusercontent\.com)$')) 'Invalid artifact download redirect.'
            $downloadTime = [Threading.CancellationTokenSource]::new((Budget ([TimeSpan]::FromMinutes(3))))
            try {
                $download = $null
                try {
                    $download = $anonymous.GetAsync($location, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $downloadTime.Token).GetAwaiter().GetResult()
                    Require $download.IsSuccessStatusCode 'The artifact bytes are unavailable.'
                    $stream = $download.Content.ReadAsStreamAsync($downloadTime.Token).GetAwaiter().GetResult()
                    $file = [IO.File]::Open($ArchivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    try {
                        $buffer = [byte[]]::new(65536)
                        [long] $length = 0
                        while (($count = $stream.ReadAsync($buffer, 0, $buffer.Length, $downloadTime.Token).GetAwaiter().GetResult()) -gt 0) {
                            $length += $count
                            Require ($length -le [long] $Artifact.size) 'Artifact download exceeds its expected byte size.'
                            $file.Write($buffer, 0, $count)
                        }
                        Require ($length -eq [long] $Artifact.size) 'Artifact download has an unexpected byte size.'
                    } finally { $file.Dispose(); $stream.Dispose() }
                } catch { throw 'Artifact download or byte validation failed.' }
                finally { if ($null -ne $download) { $download.Dispose() } }
            } finally { $downloadTime.Dispose() }
        } finally { $response.Dispose() }
    } finally { $redirectTime.Dispose() }
    Require ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $Artifact.sha256) 'Artifact SHA256 mismatch.'
}
function Test-ZipPath([string] $Name) {
    if (-not $Name -or $Name.StartsWith('/') -or $Name.Contains('\') -or $Name.Contains(':') -or
        $Name -match '[^\x20-\x7e]' -or $Name.Length -gt 1024) { return $false }
    $relative = $Name.TrimEnd('/')
    if (-not $relative -or ($Name.EndsWith('/') -and $Name -cne "$relative/")) { return $false }
    foreach ($part in $relative.Split('/')) {
        if (-not $part -or $part -in @('.', '..') -or $part.StartsWith(' ') -or $part.EndsWith('.') -or $part.EndsWith(' ') -or
            $part -match '[<>:"|?*]' -or
            $part -match '[\x00-\x1f\x7f]' -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $part -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)') { return $false }
    }
    return $true
}
function Expand-SafeArchive([string] $ArchivePath, [string] $Root, [string] $Name) {
    Require (-not (Test-Path -LiteralPath $Root)) 'Archive extraction root must start empty.'
    Assert-NoReparse ([IO.Path]::GetDirectoryName($Root))
    $zip = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        Require ($zip.Entries.Count -ge 3 -and $zip.Entries.Count -le 10000) 'Invalid artifact ZIP entry count.'
        $entries = [Collections.Generic.List[object]]::new()
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $prefixes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
        $files = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long] $total = 0
        foreach ($entry in $zip.Entries) {
            $nameInZip = $entry.FullName
            Require (Test-ZipPath $nameInZip) 'Artifact ZIP contains an unsafe path.'
            $directory = $nameInZip.EndsWith('/')
            $relative = $nameInZip.TrimEnd('/')
            Require ($seen.Add($relative)) 'Artifact ZIP contains duplicate paths.'
            $kind = ($entry.ExternalAttributes -shr 16) -band 0xF000
            $dosDirectory = ($entry.ExternalAttributes -band 0x10) -ne 0
            Require (($entry.ExternalAttributes -band 0x440) -eq 0 -and
                $kind -in @(0, 0x8000, 0x4000) -and
                ($directory -or $kind -ne 0x4000) -and
                (-not $directory -or $kind -ne 0x8000) -and
                ($directory -or -not $dosDirectory) -and
                $entry.Length -ge 0 -and $entry.Length -le 134217728 -and
                (-not $directory -or $entry.Length -eq 0)) 'Artifact ZIP contains an unsupported entry.'
            $total += $entry.Length
            Require ($total -le 536870912) 'Artifact ZIP expansion exceeds its byte bound.'
            $pieces = $relative.Split('/')
            for ($i = 0; $i -lt $pieces.Count; $i++) {
                $prefix = [string]::Join('/', $pieces[0..$i])
                if ($prefixes.ContainsKey($prefix)) {
                    Require ($prefixes[$prefix] -ceq $prefix) 'Artifact ZIP has ambiguous path casing.'
                } else { $prefixes[$prefix] = $prefix }
            }
            if (-not $directory) { [void] $files.Add($relative) }
            $entries.Add(@{ entry = $entry; relative = $relative; directory = $directory })
        }
        foreach ($item in $entries) {
            $pieces = $item.relative.Split('/')
            for ($i = 0; $i -lt $pieces.Count - 1; $i++) {
                Require (-not $files.Contains([string]::Join('/', $pieces[0..$i]))) 'Artifact ZIP has a file path prefix collision.'
            }
            if ($item.directory) { Require (-not $files.Contains($item.relative)) 'Artifact ZIP has a file and directory collision.' }
        }
        [IO.Directory]::CreateDirectory($Root) | Out-Null
        $fullRoot = [IO.Path]::GetFullPath($Root)
        $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
        $prefixRoot = $fullRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $clock = [Diagnostics.Stopwatch]::StartNew()
        [long] $writtenTotal = 0
        $buffer = [byte[]]::new(65536)
        foreach ($item in $entries) {
            Assert-Budget
            Require ($clock.Elapsed.TotalMinutes -lt 1) 'Artifact ZIP extraction exceeded its time bound.'
            $target = [IO.Path]::GetFullPath((Join-Path $Root ($item.relative.Replace('/', [IO.Path]::DirectorySeparatorChar))))
            Require ($target.StartsWith($prefixRoot, $comparison)) 'Artifact ZIP path escapes its root.'
            if ($item.directory) {
                [IO.Directory]::CreateDirectory($target) | Out-Null
                Assert-NoReparse $target
                continue
            }
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            Assert-NoReparse ([IO.Path]::GetDirectoryName($target))
            $inputStream = $item.entry.Open()
            $outputStream = [IO.File]::Open($target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                [long] $written = 0
                while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $written += $count
                    $writtenTotal += $count
                    Assert-Budget
                    Require ($written -le $item.entry.Length -and $writtenTotal -le 536870912 -and
                        $clock.Elapsed.TotalMinutes -lt 1) 'Artifact ZIP exceeded its declared size or time bound.'
                    $outputStream.Write($buffer, 0, $count)
                }
                Require ($written -eq $item.entry.Length) 'Artifact ZIP entry has an unexpected size.'
            } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        }
        $required = @('index.html', 'browser-publish.json', 'content/manifest.json')
        if ($Name -in @('windows-editor-advanced-shadow-parity-bundle', 'windows-editor-advanced-shadow-parity-off-bundle')) {
            $required += 'shadow-comparison.json'
        }
        foreach ($relative in $required) {
            Require ([IO.File]::Exists((Join-Path $Root $relative))) 'Artifact ZIP lacks an expected publication or receipt file.'
            Require (([IO.FileInfo]::new((Join-Path $Root $relative))).Length -gt 0 -and
                ([IO.FileInfo]::new((Join-Path $Root $relative))).Length -le 4194304) 'Publication metadata exceeds its size bound.'
        }
        $descriptor = [IO.File]::ReadAllText((Join-Path $Root 'browser-publish.json')) | ConvertFrom-Json -AsHashtable
        $manifest = [IO.File]::ReadAllText((Join-Path $Root 'content/manifest.json')) | ConvertFrom-Json -AsHashtable
        $worlds = @{
            'windows-editor-rollingball-bundle' = '/game/Worlds/RollingBallWorld.asset'
            'windows-editor-rendering-parity-bundle' = '/game/Worlds/RenderingParityWorld.asset'
            'windows-editor-advanced-rendering-parity-bundle' = '/game/Worlds/AdvancedRenderingParityWorld.asset'
            'windows-editor-advanced-shadow-parity-bundle' = '/game/Worlds/AdvancedRenderingParityWorld.asset'
            'windows-editor-advanced-shadow-parity-off-bundle' = '/game/Worlds/AdvancedRenderingParityWorld.asset'
            'windows-editor-ui-parity-bundle' = '/game/Worlds/BrowserUiParityWorld.asset'
            'windows-editor-modular-pipeline-parity-bundle' = '/game/Worlds/ModularPipelineParityWorld.asset'
            'windows-editor-static-meshlet-parity-bundle' = '/game/Worlds/StaticMeshletParityWorld.asset'
            'windows-editor-static-meshlet-parity-cpu-bundle' = '/game/Worlds/StaticMeshletParityWorld.asset'
        }
        Require ($descriptor.schema -eq 2 -and $descriptor.format -ceq 'xrengine-engine-launch' -and
            $descriptor.manifest -ceq './content/manifest.json' -and
            $manifest.startupWorld -ceq $worlds[$Name]) 'Artifact ZIP has the wrong publication descriptor or world.'
        if ($Name -in @('windows-editor-advanced-shadow-parity-bundle', 'windows-editor-advanced-shadow-parity-off-bundle')) {
            $receipt = [IO.File]::ReadAllText((Join-Path $Root 'shadow-comparison.json')) | ConvertFrom-Json -AsHashtable
            $state = if ($Name -ceq 'windows-editor-advanced-shadow-parity-bundle') { 'on' } else { 'off' }
            Require ($receipt.schema -eq 1 -and $receipt.state -ceq $state -and
                $receipt.sourceWorldSha256 -cmatch '^[0-9a-fA-F]{64}$' -and
                $receipt.publishedWorldSha256 -cmatch '^[0-9a-f]{64}$' -and
                $receipt.publishedStartupSettingsSha256 -cmatch '^[0-9a-f]{64}$' -and
                $receipt.verifiedStartupSemanticSha256 -cmatch '^[0-9a-f]{64}$') 'Artifact ZIP has an invalid shadow comparison receipt.'
        }
    } finally { $zip.Dispose() }
}
function Write-OutputValue([string] $Key, [string] $Value) {
    Require ($Value -notmatch '[\r\n]') 'Invalid workflow output value.'
    [IO.File]::AppendAllText($env:GITHUB_OUTPUT, "$Key=$Value`n", [Text.UTF8Encoding]::new($false))
}

if ($Mode -eq 'Select' -and ($env:GITHUB_REPOSITORY -cne $repository -or $env:GITHUB_EVENT_NAME -cne 'push' -or
    $env:GITHUB_REF -cne "refs/heads/$branch" -or $env:GITHUB_RUN_ATTEMPT -cne '1')) {
    Write-OutputValue 'reuse' 'false'
    return
}
if ($Mode -eq 'Select') { $script:selectionWatch = [Diagnostics.Stopwatch]::StartNew() }

Require (-not [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) 'GitHub read token is unavailable.'
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$api = [Net.Http.HttpClient]::new($handler)
$api.Timeout = [TimeSpan]::FromMinutes(5)
$api.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $env:GH_TOKEN)
$api.DefaultRequestHeaders.UserAgent.ParseAdd('XRENGINE-EditorBundleReuse/1')
$api.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')
$anonymousHandler = [Net.Http.HttpClientHandler]::new()
$anonymousHandler.AllowAutoRedirect = $false
$anonymous = [Net.Http.HttpClient]::new($anonymousHandler)
$anonymous.Timeout = [TimeSpan]::FromMinutes(5)
try {
    if ($Mode -eq 'Select') {
        Require ($env:GITHUB_REPOSITORY_ID -match '^[1-9][0-9]{0,18}$') 'Invalid current repository identity.'
        $repo = Get-Api 'repos/BlackJaxDev/XRENGINE'
        $repositoryId = Id $repo.id
        Require ($repositoryId -ceq $env:GITHUB_REPOSITORY_ID -and $repo.full_name -ceq $repository -and
            $repo.private -eq $false) 'Current repository identity does not match the public source.'
        $head = Sha $env:GITHUB_SHA
        Require ((Git-Text @('rev-parse', 'HEAD')) -ceq $head) 'Checked-out source is not the current run SHA.'
        $workflow = Get-Api 'repos/BlackJaxDev/XRENGINE/actions/workflows/portable-browser-compile.yml'
        $workflowId = Id $workflow.id
        Require ($workflow.path -ceq $workflowPath) 'Workflow path does not match.'
        $currentRunId = Id $env:GITHUB_RUN_ID
        $selected = $null
        for ($page = 1; $page -le 2 -and $null -eq $selected; $page++) {
            $runs = Get-Api "repos/BlackJaxDev/XRENGINE/actions/workflows/$workflowId/runs?event=push&branch=codex%2Fwebgpu-readiness-audit&status=completed&per_page=100&page=$page"
            foreach ($run in @($runs.workflow_runs)) {
                Assert-Budget
                if ([long] (Id $run.id) -ge [long] $currentRunId -or $run.status -cne 'completed' -or
                    $run.event -cne 'push' -or
                    $run.head_branch -cne $branch -or [int] $run.run_attempt -ne 1 -or
                    (Id $run.workflow_id) -cne $workflowId -or $run.path -cne $workflowPath -or
                    (Id $run.repository.id) -cne $repositoryId -or
                    (Id $run.head_repository.id) -cne $repositoryId) { continue }
                $source = Sha $run.head_sha
                if (-not (Allowed-Diff $source $head)) { continue }
                $job = Get-Producer $run $repositoryId
                if ($null -eq $job) { continue }
                $bundles = Get-Bundles $run $job $repositoryId
                if ($null -eq $bundles) { continue }
                $selected = [ordered]@{ schema = 1; currentSha = $head; currentRunId = $currentRunId;
                    currentAttempt = 1; repositoryId = $repositoryId;
                    workflowId = $workflowId; workflowPath = $workflowPath; producerRunId = (Id $run.id);
                    producerAttempt = 1; producerJobId = (Id $job.id); producerSha = $source;
                    checks = @('exact-origin', 'successful-attempt-one-producer', 'allowed-complete-git-diff',
                        'nine-finalized-artifacts', 'nine-hash-and-size-verified-zips', 'safe-extraction-and-required-files');
                    bundles = $bundles }
                break
            }
        }
        if ($null -eq $selected) { Write-OutputValue 'reuse' 'false'; return }
        $preflight = Join-Path $env:RUNNER_TEMP "editor-bundle-preflight-$currentRunId"
        Require (-not (Test-Path -LiteralPath $preflight)) 'The bundle preflight root already exists.'
        [IO.Directory]::CreateDirectory($preflight) | Out-Null
        try {
            foreach ($name in $bundleNames) {
                Assert-Budget
                $archive = Join-Path $preflight "$name.zip"
                Get-Archive $selected.bundles[$name] $archive
                $expanded = Join-Path $preflight $name
                Expand-SafeArchive $archive $expanded $name
                [IO.Directory]::Delete($expanded, $true)
                [IO.File]::Delete($archive)
            }
        } finally { [IO.Directory]::Delete($preflight, $true) }
        $manifest = $selected | ConvertTo-Json -Compress -Depth 10
        $provenance = Join-Path $env:VALIDATION_ROOT 'editor-bundle-reuse-provenance.json'
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($provenance))) | Out-Null
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($provenance), ($selected | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        Assert-Budget
        Write-OutputValue 'manifest' $manifest
        Write-OutputValue 'reuse' 'true'
        Write-Output "Validated all nine original Editor ZIPs from producer run $($selected.producerRunId)."
    } else {
        Require ($Kind -cin $bundleNames) 'Unknown Editor bundle kind.'
        Require ($env:GITHUB_REPOSITORY -ceq $repository -and $env:GITHUB_EVENT_NAME -ceq 'push' -and
            $env:GITHUB_REF -ceq "refs/heads/$branch" -and $env:GITHUB_RUN_ATTEMPT -ceq '1') 'Reuse is unavailable on this attempt.'
        Require (-not [string]::IsNullOrWhiteSpace($Destination) -and -not [string]::IsNullOrWhiteSpace($env:REUSE_MANIFEST)) 'Missing verified reuse input.'
        $manifest = $env:REUSE_MANIFEST | ConvertFrom-Json -AsHashtable
        Require ($manifest.schema -eq 1 -and (Sha $manifest.currentSha) -ceq (Sha $env:GITHUB_SHA) -and
            (Id $manifest.currentRunId) -ceq (Id $env:GITHUB_RUN_ID) -and $manifest.currentAttempt -eq 1 -and
            (Id $manifest.repositoryId) -ceq (Id $env:GITHUB_REPOSITORY_ID) -and
            $manifest.workflowPath -ceq $workflowPath -and $manifest.producerAttempt -eq 1 -and
            $manifest.bundles.Count -eq 9) 'Reuse provenance does not match this run.'
        $artifact = $manifest.bundles[$Kind]
        Require ($null -ne $artifact -and $artifact.sha256 -cmatch '^[0-9a-f]{64}$' -and
            [long] $artifact.size -ge 1 -and [long] $artifact.size -le 268435456) 'Missing exact artifact identity.'
        $live = Get-Api "repos/BlackJaxDev/XRENGINE/actions/artifacts/$(Id $artifact.id)"
        Require ((Id $live.id) -ceq (Id $artifact.id) -and $live.name -ceq $Kind -and
            $live.expired -eq $false -and $live.digest -ceq "sha256:$($artifact.sha256)" -and
            [long] $live.size_in_bytes -eq [long] $artifact.size -and
            (Id $live.workflow_run.id) -ceq (Id $manifest.producerRunId) -and
            (Sha $live.workflow_run.head_sha) -ceq (Sha $manifest.producerSha)) 'Original artifact identity changed or expired.'
        Require ((Safe-Time $live.expires_at) -gt [DateTimeOffset]::UtcNow) 'Original artifact expired.'
        $archive = Join-Path $env:RUNNER_TEMP "editor-bundle-$(Id $artifact.id).zip"
        Get-Archive $artifact $archive
        Expand-SafeArchive $archive $Destination $Kind
        Write-Output "Verified original Editor bundle $Kind from producer run $($manifest.producerRunId)."
    }
} finally {
    $api.Dispose()
    $anonymous.Dispose()
}
