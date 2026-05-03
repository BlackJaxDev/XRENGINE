<#
.SYNOPSIS
    Downloads FreePBR "bl" texture archives.

.DESCRIPTION
    Enumerates FreePBR shop products through the site's public WooCommerce Store
    API, displays the available free material products, lets the user choose which
    ones to download, then resolves each selected product page and downloads the
    "bl" ZIP form. The "bl" archives are the generic Blender/Maya/3DS Max/C4D
    style texture sets, which are the right choice when XRENGINE is not consuming
    Unity or Unreal-specific exports.

    FreePBR serves the ZIP files through product-page POST forms instead of stable
    direct URLs, so the script stores catalog metadata in the manifest and resolves
    download form fields only when a material is selected.

.PARAMETER OutputDir
    Directory where texture folders and the generated manifest are written.
    Defaults to Build\CommonAssets\Textures\Samples\FreePBR under the repo root.

.PARAMETER NamePattern
    Wildcard filter applied to the discovered product name, slug, or category.
    Example: -NamePattern '*Brick*'

.PARAMETER MaxPages
    Safety cap for Store API catalog pagination. FreePBR currently fits in fewer
    than 10 pages at the default page size.

.PARAMETER PageSize
    Number of products to request per Store API page. FreePBR's API caps this at
    100; the default is 100.

.PARAMETER ThrottleMs
    Delay between catalog/download requests in milliseconds.

.PARAMETER Force
    Re-download and re-extract textures even when the folder already exists locally.

.PARAMETER ListOnly
    Discover products and write the manifest without downloading archives.

.PARAMETER All
    Download all filtered products without interactive selection.

.PARAMETER IncludePaid
    Include paid products in the catalog. By default, only $0.00 products are shown.

.EXAMPLE
    .\Tools\Get-FreePbrTextures.ps1

.EXAMPLE
    .\Tools\Get-FreePbrTextures.ps1 -NamePattern '*Brick*' -ListOnly

.EXAMPLE
    .\Tools\Get-FreePbrTextures.ps1 -All
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$NamePattern = '*',
    [ValidateRange(1, 100)]
    [int]$MaxPages = 16,
    [ValidateRange(1, 100)]
    [int]$PageSize = 100,
    [ValidateRange(0, 10000)]
    [int]$ThrottleMs = 250,
    [switch]$Force,
    [switch]$ListOnly,
    [switch]$All,
    [switch]$IncludePaid
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot 'Build\CommonAssets\Textures\Samples\FreePBR'
}

$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$manifestPath = Join-Path $OutputDir 'freepbr-textures.index.json'

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Add-Type -AssemblyName System.Net.Http

$siteBaseUri = [Uri]'https://freepbr.com/'
$sourceUrl = 'https://freepbr.com/shop/'
$catalogEndpoint = 'https://freepbr.com/wp-json/wc/store/v1/products'
$regexOptions = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline

function Get-PropertyValue {
    param(
        [AllowNull()]
        [object]$Object,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function ConvertTo-PlainText {
    param(
        [AllowNull()]
        [string]$Html
    )

    if ([string]::IsNullOrWhiteSpace($Html)) {
        return ''
    }

    $text = $Html -replace '(?is)<br\s*/?>', ' '
    $text = $text -replace '(?is)<[^>]+>', ' '
    $text = [System.Net.WebUtility]::HtmlDecode($text)
    $text = $text -replace '\s+', ' '
    return $text.Trim()
}

function Get-HeaderFirstValue {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Headers,
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    try {
        $values = $Headers.GetValues($Name)
        foreach ($value in $values) {
            return [string]$value
        }
    }
    catch {
        return $null
    }

    return $null
}

function New-HttpClient {
    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.AutomaticDecompression = [System.Net.DecompressionMethods]::GZip -bor [System.Net.DecompressionMethods]::Deflate
    $handler.UseCookies = $true
    $handler.AllowAutoRedirect = $true

    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(15)
    $null = $client.DefaultRequestHeaders.UserAgent.ParseAdd('XRENGINE FreePBR Downloader')
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
        [string]$Uri
    )

    $response = $Client.GetAsync($Uri).GetAwaiter().GetResult()
    try {
        $response.EnsureSuccessStatusCode() | Out-Null
        return $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    }
    finally {
        $response.Dispose()
    }
}

function Get-StoreProductsPage {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)]
        [int]$Page,
        [Parameter(Mandatory = $true)]
        [int]$RequestedPageSize
    )

    $pageUri = '{0}?per_page={1}&page={2}&orderby=date&order=desc' -f $catalogEndpoint, $RequestedPageSize, $Page
    $response = $Client.GetAsync($pageUri).GetAwaiter().GetResult()
    try {
        $response.EnsureSuccessStatusCode() | Out-Null
        $total = Get-HeaderFirstValue -Headers $response.Headers -Name 'X-WP-Total'
        $totalPages = Get-HeaderFirstValue -Headers $response.Headers -Name 'X-WP-TotalPages'
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $products = @()
        if (-not [string]::IsNullOrWhiteSpace($content)) {
            $parsedProducts = ConvertFrom-Json -InputObject $content
            if ($null -ne $parsedProducts) {
                $products = @($parsedProducts)
            }
        }

        return [pscustomobject]@{
            Uri = $pageUri
            Products = $products
            Total = if ([string]::IsNullOrWhiteSpace($total)) { $null } else { [int]$total }
            TotalPages = if ([string]::IsNullOrWhiteSpace($totalPages)) { $null } else { [int]$totalPages }
        }
    }
    finally {
        $response.Dispose()
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

function Resolve-AbsoluteUri {
    param(
        [Parameter(Mandatory = $true)]
        [Uri]$BaseUri,
        [Parameter(Mandatory = $true)]
        [string]$UriText
    )

    if ([string]::IsNullOrWhiteSpace($UriText)) {
        return $BaseUri.AbsoluteUri
    }

    $resolved = [Uri]::new($BaseUri, $UriText)
    return $resolved.AbsoluteUri
}

function ConvertTo-SafeFileName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $safe = $Name -replace '[\\/:*?"<>|]', '_'
    $safe = $safe -replace '\s+', ' '
    $safe = $safe.Trim()
    $safe = $safe.TrimEnd('.', ' ')
    if ([string]::IsNullOrWhiteSpace($safe)) {
        return 'FreePBRMaterial'
    }

    return $safe
}

function Get-FirstResolution {
    param(
        [AllowNull()]
        [string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $null
    }

    $match = [regex]::Match($Text, '(?<w>\d{3,5})\s*[xX\xD7]\s*(?<h>\d{3,5})')
    if ($match.Success) {
        return ('{0}x{1}' -f $match.Groups['w'].Value, $match.Groups['h'].Value)
    }

    return $null
}

function ConvertFrom-StoreProduct {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Product,
        [Parameter(Mandatory = $true)]
        [int]$Page
    )

    $id = Get-PropertyValue -Object $Product -Name 'id'
    $name = [string](Get-PropertyValue -Object $Product -Name 'name')
    $slug = [string](Get-PropertyValue -Object $Product -Name 'slug')
    $detailUrl = [string](Get-PropertyValue -Object $Product -Name 'permalink')
    $prices = Get-PropertyValue -Object $Product -Name 'prices'
    $rawPrice = [string](Get-PropertyValue -Object $prices -Name 'price')
    $currencyCode = [string](Get-PropertyValue -Object $prices -Name 'currency_code')
    $currencySymbol = [string](Get-PropertyValue -Object $prices -Name 'currency_symbol')
    $currencyMinorUnit = Get-PropertyValue -Object $prices -Name 'currency_minor_unit'

    $categoryNames = [System.Collections.Generic.List[string]]::new()
    foreach ($category in @(Get-PropertyValue -Object $Product -Name 'categories')) {
        $categoryName = [string](Get-PropertyValue -Object $category -Name 'name')
        if (-not [string]::IsNullOrWhiteSpace($categoryName)) {
            $categoryNames.Add([System.Net.WebUtility]::HtmlDecode($categoryName)) | Out-Null
        }
    }

    $previewUrl = $null
    $images = @(Get-PropertyValue -Object $Product -Name 'images')
    if ($images.Count -gt 0) {
        $previewUrl = [string](Get-PropertyValue -Object $images[0] -Name 'src')
    }

    $description = ConvertTo-PlainText -Html ([string](Get-PropertyValue -Object $Product -Name 'short_description'))
    if ([string]::IsNullOrWhiteSpace($description)) {
        $description = ConvertTo-PlainText -Html ([string](Get-PropertyValue -Object $Product -Name 'description'))
    }

    $displayPrice = $rawPrice
    $isFree = [string]::Equals($rawPrice, '0', [System.StringComparison]::Ordinal)
    if (-not [string]::IsNullOrWhiteSpace($rawPrice) -and $null -ne $currencyMinorUnit) {
        $minorUnit = [int]$currencyMinorUnit
        $integerPrice = 0L
        if ([long]::TryParse($rawPrice, [ref]$integerPrice)) {
            $divisor = [Math]::Pow(10, $minorUnit)
            $displayPrice = ('{0}{1:N' + $minorUnit + '}') -f $currencySymbol, ($integerPrice / $divisor)
        }
    }

    return [pscustomobject]@{
        Page = $Page
        Id = [int]$id
        Name = [System.Net.WebUtility]::HtmlDecode($name)
        Slug = $slug
        DetailUrl = $detailUrl
        Category = ($categoryNames -join ', ')
        Categories = @($categoryNames)
        Price = $displayPrice
        PriceRaw = $rawPrice
        CurrencyCode = $currencyCode
        IsFree = $isFree
        PreviewUrl = $previewUrl
        Resolution = Get-FirstResolution -Text $description
        Description = $description
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

function Get-HiddenInputValues {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Html
    )

    $fields = @{}
    foreach ($inputMatch in [regex]::Matches($Html, '(?is)<input\b(?<attrs>[^>]*?)>')) {
        $attrs = $inputMatch.Groups['attrs'].Value
        $name = Get-AttributeValue -Attributes $attrs -Name 'name'
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }

        $value = Get-AttributeValue -Attributes $attrs -Name 'value'
        if ($null -eq $value) {
            $value = ''
        }

        $fields[$name] = $value
    }

    return $fields
}

function Test-BlArchiveFileName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FileName
    )

    return $FileName -match '(?i)(^|[-_])bl(?:[-_][a-z0-9]+)?\.zip$'
}

function Get-FreePbrBlDownload {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)]
        [object]$Product
    )

    $html = Get-StringResponse -Client $Client -Uri $Product.DetailUrl
    $forms = [regex]::Matches($html, '(?is)<form\b(?<attrs>[^>]*)>(?<content>.*?)</form>')
    foreach ($form in $forms) {
        $formHtml = $form.Groups['content'].Value
        if ($formHtml -notmatch 'somdn_download_multi_single') {
            continue
        }

        $fileName = $null
        foreach ($linkMatch in [regex]::Matches($formHtml, '(?is)<a\b[^>]*>(?<text>.*?)</a>')) {
            $candidate = ConvertTo-PlainText -Html $linkMatch.Groups['text'].Value
            if ($candidate -match '(?i)\.zip$' -and (Test-BlArchiveFileName -FileName $candidate)) {
                $fileName = $candidate
                break
            }
        }

        if ([string]::IsNullOrWhiteSpace($fileName)) {
            $plainForm = ConvertTo-PlainText -Html $formHtml
            foreach ($zipMatch in [regex]::Matches($plainForm, '(?i)[a-z0-9][a-z0-9._()+-]*\.zip')) {
                $candidate = $zipMatch.Value
                if (Test-BlArchiveFileName -FileName $candidate) {
                    $fileName = $candidate
                    break
                }
            }
        }

        if ([string]::IsNullOrWhiteSpace($fileName)) {
            continue
        }

        $fields = Get-HiddenInputValues -Html $formHtml
        if (-not $fields.ContainsKey('action')) {
            $fields['action'] = 'somdn_download_multi_single'
        }
        if (-not $fields.ContainsKey('somdn_product')) {
            $fields['somdn_product'] = [string]$Product.Id
        }

        foreach ($requiredField in @('action', 'somdn_product', 'somdn_productfile', 'somdn_download_key')) {
            if (-not $fields.ContainsKey($requiredField) -or [string]::IsNullOrWhiteSpace([string]$fields[$requiredField])) {
                throw ("FreePBR download form for '{0}' is missing required field '{1}'." -f $Product.Name, $requiredField)
            }
        }

        $formAction = Get-AttributeValue -Attributes $form.Groups['attrs'].Value -Name 'action'
        if ([string]::IsNullOrWhiteSpace($formAction)) {
            $formAction = $Product.DetailUrl
        }

        return [pscustomobject]@{
            FileName = $fileName
            PostUrl = Resolve-AbsoluteUri -BaseUri $siteBaseUri -UriText $formAction
            Fields = $fields
        }
    }

    return $null
}

function Invoke-FormFileDownload {
    param(
        [Parameter(Mandatory = $true)]
        [System.Net.Http.HttpClient]$Client,
        [Parameter(Mandatory = $true)]
        [string]$Uri,
        [Parameter(Mandatory = $true)]
        [hashtable]$Fields,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    $formContent = New-FormUrlEncodedContent -Fields $Fields
    try {
        $response = $Client.PostAsync($Uri, $formContent).GetAwaiter().GetResult()
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
    finally {
        $formContent.Dispose()
    }
}

function Test-ZipFileSignature {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 4) {
            return $false
        }

        $bytes = [byte[]]::new(4)
        $read = $stream.Read($bytes, 0, $bytes.Length)
        return $read -eq 4 -and $bytes[0] -eq 0x50 -and $bytes[1] -eq 0x4B
    }
    finally {
        $stream.Dispose()
    }
}

function Get-SelectionIndices {
    param(
        [Parameter(Mandatory = $true)]
        [int]$Count,
        [switch]$AllSelected
    )

    if ($Count -le 0) {
        return @()
    }

    if ($AllSelected) {
        $allIndices = [System.Collections.Generic.List[int]]::new()
        for ($i = 0; $i -lt $Count; $i++) {
            $allIndices.Add($i) | Out-Null
        }
        return @($allIndices)
    }

    Write-Host ''
    Write-Host 'Enter material numbers to download.' -ForegroundColor Cyan
    Write-Host 'Examples: 1,3,5  |  1-10  |  all  |  none  |  1-5,8,12-15' -ForegroundColor DarkGray
    $userInput = Read-Host 'Selection'
    $userInput = $userInput.Trim()

    if ([string]::IsNullOrWhiteSpace($userInput) -or $userInput -eq 'none') {
        return @()
    }

    if ($userInput -eq 'all') {
        return @(Get-SelectionIndices -Count $Count -AllSelected)
    }

    $indices = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($part in ($userInput -split ',')) {
        $part = $part.Trim()
        if ($part -match '^\s*(\d+)\s*-\s*(\d+)\s*$') {
            $rangeStart = [int]$Matches[1]
            $rangeEnd = [int]$Matches[2]
            for ($r = $rangeStart; $r -le $rangeEnd; $r++) {
                if ($r -ge 1 -and $r -le $Count) {
                    $indices.Add($r - 1) | Out-Null
                }
            }
        }
        elseif ($part -match '^\d+$') {
            $num = [int]$part
            if ($num -ge 1 -and $num -le $Count) {
                $indices.Add($num - 1) | Out-Null
            }
        }
        else {
            Write-Host "Ignoring unrecognized token: $part" -ForegroundColor Yellow
        }
    }

    return @($indices | Sort-Object)
}

function Write-CatalogManifest {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Products,
        [Parameter(Mandatory = $true)]
        [object[]]$FilteredProducts
    )

    $manifest = [ordered]@{
        generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
        sourceUrl = $sourceUrl
        catalogEndpoint = $catalogEndpoint
        downloadVariant = 'bl'
        freeOnly = [bool](-not $IncludePaid)
        totalDiscovered = $Products.Count
        selectedCount = $FilteredProducts.Count
        namePattern = $NamePattern
        note = 'FreePBR serves ZIPs through product-page POST forms; run the script to resolve selected BL downloads.'
        products = @(
            $Products | Sort-Object Name | ForEach-Object {
                [ordered]@{
                    id = $_.Id
                    name = $_.Name
                    slug = $_.Slug
                    page = $_.Page
                    detailUrl = $_.DetailUrl
                    categories = @($_.Categories)
                    price = $_.Price
                    isFree = $_.IsFree
                    resolution = $_.Resolution
                    previewUrl = $_.PreviewUrl
                }
            }
        )
    }

    $manifest | ConvertTo-Json -Depth 8 | Set-Content -Path $manifestPath -Encoding UTF8
}

$http = New-HttpClient
$client = $http.Client
$handler = $http.Handler

try {
    Write-Host '=== FreePBR BL Texture Downloader ===' -ForegroundColor Cyan
    Write-Host "Output directory: $OutputDir" -ForegroundColor Cyan
    Write-Host 'Variant: bl ZIP files for generic/Blender-style PBR texture sets.' -ForegroundColor Cyan
    Write-Host 'License note: review FreePBR terms before redistributing downloaded assets.' -ForegroundColor Yellow

    $seenProductIds = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $allEntries = [System.Collections.Generic.List[object]]::new()
    $detectedTotalPages = $null
    $detectedTotalProducts = $null

    for ($page = 1; $page -le $MaxPages; $page++) {
        $pageResult = Get-StoreProductsPage -Client $client -Page $page -RequestedPageSize $PageSize
        if ($null -eq $detectedTotalPages -and $null -ne $pageResult.TotalPages) {
            $detectedTotalPages = $pageResult.TotalPages
            $detectedTotalProducts = $pageResult.Total
            Write-Host ("Discovered {0} FreePBR product(s) across {1} Store API page(s)." -f $detectedTotalProducts, $detectedTotalPages) -ForegroundColor Cyan
        }

        $products = @($pageResult.Products)
        if ($products.Count -eq 0) {
            if ($page -eq 1) {
                throw 'FreePBR catalog discovery returned no products on the first page.'
            }

            Write-Host "Catalog page $page returned no products; stopping." -ForegroundColor DarkGray
            break
        }

        $newCount = 0
        $skippedPaidCount = 0
        foreach ($product in $products) {
            $entry = ConvertFrom-StoreProduct -Product $product -Page $page
            if (-not $IncludePaid -and -not $entry.IsFree) {
                $skippedPaidCount++
                continue
            }

            if ($seenProductIds.Add([string]$entry.Id)) {
                $allEntries.Add($entry) | Out-Null
                $newCount++
            }
        }

        if ($IncludePaid) {
            Write-Host ("Catalog page {0}: {1} product(s), {2} new." -f $page, $products.Count, $newCount) -ForegroundColor DarkGray
        }
        else {
            Write-Host ("Catalog page {0}: {1} product(s), {2} free/new, {3} paid skipped." -f $page, $products.Count, $newCount, $skippedPaidCount) -ForegroundColor DarkGray
        }

        if ($page -gt 1 -and $newCount -eq 0) {
            Write-Host 'No new product links were discovered on this page; stopping to avoid looping duplicates.' -ForegroundColor DarkGray
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
        throw 'FreePBR catalog discovery completed without finding any products.'
    }

    $filteredEntries = @(
        $allEntries | Where-Object {
            $_.Name -like $NamePattern -or
            $_.Slug -like $NamePattern -or
            $_.Category -like $NamePattern
        }
    )

    Write-CatalogManifest -Products @($allEntries) -FilteredProducts @($filteredEntries)
    Write-Host "Wrote manifest: $manifestPath" -ForegroundColor Green

    if ($filteredEntries.Count -eq 0) {
        Write-Host "No FreePBR products matched NamePattern '$NamePattern'." -ForegroundColor Yellow
        return
    }

    Write-Host ''
    Write-Host ('  {0,-4} {1,-42} {2,-22} {3,-9} {4}' -f '#', 'Name', 'Category', 'Resolution', 'Product') -ForegroundColor White
    Write-Host ('  ' + ('-' * 104)) -ForegroundColor DarkGray

    $sorted = @($filteredEntries | Sort-Object Name)
    for ($i = 0; $i -lt $sorted.Count; $i++) {
        $entry = $sorted[$i]
        $num = $i + 1
        $resolution = if ($entry.Resolution) { $entry.Resolution } else { '?' }
        $category = if ($entry.Category) { $entry.Category } else { '?' }
        if ($category.Length -gt 20) { $category = $category.Substring(0, 20) }

        $folderName = ConvertTo-SafeFileName -Name $entry.Name
        $folderPath = Join-Path $OutputDir $folderName
        $existsTag = if (Test-Path -LiteralPath $folderPath -PathType Container) { '*' } else { '' }

        $displayName = $entry.Name
        if ($displayName.Length -gt 40) { $displayName = $displayName.Substring(0, 40) }
        $displayName = $displayName + $existsTag

        Write-Host ('  {0,-4} {1,-42} {2,-22} {3,-9} {4}' -f $num, $displayName, $category, $resolution, $entry.Id)
    }

    Write-Host ''
    Write-Host '  * = already downloaded' -ForegroundColor DarkGray

    if ($ListOnly) {
        Write-Host ''
        Write-Host ("Discovered {0} product(s); {1} matched the current filter." -f $allEntries.Count, $filteredEntries.Count) -ForegroundColor Green
        return
    }

    $selectedIndices = @(Get-SelectionIndices -Count $sorted.Count -AllSelected:$All)
    if ($selectedIndices.Count -eq 0) {
        Write-Host 'No valid materials selected.' -ForegroundColor Yellow
        return
    }

    Write-Host ("{0} material(s) selected for download." -f $selectedIndices.Count) -ForegroundColor Cyan
    Write-Host ''

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $downloadedCount = 0
    $skippedCount = 0
    $extractedCount = 0
    $noBlCount = 0
    $failedCount = 0

    foreach ($idx in $selectedIndices) {
        $entry = $sorted[$idx]
        $folderName = ConvertTo-SafeFileName -Name $entry.Name
        $folderPath = Join-Path $OutputDir $folderName

        if ((Test-Path -LiteralPath $folderPath -PathType Container) -and -not $Force) {
            Write-Host ("  Skipping existing: {0}" -f $entry.Name) -ForegroundColor DarkGray
            $skippedCount++
            continue
        }

        try {
            Write-Host ("  Resolving BL download: {0}" -f $entry.Name) -ForegroundColor Cyan
            $download = Get-FreePbrBlDownload -Client $client -Product $entry
            if ($null -eq $download) {
                Write-Host ("  No BL ZIP form found for {0}; skipping." -f $entry.Name) -ForegroundColor Yellow
                $noBlCount++
                continue
            }

            $safeZipName = ConvertTo-SafeFileName -Name $download.FileName
            $zipPath = Join-Path $OutputDir $safeZipName
            $tempPath = "$zipPath.download"
            if (Test-Path -LiteralPath $tempPath) {
                Remove-Item -LiteralPath $tempPath -Force
            }

            if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or $Force) {
                Write-Host ("  Downloading {0}..." -f $download.FileName) -ForegroundColor Cyan
                Invoke-FormFileDownload -Client $client -Uri $download.PostUrl -Fields $download.Fields -DestinationPath $tempPath

                $fileInfo = Get-Item -LiteralPath $tempPath
                if ($fileInfo.Length -le 0) {
                    throw 'Downloaded file is empty.'
                }

                if (-not (Test-ZipFileSignature -Path $tempPath)) {
                    throw 'Downloaded file does not look like a ZIP archive.'
                }

                Move-Item -LiteralPath $tempPath -Destination $zipPath -Force
                $downloadedCount++
            }
            else {
                Write-Host ("  Using cached ZIP: {0}" -f $safeZipName) -ForegroundColor DarkGray
            }

            Write-Host ("  Extracting to {0}\..." -f $folderName) -ForegroundColor DarkGray
            if (Test-Path -LiteralPath $folderPath -PathType Container) {
                Remove-Item -LiteralPath $folderPath -Recurse -Force
            }
            New-Item -ItemType Directory -Path $folderPath -Force | Out-Null
            [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $folderPath)

            Remove-Item -LiteralPath $zipPath -Force
            $extractedCount++
            Write-Host ("  Extracted {0}" -f $entry.Name) -ForegroundColor Green
        }
        catch {
            $failedCount++
            if ((Get-Variable -Name tempPath -Scope Local -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $tempPath)) {
                Remove-Item -LiteralPath $tempPath -Force
            }
            Write-Host ("  FAILED {0}: {1}" -f $entry.Name, $_.Exception.Message) -ForegroundColor Red
        }

        if ($ThrottleMs -gt 0) {
            Start-Sleep -Milliseconds $ThrottleMs
        }
    }

    Write-Host ''
    Write-Host ("Finished. Downloaded {0}, extracted {1}, skipped {2}, no BL ZIP {3}, failed {4}." -f $downloadedCount, $extractedCount, $skippedCount, $noBlCount, $failedCount) -ForegroundColor Green
}
finally {
    if ($null -ne $client) {
        $client.Dispose()
    }
    if ($null -ne $handler) {
        $handler.Dispose()
    }
}
