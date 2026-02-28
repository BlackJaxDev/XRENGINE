<#
.SYNOPSIS
    Builds the XRENGINE solution and generates a categorised warning report.

.DESCRIPTION
    Runs 'dotnet build' on XRENGINE.sln, parses MSBuild warning output, and
    writes a Markdown report to docs/work/audit/warnings.md grouped by project
    and warning code with file/line references.

.PARAMETER Root
    Repository root. Defaults to current directory.

.PARAMETER OutFile
    Output Markdown path. Defaults to docs/work/audit/warnings.md under Root.

.PARAMETER NoBuild
    Skip the build step and reuse the most recent build log at Build/Logs/warnings-build.log.

.PARAMETER Configuration
    Build configuration (Debug or Release). Defaults to Debug.

.PARAMETER SkipDocLookup
    Skip online Microsoft Learn lookups for warning descriptions. Uses only
    the built-in fallback table and any previously cached entries. Useful for
    CI, air-gapped, or offline environments.

.EXAMPLE
    pwsh Tools/Reports/Find-BuildWarnings.ps1
    pwsh Tools/Reports/Find-BuildWarnings.ps1 -NoBuild
    pwsh Tools/Reports/Find-BuildWarnings.ps1 -Configuration Release
    pwsh Tools/Reports/Find-BuildWarnings.ps1 -SkipDocLookup
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Root = (Resolve-Path ".").Path,

    [Parameter(Mandatory = $false)]
    [string]$OutFile = "",

    [Parameter(Mandatory = $false)]
    [switch]$NoBuild,

    [Parameter(Mandatory = $false)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [Parameter(Mandatory = $false)]
    [switch]$SkipDocLookup
)

$ErrorActionPreference = "Stop"

# Always resolve Root to an absolute path so path-stripping works regardless of
# whether the caller passed a relative path like '.' or an absolute one.
$Root = (Resolve-Path $Root).Path

if (-not $OutFile) {
    $OutFile = Join-Path $Root "docs\work\audit\warnings.md"
}

$logDir  = Join-Path $Root "Build\Logs"
$logFile = Join-Path $logDir "warnings-build.log"
$cacheFile = Join-Path $logDir "warning-docs-cache.json"

# ── Build (unless -NoBuild) ────────────────────────────────────────────────
if (-not $NoBuild) {
    New-Item -ItemType Directory -Force -Path $logDir | Out-Null

    $slnPath = Join-Path $Root "XRENGINE.sln"
    if (-not (Test-Path $slnPath)) {
        Write-Error "Solution not found at $slnPath"
        return
    }

    Write-Host "Building $slnPath ($Configuration) ..." -ForegroundColor Cyan
    $buildArgs = @(
        "build", $slnPath,
        "--configuration", $Configuration,
        "/property:GenerateFullPaths=true",
        "/consoleloggerparameters:NoSummary",
        "-v", "quiet",
        "/p:TreatWarningsAsErrors=false"
    )

    # Run build, capture all output (stdout + stderr) to log, ignore exit code
    & dotnet @buildArgs 2>&1 | Out-File -FilePath $logFile -Encoding utf8
    Write-Host "Build complete. Log saved to $logFile" -ForegroundColor Green
}
else {
    if (-not (Test-Path $logFile)) {
        Write-Error "No build log found at $logFile. Run without -NoBuild first."
        return
    }
    Write-Host "Reusing existing build log: $logFile" -ForegroundColor Yellow
}

# ── Parse warnings ─────────────────────────────────────────────────────────
# Two MSBuild warning formats:
#   File-level:    <file>(<line>,<col>): warning <CODE>: <message> [<project>]
#   Project-level: <file> : warning <CODE>: <message> [<project>]
# The project path is always the LAST [...] on the line; messages may contain brackets.
$fileWarningRegex    = [regex]'^(.+?)\((\d+),(\d+)\)\s*:\s*warning\s+(\w+)\s*:\s*(.+)\s+\[([^\[\]]+)\]\s*$'
$projectWarningRegex = [regex]'^(.+?)\s*:\s*warning\s+(\w+)\s*:\s*(.+)\s+\[([^\[\]]+)\]\s*$'

$warnings = [System.Collections.Generic.List[object]]::new()
$rootNorm = $Root.TrimEnd('\', '/') + '\'
# Also keep a forward-slash variant for MSBuild paths (which use /)
$rootNormFwd = $rootNorm.Replace('\', '/')

foreach ($rawLine in [System.IO.File]::ReadLines($logFile)) {
    # File-level warning (has line,col)
    $m = $fileWarningRegex.Match($rawLine)
    if ($m.Success) {
        $filePath    = $m.Groups[1].Value.Trim()
        $lineNum     = [int]$m.Groups[2].Value
        $colNum      = [int]$m.Groups[3].Value
        $code        = $m.Groups[4].Value.Trim()
        $message     = $m.Groups[5].Value.Trim()
        $projectPath = $m.Groups[6].Value.Trim()

        # Derive project name from the .csproj path (or .sln if that's what's in brackets)
        $projectName = if ($projectPath.EndsWith('.csproj')) {
            [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
        }
        elseif ($filePath.EndsWith('.csproj')) {
            [System.IO.Path]::GetFileNameWithoutExtension($filePath)
        }
        else {
            [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
        }

        # Make the file path relative to repo root
        $relFile = $filePath.Replace('\', '/')
        if ($relFile.StartsWith($rootNormFwd, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relFile = $relFile.Substring($rootNormFwd.Length)
        }

        $warnings.Add([PSCustomObject]@{
            Project     = $projectName
            Code        = $code
            Message     = $message
            File        = $relFile
            Line        = $lineNum
            Column      = $colNum
        })
        continue
    }

    # Project-level warning (no line,col)
    $m = $projectWarningRegex.Match($rawLine)
    if ($m.Success) {
        $filePath    = $m.Groups[1].Value.Trim()
        $code        = $m.Groups[2].Value.Trim()
        $message     = $m.Groups[3].Value.Trim()
        $projectPath = $m.Groups[4].Value.Trim()

        # For project-level warnings, the "file" is often the .csproj itself
        $projectName = if ($filePath.EndsWith('.csproj')) {
            [System.IO.Path]::GetFileNameWithoutExtension($filePath)
        }
        elseif ($projectPath.EndsWith('.csproj')) {
            [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
        }
        else {
            [System.IO.Path]::GetFileNameWithoutExtension($projectPath)
        }

        $relFile = $filePath.Replace('\', '/')
        if ($relFile.StartsWith($rootNormFwd, [System.StringComparison]::OrdinalIgnoreCase)) {
            $relFile = $relFile.Substring($rootNormFwd.Length)
        }

        $warnings.Add([PSCustomObject]@{
            Project     = $projectName
            Code        = $code
            Message     = $message
            File        = $relFile
            Line        = 0
            Column      = 0
        })
        continue
    }
}

Write-Host "Parsed $($warnings.Count) raw warning(s)." -ForegroundColor Cyan

# ── Deduplicate ─────────────────────────────────────────────────────────────
# MSBuild can emit the same warning twice (multi-target, incremental rebuild, etc.)
$seen = [System.Collections.Generic.HashSet[string]]::new()
$unique = [System.Collections.Generic.List[object]]::new()

foreach ($w in $warnings) {
    $key = "$($w.Project)|$($w.Code)|$($w.File)|$($w.Line)|$($w.Column)"
    if ($seen.Add($key)) {
        $unique.Add($w)
    }
}

$warnings = $unique
Write-Host "After dedup: $($warnings.Count) unique warning(s)." -ForegroundColor Cyan

# ── Categorise ──────────────────────────────────────────────────────────────
# Dynamic warning metadata lookup (with local fallback and cache)
function Get-DotNetDocView {
    try {
        $versionText = (& dotnet --version 2>$null | Select-Object -First 1)
        if (-not $versionText) {
            return $null
        }

        $match = [regex]::Match($versionText.Trim(), '^(\d+)\.(\d+)')
        if (-not $match.Success) {
            return $null
        }

        return "net-$($match.Groups[1].Value).$($match.Groups[2].Value)"
    }
    catch {
        return $null
    }
}

function Get-WarningDocCandidates([string]$code, [string]$docView) {
    $upper = $code.ToUpperInvariant()
    $lower = $upper.ToLowerInvariant()
    $urls = [System.Collections.Generic.List[string]]::new()

    # Build ordered list of candidate base URLs per warning family.
    # CS warnings live at two possible paths on Microsoft Learn:
    #   - New:    /dotnet/csharp/language-reference/compiler-messages/csXXXX
    #   - Legacy: /dotnet/csharp/misc/csXXXX
    # We try the new path first; many older warnings only exist at the legacy path.
    $baseUrls = [System.Collections.Generic.List[string]]::new()
    if ($upper -match '^CS\d{4}$') {
        $baseUrls.Add("https://learn.microsoft.com/dotnet/csharp/language-reference/compiler-messages/$lower")
        $baseUrls.Add("https://learn.microsoft.com/dotnet/csharp/misc/$lower")
    }
    elseif ($upper -match '^CA\d{4}$') {
        $baseUrls.Add("https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/$lower")
    }
    elseif ($upper -match '^IL\d{4}$') {
        $baseUrls.Add("https://learn.microsoft.com/dotnet/core/deploying/trimming/trim-warnings/$lower")
    }
    elseif ($upper -match '^SYSLIB\d{4}$') {
        $baseUrls.Add("https://learn.microsoft.com/dotnet/fundamentals/syslib-diagnostics/$lower")
    }
    elseif ($upper -match '^NU\d{4}$') {
        $baseUrls.Add("https://learn.microsoft.com/nuget/reference/errors-and-warnings/$lower")
    }

    foreach ($baseUrl in $baseUrls) {
        if ($docView) {
            $urls.Add("$baseUrl`?view=$docView")
        }
        $urls.Add($baseUrl)
    }

    $searchQuery = [System.Uri]::EscapeDataString("$upper warning .NET")
    $urls.Add("https://learn.microsoft.com/search/?terms=$searchQuery")

    return $urls
}

function Get-PageMetadata([string]$url) {
    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -MaximumRedirection 5 -TimeoutSec 10
        $content = [string]$response.Content
        if (-not $content) {
            return $null
        }

        $title = ""
        $titleMatch = [regex]::Match($content, '<title>(.*?)</title>', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if ($titleMatch.Success) {
            $title = [System.Net.WebUtility]::HtmlDecode($titleMatch.Groups[1].Value).Trim()
            $title = ($title -replace '\s*-\s*.*$', '').Trim()
        }

        $description = ""
        $descriptionPatterns = @(
            '<meta\s+name=["'']description["'']\s+content=["'']([^"'']+)["'']',
            '<meta\s+content=["'']([^"'']+)["'']\s+name=["'']description["'']',
            '<meta\s+property=["'']og:description["'']\s+content=["'']([^"'']+)["'']'
        )

        foreach ($pattern in $descriptionPatterns) {
            $descMatch = [regex]::Match($content, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($descMatch.Success) {
                $description = [System.Net.WebUtility]::HtmlDecode($descMatch.Groups[1].Value).Trim()
                break
            }
        }

        if (-not $title -and -not $description) {
            return $null
        }

        $finalUrl = $url
        if ($response.BaseResponse -and $response.BaseResponse.ResponseUri) {
            $finalUrl = $response.BaseResponse.ResponseUri.AbsoluteUri
        }

        $isSearchPage = $false
        if ($finalUrl -match '/search/' -or $title -match '^Search\s*\|') {
            $isSearchPage = $true
        }

        return [PSCustomObject]@{
            Title       = $title
            Description = $description
            Url         = $finalUrl
            IsSearchPage = $isSearchPage
        }
    }
    catch {
        return $null
    }
}

function Escape-MarkdownTableCell([string]$text) {
    if (-not $text) {
        return ""
    }
    return $text.Replace('|', '\|')
}

function ConvertTo-Hashtable($inputObject) {
    if ($null -eq $inputObject) {
        return $null
    }

    if ($inputObject -is [System.Collections.IDictionary]) {
        $result = @{}
        foreach ($key in $inputObject.Keys) {
            $result[$key] = ConvertTo-Hashtable $inputObject[$key]
        }
        return $result
    }

    if ($inputObject -is [System.Collections.IEnumerable] -and -not ($inputObject -is [string])) {
        $result = @()
        foreach ($item in $inputObject) {
            $result += ,(ConvertTo-Hashtable $item)
        }
        return $result
    }

    $properties = $inputObject.PSObject.Properties
    if ($properties -and $properties.Count -gt 0) {
        $result = @{}
        foreach ($property in $properties) {
            $result[$property.Name] = ConvertTo-Hashtable $property.Value
        }
        return $result
    }

    return $inputObject
}

# Warning code metadata for human-readable descriptions
$codeDescriptions = @{
    # Nullable reference types
    "CS8600" = "Converting null literal or possible null value to non-nullable type"
    "CS8601" = "Possible null reference assignment"
    "CS8602" = "Dereference of a possibly null reference"
    "CS8603" = "Possible null reference return"
    "CS8604" = "Possible null reference argument"
    "CS8610" = "Nullability of reference types in type parameter doesn't match"
    "CS8618" = "Non-nullable field/property must contain a non-null value"
    "CS8619" = "Nullability of reference types in value doesn't match target type"
    "CS8620" = "Argument nullability differs from parameter type"
    "CS8622" = "Nullability of reference types in type of parameter doesn't match"
    "CS8625" = "Cannot convert null literal to non-nullable reference type"
    "CS8765" = "Nullability of type of parameter doesn't match overridden member"
    "CS8766" = "Nullability of reference types in return type doesn't match"
    "CS8767" = "Nullability of reference types in type of parameter doesn't match"
    "CS8769" = "Nullability of reference types in type of parameter doesn't match"
    # Code quality
    "CS0067" = "Event is never used"
    "CS0108" = "Member hides inherited member; missing 'new' keyword"
    "CS0109" = "Member does not hide an accessible member; 'new' keyword not required"
    "CS0114" = "Member hides inherited member; missing 'override' keyword"
    "CS0168" = "Variable declared but never used"
    "CS0169" = "Field is never used"
    "CS0219" = "Variable is assigned but its value is never used"
    "CS0414" = "Field is assigned but its value is never read"
    "CS0105" = "Duplicate using directive"
    "CS0162" = "Unreachable code detected"
    "CS0618" = "Member is obsolete"
    "CS0649" = "Field is never assigned to and will always have its default value"
    "CS0693" = "Type parameter has the same name as outer type parameter"
    "CS1717" = "Assignment made to same variable"
    "CS4014" = "Async call is not awaited; execution continues before call completes"
    "CS8321" = "Local function declared but never used"
    "CS9113" = "Parameter is unread"
    "CS9191" = "The ref modifier for argument corresponding to in parameter is equivalent to in"
    "CS9192" = "Argument should be passed with ref or in keyword"
    "CS9193" = "Argument should be a variable because it is passed to a ref readonly parameter"
    # NuGet
    "NU1510" = "PackageReference will not be pruned (may be unnecessary)"
    "NU1602" = "Dependency specified with exact version, no lower bound"
    "NU1701" = "Package was restored using a different target framework"
    # Trimming / AOT
    "IL2026" = "Members annotated with RequiresUnreferencedCode may break trimming"
    "IL2046" = "Annotated method conflicts with base/interface"
    "IL2067" = "Target parameter of Activator.CreateInstance is not compatible"
    "IL2070" = "Argument does not satisfy DynamicallyAccessedMemberTypes"
    "IL2072" = "Return value does not satisfy DynamicallyAccessedMemberTypes"
    "IL2075" = "Value from GetType passed to parameter with DynamicallyAccessedMemberTypes"
    "IL2091" = "DynamicallyAccessedMemberTypes on generic parameter don't match"
    "IL3050" = "Calling members annotated with RequiresDynamicCode in AOT"
    # Platform
    "CA1416" = "Platform compatibility - call site reachable on all platforms"
    "CA2022" = "Partial read from stream (use ReadExactly)"
    # Obsolete APIs
    "SYSLIB0050" = "Obsolete serialization API usage"
    "SYSLIB0051" = "Legacy serialization support APIs are obsolete"
    # Miscellaneous
    "CS0652" = "Comparison to integral constant is useless; constant outside range"
}

function Resolve-WarningMetadata(
    [string]$code,
    [string]$docView,
    [hashtable]$cache,
    [hashtable]$fallbackDescriptions
) {
    $cacheKey = "$($code.ToUpperInvariant())|$docView"
    if ($cache.ContainsKey($cacheKey)) {
        $cached = $cache[$cacheKey]
        # Cache entries may be hashtables after JSON round-trip; normalise to PSCustomObject
        if ($cached -is [hashtable]) {
            $cached = [PSCustomObject]$cached
        }
        if ($cached -and $cached.Description -and $cached.Description -notmatch '^Search\s*\|') {
            return $cached
        }
    }

    $metadata = $null
    $searchUrl = $null
    $candidates = Get-WarningDocCandidates -code $code -docView $docView
    foreach ($candidate in $candidates) {
        $page = Get-PageMetadata -url $candidate
        if ($page) {
            if ($page.IsSearchPage) {
                if (-not $searchUrl) {
                    $searchUrl = $page.Url
                }
                continue
            }

            $description = if ($page.Description) { $page.Description } elseif ($page.Title) { $page.Title } else { "(see docs)" }
            $metadata = [PSCustomObject]@{
                Code        = $code.ToUpperInvariant()
                Description = $description
                Url         = $page.Url
                Source      = "online"
            }
            break
        }
    }

    if (-not $metadata) {
        $upperCode = $code.ToUpperInvariant()
        $fallbackDescription = if ($fallbackDescriptions.ContainsKey($upperCode)) { $fallbackDescriptions[$upperCode] } else { "(see docs)" }
        # Always prefer the first direct docs URL (not a search query) for the link.
        # Candidates are ordered: versioned docs, unversioned docs, search — pick the first non-search one.
        $fallbackUrl = $candidates | Where-Object { $_ -notmatch '/search/' } | Select-Object -First 1
        if (-not $fallbackUrl) {
            $fallbackUrl = $candidates | Select-Object -First 1
        }
        $metadata = [PSCustomObject]@{
            Code        = $upperCode
            Description = $fallbackDescription
            Url         = $fallbackUrl
            Source      = "fallback"
        }
    }

    $cache[$cacheKey] = $metadata
    return $metadata
}

$warningDocCache = @{}
if (Test-Path $cacheFile) {
    try {
        $rawCache = Get-Content -Path $cacheFile -Raw
        if ($rawCache) {
            $loadedJson = ConvertFrom-Json -InputObject $rawCache
            $loaded = ConvertTo-Hashtable $loadedJson
            if ($loaded) {
                $warningDocCache = $loaded
            }
        }
    }
    catch {
        Write-Host "Warning metadata cache unreadable at $cacheFile. Rebuilding cache." -ForegroundColor Yellow
        $warningDocCache = @{}
    }
}

$docView = Get-DotNetDocView
if ($docView) {
    Write-Host "Detected .NET docs view: $docView" -ForegroundColor Cyan
}
else {
    Write-Host "Could not detect .NET SDK version; using unversioned docs URLs." -ForegroundColor Yellow
}

$warningMetadata = @{}
$uniqueCodes = $warnings | Select-Object -ExpandProperty Code -Unique
if ($SkipDocLookup) {
    Write-Host "Skipping online doc lookup (-SkipDocLookup). Using fallback descriptions and cached entries only." -ForegroundColor Yellow
    foreach ($code in $uniqueCodes) {
        $upperCode = $code.ToUpperInvariant()
        $cacheKey  = "$upperCode|$docView"
        if ($warningDocCache.ContainsKey($cacheKey)) {
            $cached = $warningDocCache[$cacheKey]
            if ($cached -is [hashtable]) { $cached = [PSCustomObject]$cached }
            # If cached entry points to a search page, replace its URL with the direct docs URL
            if ($cached.Url -match '/search/') {
                $directUrl = Get-WarningDocCandidates -code $code -docView $docView | Where-Object { $_ -notmatch '/search/' } | Select-Object -First 1
                if ($directUrl) { $cached.Url = $directUrl }
            }
            $warningMetadata[$code] = $cached
        }
        else {
            $fallbackDesc = if ($codeDescriptions.ContainsKey($upperCode)) { $codeDescriptions[$upperCode] } else { "(see docs)" }
            $fallbackUrl  = Get-WarningDocCandidates -code $code -docView $docView | Where-Object { $_ -notmatch '/search/' } | Select-Object -First 1
            if (-not $fallbackUrl) { $fallbackUrl = Get-WarningDocCandidates -code $code -docView $docView | Select-Object -First 1 }
            $warningMetadata[$code] = [PSCustomObject]@{
                Code        = $upperCode
                Description = $fallbackDesc
                Url         = $fallbackUrl
                Source      = "fallback"
            }
        }
    }
}
else {
    foreach ($code in $uniqueCodes) {
        $warningMetadata[$code] = Resolve-WarningMetadata -code $code -docView $docView -cache $warningDocCache -fallbackDescriptions $codeDescriptions
    }

    New-Item -ItemType Directory -Force -Path $logDir | Out-Null
    ($warningDocCache | ConvertTo-Json -Depth 6) | Out-File -FilePath $cacheFile -Encoding utf8
    Write-Host "Warning metadata cache updated: $cacheFile" -ForegroundColor Cyan
}

# Priority buckets (by code prefix)
function Get-WarningPriority([string]$code) {
    # High: nullability, trimming, NuGet resolution
    if ($code -match '^CS86' -or $code -match '^IL' -or $code -eq 'NU1602') {
        return 'High'
    }
    # Medium: obsolete, hiding, unused, platform, ref/in passing
    if ($code -match '^CS0(108|114|618|652|693)$' -or $code -match '^CS0(168|169|219|414|321)$' -or
        $code -match '^CS9' -or $code -match '^CA' -or $code -match '^SYSLIB' -or
        $code -eq 'NU1701' -or $code -eq 'NU1510') {
        return 'Medium'
    }
    # Low: everything else
    return 'Low'
}

# Group: Project → Priority → Code → list of warnings
$byProject = $warnings | Group-Object -Property Project | Sort-Object Name

# ── Write report ────────────────────────────────────────────────────────────
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null

$sw = [System.IO.StreamWriter]::new($OutFile, $false, [System.Text.UTF8Encoding]::new($true))

try {
    $sw.WriteLine("# XREngine Build Warnings Report")
    $sw.WriteLine("")
    $sw.WriteLine("_Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') | Configuration: $Configuration | Total warnings: $($warnings.Count)_")
    $sw.WriteLine("")
    $sw.WriteLine("---")
    $sw.WriteLine("")

    # ── Summary table ───────────────────────────────────────────────────────
    $sw.WriteLine("## Summary by Project")
    $sw.WriteLine("")
    $sw.WriteLine("| Project | Warnings |")
    $sw.WriteLine("|---------|----------|")

    foreach ($pg in $byProject) {
        $sw.WriteLine("| **$($pg.Name)** | $($pg.Count) |")
    }

    $sw.WriteLine("")

    # ── Summary by warning code ─────────────────────────────────────────────
    $byCodes = $warnings | Group-Object -Property Code | Sort-Object Count -Descending
    $sw.WriteLine("## Summary by Warning Code")
    $sw.WriteLine("")
    $sw.WriteLine("| Code | Count | Description |")
    $sw.WriteLine("|------|-------|-------------|")

    foreach ($cg in $byCodes) {
        $meta = if ($warningMetadata.ContainsKey($cg.Name)) { $warningMetadata[$cg.Name] } else { $null }
        $desc = if ($meta) { Escape-MarkdownTableCell $meta.Description } elseif ($codeDescriptions.ContainsKey($cg.Name)) { Escape-MarkdownTableCell $codeDescriptions[$cg.Name] } else { "(see docs)" }
        $codeCell = if ($meta -and $meta.Url) { "[``$($cg.Name)``]($($meta.Url))" } else { "``$($cg.Name)``" }
        $sw.WriteLine("| $codeCell | $($cg.Count) | $desc |")
    }

    $sw.WriteLine("")
    $sw.WriteLine("---")
    $sw.WriteLine("")

    # ── Per-project detail ──────────────────────────────────────────────────
    foreach ($pg in $byProject) {
        $sw.WriteLine("## Project: $($pg.Name)")
        $sw.WriteLine("> $($pg.Count) warning(s)")
        $sw.WriteLine("")

        # Group by priority, then by code
        $byPriority = $pg.Group |
            ForEach-Object { $_ | Add-Member -NotePropertyName Priority -NotePropertyValue (Get-WarningPriority $_.Code) -PassThru } |
            Group-Object -Property Priority

        # Ensure stable ordering: High, Medium, Low
        $priorityOrder = @('High', 'Medium', 'Low')
        foreach ($pri in $priorityOrder) {
            $priGroup = $byPriority | Where-Object { $_.Name -eq $pri }
            if (-not $priGroup) { continue }

            $sw.WriteLine("### $pri Priority")
            $sw.WriteLine("")

            $byCode = $priGroup.Group | Group-Object -Property Code | Sort-Object Count -Descending
            foreach ($cg in $byCode) {
                $meta = if ($warningMetadata.ContainsKey($cg.Name)) { $warningMetadata[$cg.Name] } else { $null }
                $desc = if ($meta) { $meta.Description } elseif ($codeDescriptions.ContainsKey($cg.Name)) { $codeDescriptions[$cg.Name] } else { "" }
                $codeHeading = if ($meta -and $meta.Url) { "[``$($cg.Name)``]($($meta.Url))" } else { "``$($cg.Name)``" }
                $sw.WriteLine("#### $codeHeading ($($cg.Count)) - $desc")
                $sw.WriteLine("")

                # Group by file for compact display
                $byFile = $cg.Group | Group-Object -Property File | Sort-Object Name
                foreach ($fg in $byFile) {
                    $sw.WriteLine("- **$($fg.Name)**")
                    foreach ($w in ($fg.Group | Sort-Object Line)) {
                        if ($w.Line -gt 0) {
                            $sw.WriteLine("  - L$($w.Line): $($w.Message)")
                        }
                        else {
                            $sw.WriteLine("  - $($w.Message)")
                        }
                    }
                }

                $sw.WriteLine("")
            }
        }

        $sw.WriteLine("---")
        $sw.WriteLine("")
    }

    # ── Zero-warnings congratulations ───────────────────────────────────────
    if ($warnings.Count -eq 0) {
        $sw.WriteLine("**No compiler warnings detected. The build is clean.**")
        $sw.WriteLine("")
    }
}
finally {
    $sw.Flush()
    $sw.Close()
}

Write-Host ""
Write-Host "Report written to: $OutFile" -ForegroundColor Green
Write-Host "  Total warnings: $($warnings.Count)" -ForegroundColor $(if ($warnings.Count -eq 0) { "Green" } else { "Yellow" })
