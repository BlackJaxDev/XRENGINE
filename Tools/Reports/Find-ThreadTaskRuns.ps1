[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Root = (Resolve-Path ".").Path,

    [Parameter(Mandatory = $false)]
    [string]$OutFile = (Join-Path (Resolve-Path ".").Path "docs\work\audit\thread-task-runs.md")
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

            $isVerbatim = ($prev1 -eq '@') -or ($prev2 -eq '@' -and $prev1 -eq '$') -or ($prev2 -eq '$' -and $prev1 -eq '@')

            if ($isVerbatim) { $inVerbatim = $true } else { $inNormal = $true }
        }
    }

    return ($inNormal -or $inVerbatim)
}

$scanDirs = Get-ChildItem -Path $Root -Directory |
    Where-Object { $_.Name -eq 'XRENGINE' -or $_.Name -like 'XREngine*' } |
    ForEach-Object { $_.FullName }

$excludeRegex = '\\Submodules\\|\\Build\\Submodules\\|\\ThirdParty\\|\\bin\\|\\obj\\|\\docs\\docfx\\|\\docs\\api\\|\\docs\\licenses\\|\\docs\\work\\'

$regexOptions = [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::Compiled

$rxThreadCreation = New-Object System.Text.RegularExpressions.Regex('\bnew\s+Thread\s*\(', $regexOptions)
$rxThreadInlineStart = New-Object System.Text.RegularExpressions.Regex('\bnew\s+Thread\s*\([^\)]*\)\s*\.\s*Start\s*\(', $regexOptions)
$rxThreadDecl = New-Object System.Text.RegularExpressions.Regex('\bThread\s+([A-Za-z_@][\w@]*)\b(?!\s*\()', $regexOptions)
$rxThreadAssignFromNew = New-Object System.Text.RegularExpressions.Regex('\b(?:var\s+)?([A-Za-z_@][\w@]*)\s*=\s*new\s+Thread\s*\(', $regexOptions)

$rxTaskRun = New-Object System.Text.RegularExpressions.Regex('\bTask\s*\.\s*Run\s*\(', $regexOptions)
$rxTaskStartNew = New-Object System.Text.RegularExpressions.Regex('\bTask\s*\.\s*Factory\s*\.\s*StartNew\s*\(', $regexOptions)
$rxTaskInlineStart = New-Object System.Text.RegularExpressions.Regex('\bnew\s+Task(?:\s*<[^>]+>)?\s*\([^\)]*\)\s*\.\s*Start\s*\(', $regexOptions)
$rxTaskDecl = New-Object System.Text.RegularExpressions.Regex('\bTask(?:\s*<[^>]+>)?\s+([A-Za-z_@][\w@]*)\b(?!\s*\()', $regexOptions)
$rxTaskAssignRunnable = New-Object System.Text.RegularExpressions.Regex('\b(?:var\s+)?([A-Za-z_@][\w@]*)\s*=\s*(?:Task\s*\.\s*Run\s*\(|Task\s*\.\s*Factory\s*\.\s*StartNew\s*\(|new\s+Task(?:\s*<[^>]+>)?\s*\()', $regexOptions)

$categoryOrder = @('Thread Creations', 'Thread Starts', 'Thread Stops', 'Task Runs')

New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null

$sw = New-Object System.IO.StreamWriter($OutFile, $false, (New-Object System.Text.UTF8Encoding($true)))

try {
    $sw.WriteLine('# Thread and task run report')
    $sw.WriteLine('')
    $sw.WriteLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $sw.WriteLine('')

    $sw.WriteLine('Scan roots:')
    foreach ($d in $scanDirs) { $sw.WriteLine('- ' + (Get-RelativePath -RootPath $Root -FullPath $d)) }
    $sw.WriteLine('')

    $sw.WriteLine('Search categories:')
    $sw.WriteLine('- Thread Creations: `new Thread(...)`')
    $sw.WriteLine('- Thread Starts: `<thread>.Start(...)` and `new Thread(...).Start(...)`')
    $sw.WriteLine('- Thread Stops: `<thread>.Join(...)`, `<thread>.Interrupt(...)`, `<thread>.Abort(...)`')
    $sw.WriteLine('- Task Runs: `Task.Run(...)`, `Task.Factory.StartNew(...)`, `<task>.Start(...)`, `new Task(...).Start(...)`')
    $sw.WriteLine('')

    $sw.WriteLine('Excluded paths (regex):')
    $sw.WriteLine('- ' + $excludeRegex)
    $sw.WriteLine('')

    $sw.WriteLine('Notes:')
    $sw.WriteLine('- Comment-only lines (//, ///, /*, *, */) are skipped to reduce false positives.')
    $sw.WriteLine('- Matches inside string literals are skipped to reduce false positives.')

    $files = foreach ($d in $scanDirs) {
        Get-ChildItem -Path $d -Recurse -File -Filter *.cs |
            Where-Object { $_.FullName -notmatch $excludeRegex }
    }

    $totals = @{}
    foreach ($category in $categoryOrder) { $totals[$category] = 0 }
    $totalAll = 0

    foreach ($f in ($files | Sort-Object FullName)) {
        $anyWritten = $false
        $lineNumber = 0

        $threadVars = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
        $taskVars = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)

        foreach ($line in [System.IO.File]::ReadLines($f.FullName)) {
            $lineNumber++
            $trim = $line.Trim()

            if ($trim.Length -eq 0) { continue }
            if ($trim.StartsWith('//') -or $trim.StartsWith('/*') -or $trim.StartsWith('*') -or $trim.StartsWith('*/')) { continue }

            foreach ($m in $rxThreadDecl.Matches($line)) {
                if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                $null = $threadVars.Add($m.Groups[1].Value)
            }

            foreach ($m in $rxThreadAssignFromNew.Matches($line)) {
                if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                $null = $threadVars.Add($m.Groups[1].Value)
            }

            foreach ($m in $rxTaskDecl.Matches($line)) {
                if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                $null = $taskVars.Add($m.Groups[1].Value)
            }

            foreach ($m in $rxTaskAssignRunnable.Matches($line)) {
                if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                $null = $taskVars.Add($m.Groups[1].Value)
            }

            $lineFindings = New-Object System.Collections.Generic.List[object]

            foreach ($m in $rxThreadCreation.Matches($line)) {
                if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                $lineFindings.Add([pscustomobject]@{ Category = 'Thread Creations'; Match = $m }) | Out-Null
            }

            foreach ($m in $rxThreadInlineStart.Matches($line)) {
                if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                $lineFindings.Add([pscustomobject]@{ Category = 'Thread Starts'; Match = $m }) | Out-Null
            }

            foreach ($threadVar in $threadVars) {
                $escapedVar = [System.Text.RegularExpressions.Regex]::Escape($threadVar)
                $rxThreadStartVar = New-Object System.Text.RegularExpressions.Regex("\b${escapedVar}\s*\.\s*Start\s*\(", $regexOptions)
                $rxThreadStopVar = New-Object System.Text.RegularExpressions.Regex("\b${escapedVar}\s*\.\s*(Join|Interrupt|Abort)\s*\(", $regexOptions)

                foreach ($m in $rxThreadStartVar.Matches($line)) {
                    if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                    $lineFindings.Add([pscustomobject]@{ Category = 'Thread Starts'; Match = $m }) | Out-Null
                }

                foreach ($m in $rxThreadStopVar.Matches($line)) {
                    if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                    $lineFindings.Add([pscustomobject]@{ Category = 'Thread Stops'; Match = $m }) | Out-Null
                }
            }

            foreach ($m in $rxTaskRun.Matches($line)) {
                if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                $lineFindings.Add([pscustomobject]@{ Category = 'Task Runs'; Match = $m }) | Out-Null
            }

            foreach ($m in $rxTaskStartNew.Matches($line)) {
                if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                $lineFindings.Add([pscustomobject]@{ Category = 'Task Runs'; Match = $m }) | Out-Null
            }

            foreach ($m in $rxTaskInlineStart.Matches($line)) {
                if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                $lineFindings.Add([pscustomobject]@{ Category = 'Task Runs'; Match = $m }) | Out-Null
            }

            foreach ($taskVar in $taskVars) {
                $escapedVar = [System.Text.RegularExpressions.Regex]::Escape($taskVar)
                $rxTaskStartVar = New-Object System.Text.RegularExpressions.Regex("\b${escapedVar}\s*\.\s*Start\s*\(", $regexOptions)

                foreach ($m in $rxTaskStartVar.Matches($line)) {
                    if (Test-InStringLiteral -Line $line -Index $m.Index) { continue }
                    $lineFindings.Add([pscustomobject]@{ Category = 'Task Runs'; Match = $m }) | Out-Null
                }
            }

            foreach ($finding in $lineFindings) {
                if (-not $anyWritten) {
                    $sw.WriteLine('')
                    $sw.WriteLine('')
                    $sw.WriteLine('## ' + (Get-RelativePath -RootPath $Root -FullPath $f.FullName))
                    $anyWritten = $true
                }

                $col = $finding.Match.Index + 1
                $token = $finding.Match.Value
                $sw.WriteLine("- [$($finding.Category)] L${lineNumber} C${col}: ${token} :: $trim")
                $totals[$finding.Category]++
                $totalAll++
            }
        }
    }

    $sw.WriteLine('')
    $sw.WriteLine('')
    $sw.WriteLine('---')
    $sw.WriteLine('Category totals:')
    foreach ($category in $categoryOrder) {
        $sw.WriteLine("- ${category}: $($totals[$category])")
    }
    $sw.WriteLine("Total matches: $totalAll")
}
finally {
    $sw.Flush()
    $sw.Close()
}

Write-Host "Wrote report to: $OutFile"