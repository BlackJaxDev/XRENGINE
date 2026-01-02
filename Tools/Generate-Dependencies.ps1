$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$outPath = Join-Path $root 'docs\DEPENDENCIES.md'
$licensesDir = Join-Path $root 'docs\licenses'

$nugetRoot = if ($env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES
} else {
    Join-Path $env:USERPROFILE '.nuget\packages'
}

function Invoke-Git([string[]]$args, [string]$workingDir) {
    try {
        $out = & git -C $workingDir @args 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        if ($out) { return ($out | Select-Object -First 1).Trim() }
        return $null
    } catch {
        return $null
    }
}

function Get-RepoCommitId([string]$repoRoot) {
    return Invoke-Git -args @('rev-parse', 'HEAD') -workingDir $repoRoot
}

function Get-GitOriginUrl([string]$path) {
    try {
        $top = Invoke-Git -args @('rev-parse', '--show-toplevel') -workingDir $path
        if (-not $top) { return $null }
        if (-not $top) { return $null }
        $rp = (Resolve-Path -LiteralPath $path).Path
        if ($top -ne $rp) { return $null }

        $u = Invoke-Git -args @('remote', 'get-url', 'origin') -workingDir $path
        if ($u) { return $u }
    } catch {
    }

    return $null
}

function Get-GitHubOwner([string]$url) {
    if (-not $url) { return $null }
    if ($url -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/?#]+)') { return $Matches.owner }
    return $null
}

function Get-GitHubRepoParts([string]$url) {
    if (-not $url) { return $null }
    if ($url -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/?#]+)') {
        $repo = $Matches.repo
        if ($repo.EndsWith('.git')) { $repo = $repo.Substring(0, $repo.Length - 4) }
        return [pscustomobject]@{ Owner = $Matches.owner; Repo = $repo }
    }
    return $null
}

function Get-GitHubRepoInfo([string]$owner, [string]$repo) {
    if (-not $owner -or -not $repo) { return $null }

    $uri = "https://api.github.com/repos/$owner/$repo"
    try {
        return Invoke-RestMethod -Uri $uri -Headers @{ 'User-Agent' = 'XRENGINE-Dependencies' } -Method Get
    } catch {
        return $null
    }
}

function Detect-LicenseFromText([string]$text) {
    if (-not $text) { return $null }
    $t = $text.ToLowerInvariant()

    if ($t -match 'mit license') { return 'MIT' }
    if ($t -match 'the mit license') { return 'MIT' }
    # MIT often appears without the phrase "MIT License".
    if (
        ($t -match 'permission is hereby granted, free of charge, to any person obtaining a copy') -and
        ($t -match 'the software is provided "as is"') -and
        ($t -match 'without warranty of any kind')
    ) { return 'MIT' }
    if ($t -match 'apache license') { return 'Apache-2.0' }
    if ($t -match 'apache license, version 2\.0') { return 'Apache-2.0' }
    if ($t -match 'gnu affero general public license') { return 'AGPL-3.0' }
    if ($t -match 'gnu lesser general public license') { return 'LGPL-3.0' }
    if ($t -match 'gnu general public license') { return 'GPL-3.0' }
    if ($t -match 'mozilla public license') { return 'MPL-2.0' }
    if ($t -match 'isc license') { return 'ISC' }
    if ($t -match 'microsoft public license') { return 'MS-PL' }
    if ($t -match 'ms-pl') { return 'MS-PL' }
    if ($t -match 'freetype license') { return 'FreeType License (FTL)' }
    if ($t -match 'freetype project' -and $t -match 'ftl\.txt') { return 'FreeType License (FTL)' }
    if ($t -match 'zlib license') { return 'Zlib' }
    if ($t -match 'boost software license') { return 'BSL-1.0' }
    if ($t -match 'unlicense') { return 'Unlicense' }
    if ($t -match 'creative commons') { return 'CC' }

    if ($t -match 'nvidia rtx sdks license') { return 'NVIDIA RTX SDKs License' }
    if ($t -match 'license agreement for nvidia software development kits') { return 'NVIDIA SDK License Agreement' }
    return $null
}

function Detect-LicenseFromFile([string]$path) {
    if (-not $path -or -not (Test-Path $path)) { return $null }
    try {
        $text = Get-Content -Raw -LiteralPath $path
        return Detect-LicenseFromText -text $text
    } catch {
        return $null
    }
}

function Ensure-Directory([string]$path) {
    if (-not $path) { return }
    if (-not (Test-Path -LiteralPath $path)) {
        New-Item -ItemType Directory -Path $path -Force | Out-Null
    }
}

function To-SafeFileName([string]$name) {
    if (-not $name) { return 'unknown' }
    $invalid = [System.IO.Path]::GetInvalidFileNameChars()
    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $name.ToCharArray()) {
        if ($invalid -contains $ch) { $null = $sb.Append('_') } else { $null = $sb.Append($ch) }
    }
    $s = $sb.ToString().Trim().TrimEnd('.')
    if (-not $s) { return 'unknown' }
    return $s
}

function Write-LicenseTextFile([string]$relativePathFromDocsLicenses, [string]$text) {
    if (-not $relativePathFromDocsLicenses) { return $null }
    if ($null -eq $text -or $text -eq '') { return $null }

    # Remove a few problematic control characters that can sneak into extracted texts.
    $text = [regex]::Replace($text, "[\x00\f\v]", "")

    Ensure-Directory -path $licensesDir
    $dst = Join-Path $licensesDir ($relativePathFromDocsLicenses -replace '/', '\\')
    Ensure-Directory -path (Split-Path -Parent $dst)
    $text | Out-File -LiteralPath $dst -Encoding utf8

    # Link relative to docs/DEPENDENCIES.md (docs/)
    return ('licenses/{0}' -f ($relativePathFromDocsLicenses -replace '\\', '/'))
}

function Copy-LicenseFile([string]$relativePathFromDocsLicenses, [string]$sourcePath) {
    if (-not $relativePathFromDocsLicenses -or -not $sourcePath) { return $null }
    if (-not (Test-Path -LiteralPath $sourcePath)) { return $null }

    Ensure-Directory -path $licensesDir
    $dst = Join-Path $licensesDir ($relativePathFromDocsLicenses -replace '/', '\\')
    Ensure-Directory -path (Split-Path -Parent $dst)
    Copy-Item -LiteralPath $sourcePath -Destination $dst -Force

    return ('licenses/{0}' -f ($relativePathFromDocsLicenses -replace '\\', '/'))
}

function Format-LicenseCell([string]$licenseText, [string]$licenseLink) {
    $lt = if ($licenseText) { $licenseText } else { '(unknown)' }
    if ($licenseLink) {
        return ('[{0}]({1})' -f $lt, $licenseLink)
    }
    return $lt
}

function Get-SubmoduleLicenseInfo([string]$fullPath) {
    if (-not $fullPath -or -not (Test-Path $fullPath)) { return $null }
    $licenseCandidates = @('LICENSE', 'LICENSE.txt', 'COPYING', 'COPYING.txt')
    foreach ($n in $licenseCandidates) {
        $p = Join-Path $fullPath $n
        if (-not (Test-Path -LiteralPath $p)) { continue }
        try {
            $text = Get-Content -Raw -LiteralPath $p
            $det = Detect-LicenseFromText -text $text
            $lic = if ($det) { $det } else { $n }
            return [pscustomobject]@{ License = $lic; SourcePath = $p; Text = $text }
        } catch {
        }
    }

    try {
        $any = Get-ChildItem -LiteralPath $fullPath -File -Filter 'LICENSE*' -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($any) {
            try {
                $text = Get-Content -Raw -LiteralPath $any.FullName
                $det = Detect-LicenseFromText -text $text
                $lic = if ($det) { $det } else { $any.Name }
                return [pscustomobject]@{ License = $lic; SourcePath = $any.FullName; Text = $text }
            } catch {
            }
        }
    } catch {
    }

    return $null
}

function Get-SubmoduleLicense([string]$fullPath) {
    $i = Get-SubmoduleLicenseInfo -fullPath $fullPath
    if ($i -and $i.License) { return $i.License }
    return $null
}

function Get-NuSpecMeta([string]$id, [string]$version) {
    $idLower = $id.ToLowerInvariant()
    $dir = Join-Path $nugetRoot (Join-Path $idLower $version)
    if (-not (Test-Path $dir)) { return $null }

    $nuspec = Join-Path $dir ("$idLower.nuspec")
    if (-not (Test-Path $nuspec)) {
        $cand = Get-ChildItem -LiteralPath $dir -Filter '*.nuspec' -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cand) { $nuspec = $cand.FullName } else { return $null }
    }

    try {
        [xml]$x = Get-Content -Raw -LiteralPath $nuspec
        $ns = New-Object System.Xml.XmlNamespaceManager($x.NameTable)
        $ns.AddNamespace('n', $x.DocumentElement.NamespaceURI)

        $authorsNode = $x.SelectSingleNode('//n:metadata/n:authors', $ns)
        $authors = if ($authorsNode) { $authorsNode.InnerText } else { $null }

        $repoNode = $x.SelectSingleNode('//n:metadata/n:repository', $ns)
        $repoUrl = if ($repoNode) { $repoNode.GetAttribute('url') } else { $null }

        $projectUrlNode = $x.SelectSingleNode('//n:metadata/n:projectUrl', $ns)
        $projectUrl = if ($projectUrlNode) { $projectUrlNode.InnerText } else { $null }

        $licenseNode = $x.SelectSingleNode('//n:metadata/n:license', $ns)
        $license = if ($licenseNode) { $licenseNode.InnerText } else { $null }
        $licenseType = if ($licenseNode) { $licenseNode.GetAttribute('type') } else { $null }

        $licenseUrlNode = $x.SelectSingleNode('//n:metadata/n:licenseUrl', $ns)
        $licenseUrl = if ($licenseUrlNode) { $licenseUrlNode.InnerText } else { $null }

        return [pscustomobject]@{
            Authors       = $authors
            RepositoryUrl = $repoUrl
            ProjectUrl    = $projectUrl
            License       = $license
            LicenseType   = $licenseType
            LicenseUrl    = $licenseUrl
            NuspecPath    = $nuspec
        }
    } catch {
        return $null
    }
}

function Get-NuGetPackageDir([string]$id, [string]$version) {
    $idLower = $id.ToLowerInvariant()
    $dir = Join-Path $nugetRoot (Join-Path $idLower $version)
    if (Test-Path $dir) { return $dir }
    return $null
}

function Read-NuGetExtractedFileText([string]$id, [string]$version, [string]$relativeOrFileName) {
    if (-not $relativeOrFileName) { return $null }
    $dir = Get-NuGetPackageDir -id $id -version $version
    if (-not $dir) { return $null }

    # Try exact relative path, then basename.
    $p1 = Join-Path $dir $relativeOrFileName
    if (Test-Path $p1) {
        try { return Get-Content -Raw -LiteralPath $p1 } catch { }
    }

    $base = [System.IO.Path]::GetFileName($relativeOrFileName)
    if ($base) {
        $p2 = Join-Path $dir $base
        if (Test-Path $p2) {
            try { return Get-Content -Raw -LiteralPath $p2 } catch { }
        }
    }

    return $null
}

function Get-NuGetNupkgPath([string]$id, [string]$version) {
    $dir = Get-NuGetPackageDir -id $id -version $version
    if (-not $dir) { return $null }
    $cand = Get-ChildItem -LiteralPath $dir -Filter '*.nupkg' -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cand) { return $cand.FullName }
    return $null
}

function Read-NupkgEntryText([string]$nupkgPath, [string]$entryName) {
    if (-not $nupkgPath -or -not (Test-Path $nupkgPath) -or -not $entryName) { return $null }
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue | Out-Null
        $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkgPath)
        try {
            $entry = $zip.Entries | Where-Object { $_.FullName -ieq $entryName } | Select-Object -First 1
            if (-not $entry) {
                # Some packages store it at root without path normalization
                $base = [System.IO.Path]::GetFileName($entryName)
                $entry = $zip.Entries | Where-Object { [System.IO.Path]::GetFileName($_.FullName) -ieq $base } | Select-Object -First 1
            }
            if (-not $entry) { return $null }

            $sr = New-Object System.IO.StreamReader($entry.Open())
            try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
        } finally {
            $zip.Dispose()
        }
    } catch {
        return $null
    }
}

function Get-GitHubLicenseTextFromRepoUrl([string]$url) {
    $parts = Get-GitHubRepoParts -url $url
    if (-not $parts) { return $null }
    try {
        $licInfo = Invoke-RestMethod -Uri ("https://api.github.com/repos/{0}/{1}/license" -f $parts.Owner, $parts.Repo) -Headers @{ 'User-Agent' = 'XRENGINE-Dependencies' } -Method Get
        if (-not $licInfo -or -not $licInfo.content) { return $null }
        $bytes = [System.Convert]::FromBase64String([string]$licInfo.content)
        return [System.Text.Encoding]::UTF8.GetString($bytes)
    } catch {
        return $null
    }
}

function Resolve-NuGetLicenseFileText([string]$id, [string]$version, $meta) {
    # Prefer local package artifacts.
    if ($meta -and $meta.LicenseType -eq 'file' -and $meta.License) {
        $nupkg = Get-NuGetNupkgPath -id $id -version $version
        $txt = Read-NupkgEntryText -nupkgPath $nupkg -entryName $meta.License
        if (-not $txt) { $txt = Read-NuGetExtractedFileText -id $id -version $version -relativeOrFileName $meta.License }
        return $txt
    }

    if ($meta -and $meta.License) {
        $licVal = $meta.License.Trim()
        if ($licVal -match '^(LICENSE|COPYING)(\.txt)?$' -or $licVal -match '\\.txt$') {
            $nupkg = Get-NuGetNupkgPath -id $id -version $version
            $txt = Read-NupkgEntryText -nupkgPath $nupkg -entryName $licVal
            if (-not $txt) { $txt = Read-NuGetExtractedFileText -id $id -version $version -relativeOrFileName $licVal }
            return $txt
        }
    }

    # Look for common license file names in extracted package folder.
    $dir = Get-NuGetPackageDir -id $id -version $version
    if ($dir) {
        foreach ($cand in @('LICENSE', 'LICENSE.txt', 'license.txt', 'COPYING', 'COPYING.txt', 'NOTICE', 'NOTICE.txt')) {
            $p = Join-Path $dir $cand
            if (Test-Path -LiteralPath $p) {
                try { return Get-Content -Raw -LiteralPath $p } catch { }
            }
        }
    }

    # If we have a GitHub repo URL, grab its license text via API.
    if ($meta) {
        $t = Get-GitHubLicenseTextFromRepoUrl -url $meta.RepositoryUrl
        if (-not $t) { $t = Get-GitHubLicenseTextFromRepoUrl -url $meta.ProjectUrl }
        if (-not $t -and $meta.LicenseUrl -and (Is-AbsoluteHttpUrl -s $meta.LicenseUrl)) {
            $t = Try-ReadUrlText -url $meta.LicenseUrl
        }
        return $t
    }

    return $null
}

function Is-AbsoluteHttpUrl([string]$s) {
    if (-not $s) { return $false }
    return ($s -match '^https?://')
}

function Convert-GitHubBlobUrlToRaw([string]$url) {
    if (-not $url) { return $null }
    # https://github.com/<owner>/<repo>/blob/<ref>/<path>
    if ($url -match '^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/blob/(?<ref>[^/]+)/(?<path>.+)$') {
        return ("https://raw.githubusercontent.com/{0}/{1}/{2}/{3}" -f $Matches.owner, $Matches.repo, $Matches.ref, $Matches.path)
    }
    return $url
}

function Looks-LikeHtml([string]$text) {
    if (-not $text) { return $false }
    return ($text -match '<\s*!doctype\s+html' -or $text -match '<\s*html\b' -or $text -match '<\s*head\b' -or $text -match '<\s*body\b')
}

function Convert-HtmlToPlainText([string]$html) {
    if (-not $html) { return $null }
    $t = $html

    # Remove script/style blocks.
    $t = [regex]::Replace($t, '(?is)<script\b.*?</script>', '')
    $t = [regex]::Replace($t, '(?is)<style\b.*?</style>', '')

    # Add newlines for common block elements.
    $t = [regex]::Replace($t, '(?is)<br\s*/?>', "`n")
    $t = [regex]::Replace($t, '(?is)</p\s*>', "`n")
    $t = [regex]::Replace($t, '(?is)</h\d\s*>', "`n")
    $t = [regex]::Replace($t, '(?is)</div\s*>', "`n")

    # Strip remaining tags.
    $t = [regex]::Replace($t, '(?is)<[^>]+>', '')

    # Decode entities (&quot; etc).
    try {
        Add-Type -AssemblyName System.Net.Primitives -ErrorAction SilentlyContinue | Out-Null
    } catch {
    }
    try {
        $t = [System.Net.WebUtility]::HtmlDecode($t)
    } catch {
    }

    # Remove common control characters that sometimes sneak into HTML-derived text.
    $t = $t -replace "[\f\v]", ""

    # Normalize whitespace.
    $t = $t -replace "\r\n", "`n"
    $t = $t -replace "\r", "`n"

    # Strip trailing whitespace, remove whitespace-only lines, and collapse large blank runs.
    $rawLines = $t -split "`n"
    $lines = New-Object System.Collections.Generic.List[string]
    $blankRun = 0
    foreach ($ln in $rawLines) {
        $line = $ln.TrimEnd()
        if ($line -match '^\s*$') {
            $blankRun++
            if ($blankRun -le 1) {
                $lines.Add('')
            }
            continue
        }
        $blankRun = 0
        $lines.Add($line)
    }

    # Final cleanup: trim leading/trailing blank lines.
    $out = ($lines -join "`n")
    $out = $out.Trim("`r","`n"," ","`t")
    return $out
}

function Normalize-LicenseTextFromHtml([string]$text) {
    if (-not $text) { return $null }
    $t = $text -replace "\r\n", "`n"
    $t = $t -replace "\r", "`n"
    # licenses.nuget.org / SPDX HTML often introduces indentation; drop it.
    $t = [regex]::Replace($t, '(?m)^[\t ]+', '')
    # Collapse multiple spaces created by tag boundaries.
    $t = [regex]::Replace($t, '[ \t]{2,}', ' ')
    # Clean up blank lines again.
    $t = [regex]::Replace($t, "(?m)[ \t]+$", '')
    $t = [regex]::Replace($t, "\n{3,}", "`n`n")
    return $t.Trim("`r","`n"," ","`t")
}

function Extract-LicenseTextFromNuGetHtml([string]$html) {
    if (-not $html) { return $null }
    # licenses.nuget.org pages include a dedicated "License text" section.
    $m = [regex]::Match($html, '(?is)<h2>\s*License text\s*</h2>(?<sec>.*?)(<h2>\s*SPDX web page\s*</h2>|<h2>\s*Notice\s*</h2>|</body>|</html>)')
    if (-not $m.Success) { return $null }
    $plain = Convert-HtmlToPlainText -html $m.Groups['sec'].Value
    return Normalize-LicenseTextFromHtml -text $plain
}

function Extract-LicenseTextFromHtml([string]$html) {
    if (-not $html) { return $null }
    $lower = $html.ToLowerInvariant()

    if ($lower -match 'licenses\.nuget\.org' -or $lower -match '<div\s+id="license-expression"') {
        $nu = Extract-LicenseTextFromNuGetHtml -html $html
        if ($nu) { return $nu }
    }

    return Convert-HtmlToPlainText -html $html
}

function Try-ReadUrlText([string]$url) {
    if (-not (Is-AbsoluteHttpUrl -s $url)) { return $null }
    $u = Convert-GitHubBlobUrlToRaw -url $url
    try {
        $text = Invoke-RestMethod -Uri $u -Headers @{ 'User-Agent' = 'XRENGINE-Dependencies' } -Method Get
        $s = [string]$text
        if (Looks-LikeHtml -text $s) {
            $extracted = Extract-LicenseTextFromHtml -html $s
            if ($extracted) { return $extracted }
        }
        return $s
    } catch {
        return $null
    }
}

$repoLicenseCache = @{}
function Get-GitHubLicenseSpdxFromUrl([string]$url) {
    $parts = Get-GitHubRepoParts -url $url
    if (-not $parts) { return $null }

    $key = ("{0}/{1}" -f $parts.Owner.ToLowerInvariant(), $parts.Repo.ToLowerInvariant())
    if ($repoLicenseCache.ContainsKey($key)) { return $repoLicenseCache[$key] }

    $spdx = $null
    # Prefer the /license endpoint (more direct and sometimes provides content).
    try {
        $licInfo = Invoke-RestMethod -Uri ("https://api.github.com/repos/{0}/{1}/license" -f $parts.Owner, $parts.Repo) -Headers @{ 'User-Agent' = 'XRENGINE-Dependencies' } -Method Get
        if ($licInfo -and $licInfo.license -and $licInfo.license.spdx_id -and $licInfo.license.spdx_id -ne 'NOASSERTION') {
            $spdx = $licInfo.license.spdx_id
        } elseif ($licInfo -and $licInfo.content) {
            try {
                $bytes = [System.Convert]::FromBase64String([string]$licInfo.content)
                $text = [System.Text.Encoding]::UTF8.GetString($bytes)
                $det = Detect-LicenseFromText -text $text
                if ($det) { $spdx = $det }
            } catch {
            }
        }
    } catch {
    }

    if (-not $spdx) {
        $info = Get-GitHubRepoInfo -owner $parts.Owner -repo $parts.Repo
        if ($info -and $info.license -and $info.license.spdx_id -and $info.license.spdx_id -ne 'NOASSERTION') { $spdx = $info.license.spdx_id }
    }
    $repoLicenseCache[$key] = $spdx
    return $spdx
}

function Resolve-NuGetLicense([string]$id, [string]$version, $meta) {
    # Preferred: SPDX expression in <license type="expression">MIT</license>
    if ($meta -and $meta.LicenseType -eq 'expression' -and $meta.License) {
        return $meta.License
    }

    # If <license type="file">license.txt</license> then read that file from .nupkg
    if ($meta -and $meta.LicenseType -eq 'file' -and $meta.License) {
        $nupkg = Get-NuGetNupkgPath -id $id -version $version
        $txt = Read-NupkgEntryText -nupkgPath $nupkg -entryName $meta.License
        if (-not $txt) { $txt = Read-NuGetExtractedFileText -id $id -version $version -relativeOrFileName $meta.License }
        $det = Detect-LicenseFromText -text $txt
        if ($det) { return $det }
        return $meta.License
    }

    # If <license> is present but no type, it may still be an SPDX-ish string, or it may be a filename.
    if ($meta -and $meta.License) {
        $licVal = $meta.License.Trim()
        if ($licVal -match '^(LICENSE|COPYING)(\.txt)?$' -or $licVal -match '\.txt$') {
            $nupkg = Get-NuGetNupkgPath -id $id -version $version
            $txt = Read-NupkgEntryText -nupkgPath $nupkg -entryName $licVal
            if (-not $txt) { $txt = Read-NuGetExtractedFileText -id $id -version $version -relativeOrFileName $licVal }
            $det = Detect-LicenseFromText -text $txt
            if ($det) { return $det }
            # keep as last-resort signal
            return $licVal
        }
        return $licVal
    }

    # LicenseUrl is often a link; best-effort to detect common license names.
    if ($meta -and $meta.LicenseUrl) {
        $lu = $meta.LicenseUrl.Trim()

        # Some nuspecs (older) store a relative file name here.
        if (-not (Is-AbsoluteHttpUrl -s $lu)) {
            $nupkg = Get-NuGetNupkgPath -id $id -version $version
            $txt = Read-NupkgEntryText -nupkgPath $nupkg -entryName $lu
            if (-not $txt) { $txt = Read-NuGetExtractedFileText -id $id -version $version -relativeOrFileName $lu }
            $det = Detect-LicenseFromText -text $txt
            if ($det) { return $det }
            return $lu
        }

        $urlLower = $lu.ToLowerInvariant()
        if ($urlLower -match 'apache-2\.0') { return 'Apache-2.0' }
        if ($urlLower -match 'mpl-2\.0') { return 'MPL-2.0' }

        # Try to fetch and detect (may return HTML; still often contains license header text).
        try {
            $text = Invoke-RestMethod -Uri $lu -Headers @{ 'User-Agent' = 'XRENGINE-Dependencies' } -Method Get
            $det = Detect-LicenseFromText -text ([string]$text)
            if ($det) { return $det }
        } catch {
        }

        # If this is a GitHub repo license URL, ask GitHub API for SPDX id.
        $spdx = Get-GitHubLicenseSpdxFromUrl -url $lu
        if ($spdx) { return $spdx }

        return $lu
    }

    # Last resort: check the embedded nuspec path folder for a license file name.
    if ($meta -and $meta.NuspecPath) {
        $dir = Split-Path -Parent $meta.NuspecPath
        foreach ($cand in @('license.txt','License.txt','LICENSE','LICENSE.txt','COPYING','COPYING.txt')) {
            $p = Join-Path $dir $cand
            $det = Detect-LicenseFromFile -path $p
            if ($det) { return $det }
        }
    }

    # Fallback to GitHub repo metadata if we have it.
    if ($meta) {
        $spdx = Get-GitHubLicenseSpdxFromUrl -url $meta.RepositoryUrl
        if (-not $spdx) { $spdx = Get-GitHubLicenseSpdxFromUrl -url $meta.ProjectUrl }
        if ($spdx) { return $spdx }
    }

    return '(unknown)'
}

# --- Collect solution projects ---
$solutionPath = Join-Path $root 'XRENGINE.sln'
$slnText = Get-Content -Raw -LiteralPath $solutionPath
$csprojRel = [regex]::Matches($slnText, '"(?<p>[^"]+\.csproj)"') | ForEach-Object { $_.Groups['p'].Value } | Sort-Object -Unique

$csproj = @()
foreach ($p in $csprojRel) {
    $full = Join-Path $root $p
    if (Test-Path $full) { $csproj += (Resolve-Path -LiteralPath $full).Path }
}

# --- Parse csproj for packages, references, and copied binaries ---
$packages = @{} # id -> object
$refs = New-Object System.Collections.Generic.List[object]
$binaries = New-Object System.Collections.Generic.List[object]

function Get-BinaryOwner([string]$fileOrPath) {
    if (-not $fileOrPath) { return '(unknown)' }
    $name = [System.IO.Path]::GetFileName($fileOrPath)

    switch -Regex ($name) {
        '^openvr_api\.dll$' { return 'Valve (OpenVR/SteamVR)' }
        '^openxr_loader\.dll$' { return 'Valve (SteamVR) / Khronos (OpenXR loader)' }
        '^OVRLipSync\.dll$' { return 'Meta/Oculus (OVR LipSync)' }

        '^(av.*|sw(resample|scale)(-[0-9]+)?|postproc)(-[0-9]+)?\.dll$' { return 'FFmpeg project' }
        '^ffmpeg\.exe$' { return 'FFmpeg project' }
        '^ffplay\.exe$' { return 'FFmpeg project' }
        '^ffprobe\.exe$' { return 'FFmpeg project' }

        '^sl\..*\.dll$' { return 'NVIDIA (Streamline)' }
        '^nvngx_.*\.dll$' { return 'NVIDIA (DLSS/NIS/DeepDVC)' }
        '^NvLowLatencyVk\.dll$' { return 'NVIDIA (Reflex / Low Latency)' }

        '^lib_coacd\.(dll|so|dylib)$' { return 'SarahWeiii (CoACD)' }
        '^libmagicphysx\.(dll|so|dylib)$' { return 'MagicPhysX' }

        '^libmp3lame\.(32|64)\.dll$' { return 'LAME / NAudio.Lame (Corey-M) packaging' }
        default { return '(unknown)' }
    }
}

function Get-BinaryLicense([string]$fileOrPath) {
    if (-not $fileOrPath) { return '(unknown)' }
    $name = [System.IO.Path]::GetFileName($fileOrPath)

    switch -Regex ($name) {
        '^sl\.nis\.dll$' { return 'MIT (see XRENGINE/nis.license.txt)' }
        '^nvngx_.*\.dll$' { return 'NVIDIA RTX SDKs License (see XRENGINE/nvngx_dlss.license.txt)' }
        '^NvLowLatencyVk\.dll$' { return 'NVIDIA SDK License Agreement (see XRENGINE/reflex.license.txt)' }
        '^sl\..*\.dll$' { return '(unknown - see NVIDIA license files)' }

        '^lib_coacd\.(dll|so|dylib)$' { return 'MIT (see Build/Submodules/CoACD/LICENSE)' }
        '^libmagicphysx\.(dll|so|dylib)$' { return '(unknown)' }

        '^(av.*|sw(resample|scale)(-[0-9]+)?|postproc)(-[0-9]+)?\.dll$' { return '(unknown - depends on FFmpeg build config)' }
        '^ff(mpeg|play|probe)\.exe$' { return '(unknown - depends on FFmpeg build config)' }
        default { return '(unknown)' }
    }
}

function Get-BinaryLicenseLink([string]$fileOrPath) {
    if (-not $fileOrPath) { return $null }
    $name = [System.IO.Path]::GetFileName($fileOrPath)

    switch -Regex ($name) {
        '^sl\.nis\.dll$' { return '../XRENGINE/nis.license.txt' }
        '^nvngx_.*\.dll$' { return '../ThirdParty/NVIDIA/SDK/win-x64/nvngx_dlss.license.txt' }
        '^NvLowLatencyVk\.dll$' { return '../ThirdParty/NVIDIA/SDK/win-x64/reflex.license.txt' }
        '^sl\..*\.dll$' { return '../ThirdParty/NVIDIA/SDK/win-x64/' }
        '^lib_coacd\.(dll|so|dylib)$' { return '../Build/Submodules/CoACD/LICENSE' }
        default { return $null }
    }
}

function Get-ReferenceOwner([string]$referenceName, [string]$hintPath) {
    if ($hintPath -match '\\Build\\Submodules\\(?<sub>[^\\/]+)\\') {
        $sub = $Matches.sub
        $match = $submodules | Where-Object { $_.Path -eq ("Build/Submodules/{0}" -f $sub) } | Select-Object -First 1
        if ($match -and $match.Owner) { return $match.Owner }
    }
    if ($referenceName -match '^OpenVR\.NET') { return 'BlackJaxDev' }
    if ($referenceName -match '^OscCore') { return 'BlackJaxDev' }
    if ($referenceName -match '^RiveSharp') { return 'rive-app' }
    return '(unknown)'
}

foreach ($proj in $csproj) {
    try { [xml]$x = Get-Content -Raw -LiteralPath $proj } catch { continue }
    $projName = Split-Path $proj -Leaf

    foreach ($pr in $x.SelectNodes('//PackageReference')) {
        $id = $pr.GetAttribute('Include')
        if (-not $id) { $id = $pr.GetAttribute('Update') }
        if (-not $id) { continue }

        $ver = $pr.GetAttribute('Version')
        if (-not $ver) { $ver = '' }

        if (-not $packages.ContainsKey($id)) {
            $packages[$id] = [pscustomobject]@{
                Id       = $id
                Versions = (New-Object System.Collections.Generic.HashSet[string])
                Projects = (New-Object System.Collections.Generic.HashSet[string])
                Meta     = $null
                Owner    = $null
            }
        }
        if ($ver) { $null = $packages[$id].Versions.Add($ver) }
        $null = $packages[$id].Projects.Add($projName)
    }

    foreach ($r in $x.SelectNodes('//Reference')) {
        $include = $r.GetAttribute('Include')
        $hintNode = $r.SelectSingleNode('HintPath')
        $hint = if ($hintNode) { $hintNode.InnerText } else { $null }
        if ($include -or $hint) {
            $refs.Add([pscustomobject]@{ Project = $projName; Reference = $include; HintPath = $hint })
        }
    }

    foreach ($n in $x.SelectNodes('//Content|//None')) {
        $path = $n.GetAttribute('Include')
        if (-not $path) { $path = $n.GetAttribute('Update') }
        if (-not $path) { continue }
        if ($path -match '\.(dll|exe)$') {
            $copyNode = $n.SelectSingleNode('CopyToOutputDirectory')
            $copy = if ($copyNode) { $copyNode.InnerText } else { $null }

            $linkNode = $n.SelectSingleNode('Link')
            $link = if ($linkNode) { $linkNode.InnerText } else { $null }
            $binaries.Add([pscustomobject]@{ Project = $projName; Path = $path; Link = $link; CopyToOutputDirectory = $copy })
        }
    }
}

# --- Resolve NuGet owners/authors ---
foreach ($k in $packages.Keys) {
    $pkg = $packages[$k]
    $ver = ($pkg.Versions | Sort-Object | Select-Object -First 1)
    if (-not $ver) { continue }

    $meta = Get-NuSpecMeta -id $pkg.Id -version $ver
    $pkg.Meta = $meta
    if ($meta) {
        $owner = Get-GitHubOwner $meta.RepositoryUrl
        if (-not $owner -and $meta.ProjectUrl) { $owner = Get-GitHubOwner $meta.ProjectUrl }
        if (-not $owner -and $meta.Authors) { $owner = $meta.Authors }
        $pkg.Owner = $owner
    }

    $pkg | Add-Member -NotePropertyName LicenseResolved -NotePropertyValue (Resolve-NuGetLicense -id $pkg.Id -version $ver -meta $meta) -Force
    $pkg | Add-Member -NotePropertyName LicenseText -NotePropertyValue (Resolve-NuGetLicenseFileText -id $pkg.Id -version $ver -meta $meta) -Force
}

# --- Submodules (from .gitmodules + actual Build/Submodules folders) ---
$submodules = New-Object System.Collections.Generic.List[object]

$gitmodulesPath = Join-Path $root '.gitmodules'
if (Test-Path $gitmodulesPath) {
    $gm = Get-Content -Raw -LiteralPath $gitmodulesPath
    $blocks = $gm -split "\r?\n\r?\n" | Where-Object { $_ -match '\[submodule' }
    foreach ($b in $blocks) {
        $name = ([regex]::Match($b, '\[submodule\s+"(?<n>[^"]+)"\]')).Groups['n'].Value
        $path = ([regex]::Match($b, '^\s*path\s*=\s*(?<p>.+)$', 'Multiline')).Groups['p'].Value.Trim()
        $url = ([regex]::Match($b, '^\s*url\s*=\s*(?<u>.+)$', 'Multiline')).Groups['u'].Value.Trim()
        if ($path) {
            $diskPath = Join-Path $root ($path -replace '/', '\\')
            $licInfo = Get-SubmoduleLicenseInfo -fullPath $diskPath
            $lic = $null
            $licSourcePath = $null
            if ($licInfo) {
                $lic = $licInfo.License
                $licSourcePath = $licInfo.SourcePath
            }
            $submodules.Add([pscustomobject]@{ Name = $name; Path = $path; Url = $url; Owner = (Get-GitHubOwner $url); License = $lic; LicenseSourcePath = $licSourcePath })
        }
    }
}

$submodulesDir = Join-Path $root 'Build\Submodules'
if (Test-Path $submodulesDir) {
    Get-ChildItem -LiteralPath $submodulesDir -Directory | ForEach-Object {
        $origin = Get-GitOriginUrl $_.FullName
        $licInfo = Get-SubmoduleLicenseInfo -fullPath $_.FullName
        $lic = $null
        $licSourcePath = $null
        if ($licInfo) {
            $lic = $licInfo.License
            $licSourcePath = $licInfo.SourcePath
        }
        $submodules.Add([pscustomobject]@{ Name = $_.Name; Path = ("Build/Submodules/{0}" -f $_.Name); Url = $origin; Owner = (Get-GitHubOwner $origin); License = $lic; LicenseSourcePath = $licSourcePath })
    }
}

# Dedupe by Path, preferring entries that include a URL/Owner.
$submodules = $submodules |
    Group-Object -Property Path |
    ForEach-Object {
        $_.Group |
            Sort-Object @(
                @{ Expression = { if ($_.Url) { 0 } else { 1 } } },
                @{ Expression = { if ($_.Owner) { 0 } else { 1 } } },
                @{ Expression = { $_.Name } }
            ) |
            Select-Object -First 1
    } |
    Sort-Object Path, Name

# Known vendored dependency upstreams (when the folder isn't a git submodule in this checkout).
foreach ($s in $submodules) {
    if ($s.Path -eq 'Build/Submodules/CoACD' -and (-not $s.Url -or $s.Url -eq '(not detected)')) {
        $s.Url = 'https://github.com/SarahWeiii/CoACD'
        $s.Owner = 'SarahWeiii'
        if (-not $s.License) { $s.License = 'MIT' }
    }
}

# If license still unknown, try GitHub API for SPDX id (best-effort).
foreach ($s in $submodules) {
    if ($s.License) { continue }
    if (-not $s.Url) { continue }
    $parts = Get-GitHubRepoParts -url $s.Url
    if (-not $parts) { continue }
    $info = Get-GitHubRepoInfo -owner $parts.Owner -repo $parts.Repo
    if ($info -and $info.license -and $info.license.spdx_id) {
        $s.License = $info.license.spdx_id
    }
}

# If a submodule is a BlackJaxDev fork, keep the fork URL but make ownership explicit.
foreach ($s in $submodules) {
    if (-not $s.Url) { continue }
    $parts = Get-GitHubRepoParts -url $s.Url
    if (-not $parts) { continue }
    if ($parts.Owner -ne 'BlackJaxDev') { continue }

    $info = Get-GitHubRepoInfo -owner $parts.Owner -repo $parts.Repo
    if (-not $info) { continue }

    if ($info.fork -and $info.parent) {
        $upstreamOwner = $info.parent.owner.login
        $upstreamUrl = $info.parent.html_url

        $s | Add-Member -NotePropertyName UpstreamOwner -NotePropertyValue $upstreamOwner -Force
        $s | Add-Member -NotePropertyName UpstreamUrl -NotePropertyValue $upstreamUrl -Force

        # Keep $s.Url as the fork URL; annotate ownership instead.
        $s.Owner = ("{0} + BlackJaxDev (modifications)" -f $upstreamOwner)

        if (-not $s.License -and $info.parent.license -and $info.parent.license.spdx_id) {
            $s.License = $info.parent.license.spdx_id
        }
    }
}

# Nested submodules / fetched-from-upstream dependencies referenced by build scripts.
$nested = New-Object System.Collections.Generic.List[object]
try {
    $buildCoacd = Join-Path $root 'Tools\Dependencies\Build-CoACD.ps1'
    if (Test-Path $buildCoacd) {
        $txt = Get-Content -Raw -LiteralPath $buildCoacd
        if ($txt -match 'github\.com/(?<owner>artem-ogre)/(?<repo>CDT)') {
            $nested.Add([pscustomobject]@{
                Name = 'CDT'
                UsedBy = 'CoACD'
                Owner = $Matches.owner
                Url = 'https://github.com/artem-ogre/CDT'
                License = '(unknown)'
            })
        }
    }
} catch {
}

# Fill nested license from GitHub API if available.
foreach ($n in $nested) {
    if ($n.License -and $n.License -ne '(unknown)') { continue }
    if (-not $n.Url) { continue }
    $parts = Get-GitHubRepoParts -url $n.Url
    if (-not $parts) { continue }
    $info = Get-GitHubRepoInfo -owner $parts.Owner -repo $parts.Repo
    if ($info -and $info.license -and $info.license.spdx_id) {
        $n.License = $info.license.spdx_id
    }
}

# --- Checked-in binaries (filesystem) ---
$checkedBinaries = New-Object System.Collections.Generic.List[object]
$xreDir = Join-Path $root 'XRENGINE'
if (Test-Path $xreDir) {
    Get-ChildItem -Path (Join-Path $xreDir '*.dll') -File -ErrorAction SilentlyContinue | ForEach-Object {
        $checkedBinaries.Add([pscustomobject]@{ Path = ("XRENGINE/{0}" -f $_.Name); File = $_.Name })
    }
    Get-ChildItem -Path (Join-Path $xreDir '*.exe') -File -ErrorAction SilentlyContinue | ForEach-Object {
        $checkedBinaries.Add([pscustomobject]@{ Path = ("XRENGINE/{0}" -f $_.Name); File = $_.Name })
    }

    $rt = Join-Path $xreDir 'runtimes'
    if (Test-Path $rt) {
        Get-ChildItem -LiteralPath $rt -Recurse -File -Filter '*.dll' -ErrorAction SilentlyContinue | ForEach-Object {
            $rel = $_.FullName.Substring($root.Length).TrimStart('\') -replace '\\', '/'
            $checkedBinaries.Add([pscustomobject]@{ Path = $rel; File = $_.Name })
        }
    }
}

$checkedBinaries = $checkedBinaries | Sort-Object Path -Unique

# --- Write markdown ---
$lines = New-Object System.Collections.Generic.List[string]

# Clear and re-materialize docs/licenses on each run to prevent stale license files.
try {
    if (Test-Path -LiteralPath $licensesDir) {
        Remove-Item -LiteralPath $licensesDir -Recurse -Force -ErrorAction SilentlyContinue
    }
} catch {
}
Ensure-Directory -path $licensesDir

$generatedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
$commitId = Get-RepoCommitId -repoRoot $root
$hasGit = $null -ne (Get-Command git -ErrorAction SilentlyContinue)
$commitText = if ($commitId) {
    $commitId
} elseif ($hasGit) {
    '(not a git repo)'
} else {
    '(git not found)'
}

$lines.Add('# Dependency Inventory')
$lines.Add('')
$lines.Add(("Generated: {0}" -f $generatedAt))
$lines.Add(("Commit: {0}" -f $commitText))
$lines.Add('')
$lines.Add('Best-effort inventory of dependencies referenced by the XRENGINE solution: NuGet packages, git submodules, and native/managed binaries that are referenced or shipped.')
$lines.Add('')
$lines.Add('Notes:')
$lines.Add('- `Owner` is derived from a GitHub repository URL when available, otherwise from the NuGet nuspec `authors` field (best-effort).')
$lines.Add('- This lists direct `PackageReference`s from solution projects, not all transitive dependencies.')
$lines.Add('- NVIDIA proprietary SDK binaries (DLSS/NGX, Reflex, Streamline) are **not redistributed** and are expected to be provided by end users via `ThirdParty/NVIDIA/SDK/win-x64/`.')
$lines.Add('')

$lines.Add('## Git submodules / vendored submodules')
$lines.Add('| Name | Path | Owner | License (best-effort) | URL |')
$lines.Add('|---|---|---|---|---|')
foreach ($s in $submodules) {
    $owner = if ($s.Owner) { $s.Owner } else { '(unknown)' }
    $licText = if ($s.License) { $s.License } else { '(unknown)' }
    $licLink = $null

    if ($s.PSObject.Properties.Match('LicenseSourcePath').Count -gt 0 -and $s.LicenseSourcePath) {
        $ext = [System.IO.Path]::GetExtension($s.LicenseSourcePath)
        if (-not $ext) { $ext = '.txt' }
        $dstRel = ('submodules/{0}-{1}{2}' -f (To-SafeFileName $s.Name), (To-SafeFileName $licText), $ext)
        $licLink = Copy-LicenseFile -relativePathFromDocsLicenses $dstRel -sourcePath $s.LicenseSourcePath
    } elseif ($s.Url) {
        $t = Get-GitHubLicenseTextFromRepoUrl -url $s.Url
        if ($t) {
            $dstRel = ('github/{0}-{1}.txt' -f (To-SafeFileName $s.Name), (To-SafeFileName $licText))
            $licLink = Write-LicenseTextFile -relativePathFromDocsLicenses $dstRel -text $t
        }
    }

    if (-not $licLink) {
        $dstRel = ('unknown/submodules-{0}.txt' -f (To-SafeFileName $s.Name))
        $licLink = Write-LicenseTextFile -relativePathFromDocsLicenses $dstRel -text ("License file not detected locally for submodule '{0}'.`r`n`r`nURL: {1}`r`n" -f $s.Name, $s.Url)
    }

    $lic = Format-LicenseCell -licenseText $licText -licenseLink $licLink
    $url = if ($s.Url) { $s.Url } else { '(not detected)' }
    $lines.Add(("| {0} | {1} | {2} | {3} | {4} |" -f $s.Name, $s.Path, $owner, $lic, $url))
}
$lines.Add('')

if ($nested.Count -gt 0) {
    $lines.Add('## Nested / fetched dependencies (build scripts)')
    $lines.Add('| Name | Used by | Owner | License (best-effort) | URL |')
    $lines.Add('|---|---|---|---|---|')
    foreach ($n in ($nested | Sort-Object Name, UsedBy)) {
        $owner = if ($n.Owner) { $n.Owner } else { '(unknown)' }
        $licText = if ($n.License) { $n.License } else { '(unknown)' }
        $licLink = $null
        if ($n.Url) {
            $t = Get-GitHubLicenseTextFromRepoUrl -url $n.Url
            if ($t) {
                $dstRel = ('github/{0}-{1}.txt' -f (To-SafeFileName $n.Name), (To-SafeFileName $licText))
                $licLink = Write-LicenseTextFile -relativePathFromDocsLicenses $dstRel -text $t
            }
        }
        if (-not $licLink) {
            $dstRel = ('unknown/nested-{0}.txt' -f (To-SafeFileName $n.Name))
            $licLink = Write-LicenseTextFile -relativePathFromDocsLicenses $dstRel -text ("License file not detected for nested dependency '{0}'.`r`n`r`nURL: {1}`r`n" -f $n.Name, $n.Url)
        }
        $lic = Format-LicenseCell -licenseText $licText -licenseLink $licLink
        $url = if ($n.Url) { $n.Url } else { '(unknown)' }
        $lines.Add(("| {0} | {1} | {2} | {3} | {4} |" -f $n.Name, $n.UsedBy, $owner, $lic, $url))
    }
    $lines.Add('')
}

$lines.Add('## NuGet packages (direct)')
$lines.Add('| Package | Version(s) | Owner (best-effort) | License (best-effort) | Used by |')
$lines.Add('|---|---|---|---|---|')
foreach ($pkg in ($packages.Values | Sort-Object Id)) {
    $vers = (($pkg.Versions | Sort-Object) -join ', ')
    $owner = if ($pkg.Owner) { $pkg.Owner } else { '(unknown - nuspec not found locally)' }
    $licText = if ($pkg.PSObject.Properties.Match('LicenseResolved').Count -gt 0 -and $pkg.LicenseResolved) { $pkg.LicenseResolved } else { '(unknown)' }
    $licLink = $null
    $verForFile = (($pkg.Versions | Sort-Object | Select-Object -First 1))
    $pkgSafe = To-SafeFileName $pkg.Id
    $licSafe = To-SafeFileName $licText

    if ($pkg.PSObject.Properties.Match('LicenseText').Count -gt 0 -and $pkg.LicenseText) {
        $dstRel = ("nuget/{0}-{1}-{2}.txt" -f $pkgSafe, (To-SafeFileName $verForFile), $licSafe)
        $licLink = Write-LicenseTextFile -relativePathFromDocsLicenses $dstRel -text $pkg.LicenseText
    } elseif ($pkg.Meta) {
        $t = Get-GitHubLicenseTextFromRepoUrl -url $pkg.Meta.RepositoryUrl
        if (-not $t) { $t = Get-GitHubLicenseTextFromRepoUrl -url $pkg.Meta.ProjectUrl }
        if ($t) {
            $dstRel = ("nuget/{0}-{1}-{2}.txt" -f $pkgSafe, (To-SafeFileName $verForFile), $licSafe)
            $licLink = Write-LicenseTextFile -relativePathFromDocsLicenses $dstRel -text $t
        }
    }

    if (-not $licLink -and $pkg.Meta -and $pkg.Meta.LicenseUrl -and (Is-AbsoluteHttpUrl -s $pkg.Meta.LicenseUrl)) {
        $licLink = Convert-GitHubBlobUrlToRaw -url $pkg.Meta.LicenseUrl
    }

    if (-not $licLink) {
        $dstRel = ("unknown/nuget-{0}-{1}.txt" -f $pkgSafe, (To-SafeFileName $verForFile))
        $repoUrl = if ($pkg.Meta) { $pkg.Meta.RepositoryUrl } else { '' }
        $projUrl = if ($pkg.Meta) { $pkg.Meta.ProjectUrl } else { '' }
        $licLink = Write-LicenseTextFile -relativePathFromDocsLicenses $dstRel -text ("License text could not be resolved locally for NuGet package '{0}' ({1}).`r`n`r`nResolved license: {2}`r`nRepositoryUrl: {3}`r`nProjectUrl: {4}`r`n" -f $pkg.Id, $verForFile, $licText, $repoUrl, $projUrl)
    }

    $lic = Format-LicenseCell -licenseText $licText -licenseLink $licLink
    $usedBy = (($pkg.Projects | Sort-Object) -join ', ')
    $lines.Add(("| {0} | {1} | {2} | {3} | {4} |" -f $pkg.Id, $vers, $owner, $lic, $usedBy))
}
$lines.Add('')

$lines.Add('## Explicit assembly references (`<Reference>` )')
$lines.Add('| Project | Reference | Owner (best-effort) | License (best-effort) | HintPath |')
$lines.Add('|---|---|---|---|---|')
foreach ($r in ($refs | Sort-Object Project, Reference, HintPath)) {
    $refText = if ($null -ne $r.Reference) { $r.Reference } else { '' }
    $hintText = if ($null -ne $r.HintPath) { $r.HintPath } else { '' }
    $ownerText = Get-ReferenceOwner -referenceName $refText -hintPath $hintText
    $licText = '(unknown)'
    $licLink = $null
    if ($hintText -match '\\Build\\Submodules\\(?<sub>[^\\/]+)\\') {
        $sub = $Matches.sub
        $subPath = Join-Path (Join-Path $root 'Build\Submodules') $sub
        $li = Get-SubmoduleLicenseInfo -fullPath $subPath
        if ($li -and $li.License) {
            $licText = $li.License
            if ($li.SourcePath) {
                $ext = [System.IO.Path]::GetExtension($li.SourcePath)
                if (-not $ext) { $ext = '.txt' }
                $dstRel = ('submodules/{0}-{1}{2}' -f (To-SafeFileName $sub), (To-SafeFileName $licText), $ext)
                $licLink = Copy-LicenseFile -relativePathFromDocsLicenses $dstRel -sourcePath $li.SourcePath
            }
        }
    }

    if (-not $licLink) {
        $dstRel = ('unknown/reference-{0}-{1}.txt' -f (To-SafeFileName $r.Project), (To-SafeFileName $refText))
        $licLink = Write-LicenseTextFile -relativePathFromDocsLicenses $dstRel -text ("License text not detected for assembly reference.`r`n`r`nProject: {0}`r`nReference: {1}`r`nHintPath: {2}`r`n" -f $r.Project, $refText, $hintText)
    }

    $lic = Format-LicenseCell -licenseText $licText -licenseLink $licLink
    $lines.Add(("| {0} | {1} | {2} | {3} | {4} |" -f $r.Project, $refText, $ownerText, $lic, $hintText))
}
$lines.Add('')

$lines.Add('## Referenced binaries via project items (dll/exe)')
$lines.Add('| Project | Path/Update | Owner (best-effort) | License (best-effort) | Link | CopyToOutputDirectory |')
$lines.Add('|---|---|---|---|---|---|')
foreach ($b in ($binaries | Sort-Object Project, Path)) {
    $linkText = if ($null -ne $b.Link) { $b.Link } else { '' }
    $copyText = if ($null -ne $b.CopyToOutputDirectory) { $b.CopyToOutputDirectory } else { '' }
    $ownerText = Get-BinaryOwner -fileOrPath $b.Path
    $licText = Get-BinaryLicense -fileOrPath $b.Path
    $licLink = Get-BinaryLicenseLink -fileOrPath $b.Path
    if (-not $licLink) {
        $dstRel = ('unknown/binary-item-{0}-{1}.txt' -f (To-SafeFileName $b.Project), (To-SafeFileName $b.Path))
        $licLink = Write-LicenseTextFile -relativePathFromDocsLicenses $dstRel -text ("License file not detected for referenced binary item.`r`n`r`nProject: {0}`r`nPath: {1}`r`n" -f $b.Project, $b.Path)
    }
    $lic = Format-LicenseCell -licenseText $licText -licenseLink $licLink
    $lines.Add(("| {0} | {1} | {2} | {3} | {4} | {5} |" -f $b.Project, $b.Path, $ownerText, $lic, $linkText, $copyText))
}
$lines.Add('')

$lines.Add('## Checked-in native/managed binaries (filesystem)')
$lines.Add('| Path | File | Likely upstream/owner | License (best-effort) |')
$lines.Add('|---|---|---|---|')
foreach ($f in $checkedBinaries) {
    $owner = Get-BinaryOwner -fileOrPath $f.File
    $licText = Get-BinaryLicense -fileOrPath $f.File
    $licLink = Get-BinaryLicenseLink -fileOrPath $f.File
    if (-not $licLink) {
        $dstRel = ('unknown/checked-binary-{0}.txt' -f (To-SafeFileName $f.File))
        $licLink = Write-LicenseTextFile -relativePathFromDocsLicenses $dstRel -text ("License file not detected for checked-in binary.`r`n`r`nPath: {0}`r`nFile: {1}`r`n" -f $f.Path, $f.File)
    }
    $lic = Format-LicenseCell -licenseText $licText -licenseLink $licLink
    $lines.Add(("| {0} | {1} | {2} | {3} |" -f $f.Path, $f.File, $owner, $lic))
}

$lines | Out-File -LiteralPath $outPath -Encoding utf8
Write-Output "Wrote: $outPath"
