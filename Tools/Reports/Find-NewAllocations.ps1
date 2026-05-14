[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Root = (Resolve-Path ".").Path,

    [Parameter(Mandatory = $false)]
    [string]$OutFile = (Join-Path (Resolve-Path ".").Path "docs\work\audit\new-allocations.md"),

    [Parameter(Mandatory = $false)]
    [switch]$FailOnOpenXrHotPathAllocations
)

$ErrorActionPreference = "Stop"

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$FullPath
    )

    return $FullPath.Substring($RootPath.Length).TrimStart("\").Replace("\", "/")
}

function Test-InStringLiteral {
    param(
        [Parameter(Mandatory = $true)][string]$Line,
        [Parameter(Mandatory = $true)][int]$Index
    )

    $inNormal = $false
    $inVerbatim = $false

    for ($i = 0; $i -lt $Index; $i++) {
        $ch = $Line[$i]

        if ($inNormal) {
            if ($ch -eq "\\") {
                if ($i + 1 -lt $Index) { $i++ }
                continue
            }
            if ($ch -eq '"') { $inNormal = $false }
            continue
        }

        if ($inVerbatim) {
            if ($ch -eq '"') {
                if ($i + 1 -lt $Index -and $Line[$i + 1] -eq '"') {
                    $i++
                    continue
                }
                $inVerbatim = $false
            }
            continue
        }

        if ($ch -eq '"') {
            $prev1 = if ($i - 1 -ge 0) { $Line[$i - 1] } else { [char]0 }
            $prev2 = if ($i - 2 -ge 0) { $Line[$i - 2] } else { [char]0 }

            # Verbatim string can be @" or $@" or @$" (interpolated verbatim)
            $isVerbatim = ($prev1 -eq '@') -or ($prev2 -eq '@' -and $prev1 -eq '$') -or ($prev2 -eq '$' -and $prev1 -eq '@')

            if ($isVerbatim) { $inVerbatim = $true } else { $inNormal = $true }
        }
    }

    return ($inNormal -or $inVerbatim)
}

$scanDirs = Get-ChildItem -Path $Root -Directory |
    Where-Object { $_.Name -eq 'XRENGINE' -or $_.Name -like 'XREngine*' } |
    ForEach-Object { $_.FullName }

$excludeRegex = '\\Submodules\\|\\Build\\Submodules\\|\\bin\\|\\obj\\'

$patterns = @(
    '\bnew\s+[A-Za-z_@][\w\.@]*',
    '\bnew\s*\(\)',
    '\bnew\s*\[\]'
)

$openXrHotPathPatterns = @(
    'Debug\.(Out|Log|LogWarning|LogException)\s*\(\s*\$"',
    'Console\.WriteLine\s*\(\s*\$"',
    'string\.Format\s*\('
)

$regexOptions = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Compiled
$regexes = foreach ($pat in $patterns) { New-Object System.Text.RegularExpressions.Regex($pat, $regexOptions) }
$openXrHotPathRegexes = foreach ($pat in $openXrHotPathPatterns) { New-Object System.Text.RegularExpressions.Regex($pat, $regexOptions) }

New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null

$sw = New-Object System.IO.StreamWriter($OutFile, $false, (New-Object System.Text.UTF8Encoding($true)))

try {
    $sw.WriteLine('# C# `new` allocations report')
    $sw.WriteLine('')
    $sw.WriteLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $sw.WriteLine('')

    $sw.WriteLine('Scan roots:')
    foreach ($d in $scanDirs) { $sw.WriteLine('- ' + (Get-RelativePath -RootPath $Root -FullPath $d)) }
    $sw.WriteLine('')

    $sw.WriteLine('Search patterns:')
    foreach ($pat in $patterns) { $sw.WriteLine('- ' + $pat) }
    $sw.WriteLine('')

    $sw.WriteLine('OpenXR hot-path formatted logging patterns:')
    foreach ($pat in $openXrHotPathPatterns) { $sw.WriteLine('- ' + $pat) }
    $sw.WriteLine('')

    $sw.WriteLine('Excluded paths (regex):')
    $sw.WriteLine('- ' + $excludeRegex)
    $sw.WriteLine('')

    $sw.WriteLine('Notes:')
    $sw.WriteLine('- Comment-only lines (//, ///, /*, *, */) are skipped to reduce false positives.')
    $sw.WriteLine('- Matches inside string literals are skipped to reduce false positives (e.g., "Default ... new ...").')

    $files = foreach ($d in $scanDirs) {
        Get-ChildItem -Path $d -Recurse -File -Filter *.cs |
            Where-Object { $_.FullName -notmatch $excludeRegex }
    }

    $total = 0
    foreach ($f in ($files | Sort-Object FullName)) {
        $anyWritten = $false
        $lineNumber = 0

        foreach ($line in [System.IO.File]::ReadLines($f.FullName)) {
            $lineNumber++
            $trim = $line.Trim()

            if ($trim.Length -eq 0) { continue }
            if ($trim.StartsWith('//') -or $trim.StartsWith('/*') -or $trim.StartsWith('*') -or $trim.StartsWith('*/')) { continue }

            foreach ($re in $regexes) {
                $matches = $re.Matches($line)
                if ($matches.Count -eq 0) { continue }

                foreach ($m in $matches) {
                    if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }

                    if (-not $anyWritten) {
                        $sw.WriteLine('')
                        $sw.WriteLine('')
                        $sw.WriteLine('## ' + (Get-RelativePath -RootPath $Root -FullPath $f.FullName))
                        $anyWritten = $true
                    }

                    $col = $m.Index + 1
                    $token = $m.Value
                    $sw.WriteLine("- L${lineNumber} C${col}: ${token} :: $trim")
                    $total++
                }
            }
        }
    }

    $openXrHotPathTotal = 0
    $openXrFiles = foreach ($d in $scanDirs) {
        $openXrDir = Join-Path $d "Rendering\API\Rendering\OpenXR"
        if (Test-Path $openXrDir) {
            Get-ChildItem -Path $openXrDir -File -Filter "OpenXRAPI*.cs" |
                Where-Object { $_.FullName -notmatch $excludeRegex }
        }
    }

    foreach ($f in ($openXrFiles | Sort-Object FullName)) {
        $anyWritten = $false
        $lineNumber = 0

        foreach ($line in [System.IO.File]::ReadLines($f.FullName)) {
            $lineNumber++
            $trim = $line.Trim()

            if ($trim.Length -eq 0) { continue }
            if ($trim.StartsWith('//') -or $trim.StartsWith('/*') -or $trim.StartsWith('*') -or $trim.StartsWith('*/')) { continue }

            foreach ($re in $openXrHotPathRegexes) {
                $matches = $re.Matches($line)
                if ($matches.Count -eq 0) { continue }

                if (-not $anyWritten) {
                    if ($openXrHotPathTotal -eq 0) {
                        $sw.WriteLine('')
                        $sw.WriteLine('')
                        $sw.WriteLine('# OpenXR hot-path formatted logging candidates')
                        $sw.WriteLine('')
                        $sw.WriteLine('These are potential per-frame string allocations in `OpenXRAPI.*`. Keep them debug-gated or move them to a non-allocating diagnostic path before enabling them in steady-state rendering.')
                    }

                    $sw.WriteLine('')
                    $sw.WriteLine('## ' + (Get-RelativePath -RootPath $Root -FullPath $f.FullName))
                    $anyWritten = $true
                }

                foreach ($m in $matches) {
                    $col = $m.Index + 1
                    $token = $m.Value
                    $sw.WriteLine("- L${lineNumber} C${col}: ${token} :: $trim")
                    $openXrHotPathTotal++
                }
            }
        }
    }

    $sw.WriteLine('')
    $sw.WriteLine('')
    $sw.WriteLine('---')
    $sw.WriteLine("Total matches: $total")
    $sw.WriteLine("OpenXR hot-path formatted logging candidates: $openXrHotPathTotal")
}
finally {
    $sw.Flush()
    $sw.Close()
}

Write-Host "Wrote report to: $OutFile"

if ($FailOnOpenXrHotPathAllocations -and $openXrHotPathTotal -gt 0) {
    throw "Found $openXrHotPathTotal OpenXR hot-path formatted logging candidate(s). See $OutFile."
}
