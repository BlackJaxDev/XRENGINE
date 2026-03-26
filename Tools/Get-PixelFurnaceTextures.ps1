<#
.SYNOPSIS
    Downloads free texture archives from Pixel-Furnace.

.DESCRIPTION
    Crawls the Pixel-Furnace texture catalog via the site's article-listing endpoint,
    displays all available textures with their included PBR maps, lets the user
    interactively choose which ones to download, then downloads and extracts the
    selected ZIP archives into per-texture folders.

    Pixel-Furnace states that these textures are free to use in commercial and
    non-commercial projects, but their terms do not allow redistributing the
    textures by themselves or as a collection. Keep the downloaded textures out of
    shipped asset packs unless they are bundled into a larger game or project.

.PARAMETER OutputDir
    Directory where texture folders and the generated manifest are written.
    Defaults to Build\CommonAssets\Textures\Samples under the repo root.

.PARAMETER NamePattern
    Wildcard filter applied to the discovered texture name and ZIP filename.
    Example: -NamePattern '*Brick*'

.PARAMETER MaxPages
    Safety cap for catalog pagination crawling.

.PARAMETER ThrottleMs
    Delay between page requests in milliseconds.

.PARAMETER Force
    Re-download and re-extract textures even when the folder already exists locally.

.PARAMETER ListOnly
    Discover textures and write the manifest without downloading archives.

.PARAMETER All
    Download all textures without interactive selection.

.EXAMPLE
    .\Tools\Get-PixelFurnaceTextures.ps1

.EXAMPLE
    .\Tools\Get-PixelFurnaceTextures.ps1 -NamePattern '*Brick*' -ListOnly

.EXAMPLE
    .\Tools\Get-PixelFurnaceTextures.ps1 -All
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$NamePattern = '*',
    [ValidateRange(1, 500)]
    [int]$MaxPages = 32,
    [ValidateRange(0, 10000)]
    [int]$ThrottleMs = 250,
    [switch]$Force,
    [switch]$ListOnly,
    [switch]$All
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot 'Build\CommonAssets\Textures\Samples'
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$manifestPath = Join-Path $OutputDir 'pixel-furnace-textures.index.json'

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Add-Type -AssemblyName System.Net.Http

$regexOptions = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline
$baseUri = [Uri]'https://textures.pixel-furnace.com/'

function New-HttpClient {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
    $handler.UseCookies = $true

    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.BaseAddress = $baseUri
    $client.Timeout = [TimeSpan]::FromMinutes(15)
    $null = $client.DefaultRequestHeaders.UserAgent.ParseAdd('XRENGINE PixelFurnace Downloader')
    return [pscustomobject]@{
        Client = $client
        Handler = $handler
    }
}

function Get-StringResponse {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)]
        [string]$RelativeUri
    )

    $response = $Client.GetAsync($RelativeUri).GetAwaiter().GetResult()
    try {
        $response.EnsureSuccessStatusCode() | Out-Null
        return $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
    finally {
        $response.Dispose()
    }
}

function New-FormUrlEncodedContent {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Fields
    )

    $pairs = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]]::new()
    foreach ($entry in $Fields.GetEnumerator()) {
        $pairs.Add([System.Collections.Generic.KeyValuePair[string,string]]::new([string]$entry.Key, [string]$entry.Value))
    }

    return [System.Net.Http.FormUrlEncodedContent]::new($pairs)
}

function Post-FormForString {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)]
        [string]$RelativeUri,
        [Parameter(Mandatory = $true)]
        [hashtable]$Fields
    )

    $content = New-FormUrlEncodedContent -Fields $Fields
    try {
        $response = $Client.PostAsync($RelativeUri, $content).GetAwaiter().GetResult()
        try {
            $response.EnsureSuccessStatusCode() | Out-Null
            return $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $content.Dispose()
    }
}

function Get-AttributeValue {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Attributes,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $match = [regex]::Match($Attributes, '(?is)\b' + [regex]::Escape($Name) + '\s*=\s*["''](?<value>.*?)["'']')
    if ($match.Success) {
        return [System.Net.WebUtility]::HtmlDecode($match.Groups['value'].Value)
    }

    return $null
}

function Get-FilterDefaults {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Html
    )

    $html = $Html
    $formMatch = [regex]::Match($html, '(?is)<form[^>]*\bid\s*=\s*["'']filter["''][^>]*>(?<content>.*?)</form>')
    if (-not $formMatch.Success) {
        return @{}
    }

    $defaults = @{}
    $formHtml = $formMatch.Groups['content'].Value

    foreach ($inputMatch in [regex]::Matches($formHtml, '(?is)<input\b(?<attrs>[^>]*?)>')) {
        $attrs = $inputMatch.Groups['attrs'].Value
        $name = Get-AttributeValue -Attributes $attrs -Name 'name'
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }

        $type = Get-AttributeValue -Attributes $attrs -Name 'type'
        if ([string]::IsNullOrWhiteSpace($type)) {
            $type = 'text'
        }

        $value = Get-AttributeValue -Attributes $attrs -Name 'value'
        $isChecked = $attrs -match '(?i)\bchecked\b'

        switch ($type.ToLowerInvariant()) {
            'hidden' { $defaults[$name] = if ($null -ne $value) { $value } else { '' } }
            'text' { $defaults[$name] = if ($null -ne $value) { $value } else { '' } }
            'search' { $defaults[$name] = if ($null -ne $value) { $value } else { '' } }
            'range' { $defaults[$name] = if ($null -ne $value) { $value } else { '' } }
            'number' { $defaults[$name] = if ($null -ne $value) { $value } else { '' } }
            'checkbox' {
                if ($isChecked) {
                    $defaults[$name] = if ([string]::IsNullOrWhiteSpace($value)) { 'on' } else { $value }
                }
            }
            'radio' {
                if ($isChecked) {
                    $defaults[$name] = if ($null -ne $value) { $value } else { '' }
                }
            }
        }
    }

    foreach ($selectMatch in [regex]::Matches($formHtml, '(?is)<select\b(?<attrs>[^>]*?)>(?<content>.*?)</select>')) {
        $attrs = $selectMatch.Groups['attrs'].Value
        $name = Get-AttributeValue -Attributes $attrs -Name 'name'
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }

        $options = [regex]::Matches($selectMatch.Groups['content'].Value, '(?is)<option\b(?<attrs>[^>]*?)>(?<text>.*?)</option>')
        if ($options.Count -eq 0) {
            continue
        }

        $selected = $null
        foreach ($option in $options) {
            $optionAttrs = $option.Groups['attrs'].Value
            if ($optionAttrs -match '(?i)\bselected\b') {
                $selected = $option
                break
            }
        }

        if ($null -eq $selected) {
            $selected = $options[0]
        }

        $value = Get-AttributeValue -Attributes $selected.Groups['attrs'].Value -Name 'value'
        if ($null -eq $value) {
            $value = [System.Net.WebUtility]::HtmlDecode(($selected.Groups['text'].Value -replace '<[^>]+>', '').Trim())
        }

        $defaults[$name] = $value
    }

    return $defaults
}

function Get-TotalResults {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Html
    )

    $match = [regex]::Match($Html, 'Showing results\s+\d+\s*-\s*\d+\s+of\s+(?<total>\d+)\s*:', $regexOptions)
    if ($match.Success) {
        return [int]$match.Groups['total'].Value
    }

    return $null
}

function Get-TextureEntries {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Html,
        [Parameter(Mandatory = $true)]
        [int]$Page
    )

    $articleRegex = [regex]::new('(?s)<article\b[^>]*>(.+?)</article>', $regexOptions)
    $articleMatches = $articleRegex.Matches($Html)

    if ($articleMatches.Count -eq 0) {
        return @()
    }

    $mapTypes = @('diffuse', 'normal', 'bump', 'specular', 'roughness', 'metalness')
    $mapLabels = @{
        diffuse   = 'Albedo'
        normal    = 'Normal'
        bump      = 'Bump'
        specular  = 'Specular'
        roughness = 'Roughness'
        metalness = 'Metal'
    }

    $results = [System.Collections.Generic.List[object]]::new()

    foreach ($articleMatch in $articleMatches) {
        $card = $articleMatch.Value

        # Extract texture name from the title link
        $nameMatch = [regex]::Match($card, '<a[^>]*href\s*=\s*"[^"]*/?texture\?name=(?<nameenc>[^"]+)"[^>]*>\s*<h2[^>]*>(?<name>[^<]+)</h2>', $regexOptions)
        $name = $null
        $detailUrl = $null
        if ($nameMatch.Success) {
            $name = [System.Net.WebUtility]::HtmlDecode($nameMatch.Groups['name'].Value.Trim())
            $rawDetailUrl = $nameMatch.Value
            $hrefMatch = [regex]::Match($rawDetailUrl, 'href\s*=\s*"(?<url>[^"]+)"', $regexOptions)
            if ($hrefMatch.Success) {
                $rawHref = $hrefMatch.Groups['url'].Value
                $detailUrl = if ($rawHref.StartsWith('http', [System.StringComparison]::OrdinalIgnoreCase)) {
                    $rawHref
                } elseif ($rawHref.StartsWith('/')) {
                    "https://textures.pixel-furnace.com$rawHref"
                } else {
                    "https://textures.pixel-furnace.com/$rawHref"
                }
            }
        }

        # Extract download URL
        $dlMatch = [regex]::Match($card, 'href\s*=\s*"(?<url>(?:https?://textures\.pixel-furnace\.com)?/?uploads/textures/[^"]+\.zip)"', $regexOptions)
        if (-not $dlMatch.Success) { continue }

        $rawDownloadUrl = $dlMatch.Groups['url'].Value
        $downloadUrl = if ($rawDownloadUrl.StartsWith('http', [System.StringComparison]::OrdinalIgnoreCase)) {
            $rawDownloadUrl
        } elseif ($rawDownloadUrl.StartsWith('/')) {
            "https://textures.pixel-furnace.com$rawDownloadUrl"
        } else {
            "https://textures.pixel-furnace.com/$rawDownloadUrl"
        }

        $fileName = [System.IO.Path]::GetFileName([Uri]::UnescapeDataString(([Uri]$downloadUrl).AbsolutePath))

        if ([string]::IsNullOrWhiteSpace($name)) {
            $name = [System.IO.Path]::GetFileNameWithoutExtension($fileName) -replace '[_]', ' '
        }

        # Extract resolution and file size
        $resolution = $null
        $resMatch = [regex]::Match($card, '<span\s+class="number">(?<res>[^<]+)</span>px', $regexOptions)
        if ($resMatch.Success) { $resolution = $resMatch.Groups['res'].Value.Trim() + 'px' }

        $fileSize = $null
        $sizeMatch = [regex]::Match($card, '<span\s+class="fileSize">(?<size>[^<]+)</span>', $regexOptions)
        if ($sizeMatch.Success) { $fileSize = $sizeMatch.Groups['size'].Value.Trim() }

        # Parse included maps from the texture_info paragraphs
        $maps = [ordered]@{}
        foreach ($mt in $mapTypes) {
            # A map paragraph with "inactive" in its class means the map is NOT included
            $mapRx = [regex]::new('class\s*=\s*"[^"]*\btexture_info\b[^"]*\b' + [regex]::Escape($mt) + '\b(?<cls>[^"]*)"', $regexOptions)
            $mapMatch = $mapRx.Match($card)
            if ($mapMatch.Success) {
                $isActive = -not ($mapMatch.Groups['cls'].Value -match '\binactive\b')
                $maps[$mapLabels[$mt]] = $isActive
            }
        }

        # Extract article ID from download link
        $articleId = $null
        $aidMatch = [regex]::Match($card, 'data-article\s*=\s*"(?<id>\d+)"', $regexOptions)
        if ($aidMatch.Success) { $articleId = $aidMatch.Groups['id'].Value }

        $results.Add([pscustomobject]@{
            Page        = $Page
            Name        = $name
            DetailUrl   = $detailUrl
            DownloadUrl = $downloadUrl
            ArticleId   = $articleId
            FileName    = $fileName
            Resolution  = $resolution
            FileSize    = $fileSize
            Maps        = $maps
        }) | Out-Null
    }

    return $results
}

function Invoke-FileDownload {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)]
        [string]$Uri,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $response = $Client.GetAsync($Uri, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
    try {
        $response.EnsureSuccessStatusCode() | Out-Null
        $inputStream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        try {
            $outputStream = [System.IO.File]::Create($DestinationPath)
            try {
                $inputStream.CopyTo($outputStream)
            }
            finally {
                $outputStream.Dispose()
            }
        }
        finally {
            $inputStream.Dispose()
        }
    }
    finally {
        $response.Dispose()
    }
}

$http = New-HttpClient
$client = $http.Client
$handler = $http.Handler

try {
    Write-Host '=== Pixel-Furnace Texture Downloader ===' -ForegroundColor Cyan
    Write-Host "Output directory: $OutputDir" -ForegroundColor Cyan
    Write-Host 'Terms: use in games/projects is allowed; redistributing the textures alone is not.' -ForegroundColor Yellow

    # Fetch homepage once — reuse for filter defaults and page-1 parsing
    $homepageHtml = Get-StringResponse -Client $client -RelativeUri '/'

    $defaultFields = Get-FilterDefaults -Html $homepageHtml
    if ($defaultFields.Count -gt 0) {
        Write-Host ("Captured {0} default filter field(s) from the site." -f $defaultFields.Count) -ForegroundColor DarkGray
    }
    else {
        Write-Host 'Could not detect filter defaults from the site; using page-only requests.' -ForegroundColor Yellow
    }

    $seenDownloadUrls = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $allEntries = [System.Collections.Generic.List[object]]::new()
    $detectedTotalPages = $null

    for ($page = 1; $page -le $MaxPages; $page++) {
        $fields = @{}
        foreach ($key in $defaultFields.Keys) {
            $fields[$key] = $defaultFields[$key]
        }
        $fields['page'] = [string]$page

        $pageHtml = Post-FormForString -Client $client -RelativeUri 'fetchArticles.php' -Fields $fields
        $pageEntries = @(Get-TextureEntries -Html $pageHtml -Page $page)
        if ($pageEntries.Count -eq 0) {
            if ($page -eq 1) {
                throw 'Texture discovery returned no entries on the first page.'
            }

            Write-Host "Page $page returned no textures; stopping." -ForegroundColor DarkGray
            break
        }

        $newCount = 0
        foreach ($entry in $pageEntries) {
            if ($seenDownloadUrls.Add($entry.DownloadUrl)) {
                $allEntries.Add($entry) | Out-Null
                $newCount++
            }
        }

        if ($null -eq $detectedTotalPages) {
            $totalResults = Get-TotalResults -Html $pageHtml
            if ($null -ne $totalResults) {
                $detectedTotalPages = [int][Math]::Ceiling($totalResults / [double]$pageEntries.Count)
                Write-Host ("Discovered approximately {0} total textures across {1} page(s)." -f $totalResults, $detectedTotalPages) -ForegroundColor Cyan
            }
        }

        Write-Host ("Catalog page {0}: {1} texture(s), {2} new." -f $page, $pageEntries.Count, $newCount) -ForegroundColor DarkGray

        if ($page -gt 1 -and $newCount -eq 0) {
            Write-Host 'No new texture links were discovered on this page; stopping to avoid looping duplicates.' -ForegroundColor DarkGray
            break
        }

        if (($null -ne $detectedTotalPages) -and ($page -ge $detectedTotalPages)) {
            break
        }

        if ($ThrottleMs -gt 0) {
            Start-Sleep -Milliseconds $ThrottleMs
        }
    }

    if ($allEntries.Count -eq 0) {
        throw 'Texture discovery completed without finding any downloadable archives.'
    }

    $filteredEntries = @(
        $allEntries | Where-Object {
            $_.Name -like $NamePattern -or $_.FileName -like $NamePattern
        }
    )

    # --- Write manifest ---
    $manifest = [ordered]@{
        generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        sourceUrl = 'https://textures.pixel-furnace.com/'
        listingEndpoint = 'https://textures.pixel-furnace.com/fetchArticles.php'
        termsUrl = 'https://textures.pixel-furnace.com/terms'
        redistributionNotice = 'Pixel-Furnace allows use in commercial and non-commercial projects, but does not allow redistribution of the textures by themselves or as a collection.'
        totalDiscovered = $allEntries.Count
        selectedCount = $filteredEntries.Count
        namePattern = $NamePattern
        textures = @(
            $allEntries | Sort-Object Name | ForEach-Object {
                $maps = @{}
                if ($null -ne $_.Maps) {
                    foreach ($mk in $_.Maps.Keys) { $maps[$mk] = $_.Maps[$mk] }
                }
                [ordered]@{
                    name = $_.Name
                    fileName = $_.FileName
                    page = $_.Page
                    detailUrl = $_.DetailUrl
                    downloadUrl = $_.DownloadUrl
                    articleId = $_.ArticleId
                    resolution = $_.Resolution
                    fileSize = $_.FileSize
                    maps = $maps
                }
            }
        )
    }

    $manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $manifestPath -Encoding UTF8
    Write-Host "Wrote manifest: $manifestPath" -ForegroundColor Green

    if ($filteredEntries.Count -eq 0) {
        Write-Host "No textures matched NamePattern '$NamePattern'." -ForegroundColor Yellow
        return
    }

    # --- Display catalog ---
    Write-Host ''
    Write-Host ('  {0,-4} {1,-30} {2,-10} {3,-6}  {4}' -f '#', 'Name', 'Resolution', 'Size', 'Maps (A=Albedo N=Normal B=Bump S=Spec R=Rough M=Metal)') -ForegroundColor White
    Write-Host ('  ' + ('-' * 100)) -ForegroundColor DarkGray

    $sorted = @($filteredEntries | Sort-Object Name)
    $mapLetters = @(
        @('Albedo',    'A'),
        @('Normal',    'N'),
        @('Bump',      'B'),
        @('Specular',  'S'),
        @('Roughness', 'R'),
        @('Metal',     'M')
    )

    for ($i = 0; $i -lt $sorted.Count; $i++) {
        $entry = $sorted[$i]
        $num = $i + 1
        $res = if ($entry.Resolution) { $entry.Resolution } else { '?' }
        $sz  = if ($entry.FileSize)   { $entry.FileSize }   else { '?' }

        # Check if folder already exists
        $folderName = $entry.Name -replace '[\\/:*?"<>|]', '_'
        $folderPath = Join-Path $OutputDir $folderName
        $existsTag = if (Test-Path -LiteralPath $folderPath -PathType Container) { '*' } else { '' }

        $displayName = $entry.Name
        if ($displayName.Length -gt 28) { $displayName = $displayName.Substring(0, 28) }
        $displayName = $displayName + $existsTag

        $prefix = '  {0,-4} {1,-30} {2,-10} {3,-6}  ' -f $num, $displayName, $res, $sz
        Write-Host -NoNewline $prefix

        foreach ($ml in $mapLetters) {
            $mapName = $ml[0]
            $letter = $ml[1]
            $active = $false
            if ($null -ne $entry.Maps -and $entry.Maps.Contains($mapName)) {
                $active = $entry.Maps[$mapName]
            }
            if ($active) {
                Write-Host -NoNewline $letter -ForegroundColor Green
            } else {
                Write-Host -NoNewline $letter -ForegroundColor DarkGray
            }
            Write-Host -NoNewline ' '
        }
        Write-Host ''
    }

    Write-Host ''
    Write-Host '  * = already downloaded' -ForegroundColor DarkGray

    if ($ListOnly) {
        Write-Host ''
        Write-Host ("Discovered {0} total textures; {1} matched the current filter." -f $allEntries.Count, $filteredEntries.Count) -ForegroundColor Green
        return
    }

    # --- Interactive selection ---
    $selectedIndices = $null

    if ($All) {
        $selectedIndices = @(0..($sorted.Count - 1))
    }
    else {
        Write-Host ''
        Write-Host 'Enter texture numbers to download.' -ForegroundColor Cyan
        Write-Host 'Examples: 1,3,5  |  1-10  |  all  |  none  |  1-5,8,12-15' -ForegroundColor DarkGray
        $userInput = Read-Host 'Selection'
        $userInput = $userInput.Trim()

        if ([string]::IsNullOrWhiteSpace($userInput) -or $userInput -eq 'none') {
            Write-Host 'No textures selected.' -ForegroundColor Yellow
            return
        }

        if ($userInput -eq 'all') {
            $selectedIndices = @(0..($sorted.Count - 1))
        }
        else {
            $selectedIndices = [System.Collections.Generic.List[int]]::new()
            foreach ($part in ($userInput -split ',')) {
                $part = $part.Trim()
                if ($part -match '^\s*(\d+)\s*-\s*(\d+)\s*$') {
                    $rangeStart = [int]$Matches[1]
                    $rangeEnd = [int]$Matches[2]
                    for ($r = $rangeStart; $r -le $rangeEnd; $r++) {
                        if ($r -ge 1 -and $r -le $sorted.Count) {
                            $selectedIndices.Add($r - 1)
                        }
                    }
                }
                elseif ($part -match '^\d+$') {
                    $num = [int]$part
                    if ($num -ge 1 -and $num -le $sorted.Count) {
                        $selectedIndices.Add($num - 1)
                    }
                }
                else {
                    Write-Host "Ignoring unrecognized token: $part" -ForegroundColor Yellow
                }
            }
            $selectedIndices = @($selectedIndices | Sort-Object -Unique)
        }
    }

    if ($selectedIndices.Count -eq 0) {
        Write-Host 'No valid textures selected.' -ForegroundColor Yellow
        return
    }

    Write-Host ("{0} texture(s) selected for download." -f $selectedIndices.Count) -ForegroundColor Cyan
    Write-Host ''

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # --- Download and extract ---
    $downloadedCount = 0
    $skippedCount = 0
    $extractedCount = 0

    foreach ($idx in $selectedIndices) {
        $entry = $sorted[$idx]
        $folderName = $entry.Name -replace '[\\/:*?"<>|]', '_'
        $folderPath = Join-Path $OutputDir $folderName

        if ((Test-Path -LiteralPath $folderPath -PathType Container) -and -not $Force) {
            Write-Host ("  Skipping existing: {0}" -f $entry.Name) -ForegroundColor DarkGray
            $skippedCount++
            continue
        }

        $zipPath = Join-Path $OutputDir $entry.FileName
        $tempPath = "$zipPath.download"
        if (Test-Path -LiteralPath $tempPath) {
            Remove-Item -LiteralPath $tempPath -Force
        }

        # Download if ZIP doesn't exist or Force is set
        if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or $Force) {
            Write-Host ("  Downloading {0}..." -f $entry.Name) -ForegroundColor Cyan
            try {
                Invoke-FileDownload -Client $client -Uri $entry.DownloadUrl -DestinationPath $tempPath

                $fileInfo = Get-Item -LiteralPath $tempPath
                if ($fileInfo.Length -le 0) {
                    throw 'Downloaded file is empty.'
                }

                Move-Item -LiteralPath $tempPath -Destination $zipPath -Force
                $downloadedCount++
            }
            catch {
                if (Test-Path -LiteralPath $tempPath) {
                    Remove-Item -LiteralPath $tempPath -Force
                }
                Write-Host ("  FAILED to download {0}: {1}" -f $entry.Name, $_.Exception.Message) -ForegroundColor Red
                continue
            }
        }
        else {
            Write-Host ("  Using cached ZIP: {0}" -f $entry.FileName) -ForegroundColor DarkGray
        }

        # Extract ZIP into named folder
        Write-Host ("  Extracting to {0}\..." -f $folderName) -ForegroundColor DarkGray
        try {
            if (Test-Path -LiteralPath $folderPath -PathType Container) {
                Remove-Item -LiteralPath $folderPath -Recurse -Force
            }
            New-Item -ItemType Directory -Path $folderPath -Force | Out-Null
            [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $folderPath)

            # Clean up ZIP after successful extraction
            Remove-Item -LiteralPath $zipPath -Force
            $extractedCount++
            Write-Host ("  Extracted {0}" -f $entry.Name) -ForegroundColor Green
        }
        catch {
            Write-Host ("  FAILED to extract {0}: {1}" -f $entry.Name, $_.Exception.Message) -ForegroundColor Red
        }
    }

    Write-Host ''
    Write-Host ("Finished. Downloaded {0}, extracted {1}, skipped {2}." -f $downloadedCount, $extractedCount, $skippedCount) -ForegroundColor Green
}
finally {
    if ($null -ne $client) {
        $client.Dispose()
    }
    if ($null -ne $handler) {
        $handler.Dispose()
    }
}