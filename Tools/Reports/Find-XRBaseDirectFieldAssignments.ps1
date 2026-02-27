[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$RepoRoot = (Resolve-Path ".").Path,

    [Parameter(Mandatory = $false)]
    [string]$OutFile = (Join-Path (Resolve-Path ".").Path "docs\work\audit\xrbase-direct-field-assignments.md")
)

$ErrorActionPreference = "Stop"

$excludePathRegex = '\\Submodules\\|\\Build\\Submodules\\|\\ThirdParty\\|\\bin\\|\\obj\\|\\docs\\docfx\\|\\docs\\api\\|\\docs\\licenses\\'

function Get-RelativePath {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$FullPath
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($RootPath).TrimEnd('\', '/')
    $normalizedPath = [System.IO.Path]::GetFullPath($FullPath)

    if ($normalizedPath.StartsWith($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $normalizedPath.Substring($normalizedRoot.Length).TrimStart('\', '/').Replace('\\', '/')
    }

    return $normalizedPath.Replace('\\', '/')
}

function Get-NormalizedTypeNames {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$RawType
    )

    $type = $RawType.Trim()
    if ($type.Length -eq 0) {
        return @()
    }

    if ($type.StartsWith("global::")) {
        $type = $type.Substring(8)
    }

    if ($type.Contains('<')) {
        $type = $type.Substring(0, $type.IndexOf('<'))
    }

    $type = $type.Trim()
    if ($type.Length -eq 0) {
        return @()
    }

    $simple = if ($type.Contains('.')) { $type.Substring($type.LastIndexOf('.') + 1) } else { $type }

    if ($simple -eq $type) {
        return @($simple)
    }

    return @($type, $simple)
}

function Get-BraceCounts {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line
    )

    $clean = $Line
    $commentIndex = $clean.IndexOf('//')
    if ($commentIndex -ge 0) {
        $clean = $clean.Substring(0, $commentIndex)
    }

    $openCount = ([regex]::Matches($clean, '\{')).Count
    $closeCount = ([regex]::Matches($clean, '\}')).Count

    return [pscustomobject]@{
        Open = $openCount
        Close = $closeCount
    }
}

function Test-IsStaticProperty {
    param(
        [Parameter(Mandatory = $true)]$Lines,
        [Parameter(Mandatory = $true)][int]$SetterLineIndex
    )

    # Scan backward up to 10 lines to find the property declaration.
    $startSearch = [Math]::Max(0, $SetterLineIndex - 10)
    for ($i = $SetterLineIndex - 1; $i -ge $startSearch; $i--) {
        $l = $Lines[$i].Trim()
        # Skip blank lines and lone braces
        if ($l.Length -eq 0 -or $l -eq '{' -or $l -eq '}') { continue }
        # Skip get accessor lines (keep scanning up)
        if ($l -match '^get\b') { continue }
        # If line has 'static' in a member declaration, it's a static property
        if ($l -match '\bstatic\b' -and $l -match '\b(?:public|internal|protected|private)\b') {
            return $true
        }
        # If we hit a non-static member declaration, stop
        if ($l -match '\b(?:public|internal|protected|private)\b') {
            return $false
        }
    }
    return $false
}

function Get-SourceFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Root
    )

    Get-ChildItem -Path $Root -Recurse -File -Filter *.cs |
        Where-Object { $_.FullName -notmatch $excludePathRegex } |
        Sort-Object FullName
}

$files = Get-SourceFiles -Root $RepoRoot

if (-not $files -or $files.Count -eq 0) {
    throw "No C# files found under '$RepoRoot'."
}

$classPattern = New-Object System.Text.RegularExpressions.Regex(
    '^\s*(?:(?:public|internal|protected|private|abstract|sealed|partial|new|readonly|unsafe|static)\s+)*class\s+([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]*>)?\s*(?::\s*([^\{]+))?',
    [System.Text.RegularExpressions.RegexOptions]::Compiled
)

$allClasses = New-Object System.Collections.Generic.List[object]
$classToBases = @{}

foreach ($file in $files) {
    $lines = [System.IO.File]::ReadAllLines($file.FullName)

    for ($lineIndex = 0; $lineIndex -lt $lines.Length; $lineIndex++) {
        $line = $lines[$lineIndex]
        $match = $classPattern.Match($line)
        if (-not $match.Success) {
            continue
        }

        $className = $match.Groups[1].Value
        $basesRaw = $match.Groups[2].Value
        $baseNames = New-Object System.Collections.Generic.List[string]

        if (-not [string]::IsNullOrWhiteSpace($basesRaw)) {
            $baseParts = $basesRaw.Split(',')
            foreach ($basePart in $baseParts) {
                if ([string]::IsNullOrWhiteSpace($basePart)) {
                    continue
                }

                foreach ($name in (Get-NormalizedTypeNames -RawType $basePart)) {
                    if (-not [string]::IsNullOrWhiteSpace($name)) {
                        $baseNames.Add($name)
                    }
                }
            }
        }

        $record = [pscustomobject]@{
            Name = $className
            BaseNames = @($baseNames)
            File = $file.FullName
            Line = $lineIndex + 1
        }
        $allClasses.Add($record)

        if (-not $classToBases.ContainsKey($className)) {
            $classToBases[$className] = New-Object System.Collections.Generic.List[string]
        }

        foreach ($b in $baseNames) {
            if (-not $classToBases[$className].Contains($b)) {
                $classToBases[$className].Add($b)
            }
        }
    }
}

$xrDerived = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::Ordinal)

foreach ($class in $allClasses) {
    if ($class.BaseNames -contains 'XRBase') {
        [void]$xrDerived.Add($class.Name)
    }
}

$changed = $true
while ($changed) {
    $changed = $false
    foreach ($class in $allClasses) {
        if ($xrDerived.Contains($class.Name)) {
            continue
        }

        foreach ($baseName in $class.BaseNames) {
            if ($baseName -eq 'XRBase' -or $xrDerived.Contains($baseName)) {
                if ($xrDerived.Add($class.Name)) {
                    $changed = $true
                }
                break
            }
        }
    }
}

$derivedClassesByFile = @{}
foreach ($class in $allClasses) {
    if (-not $xrDerived.Contains($class.Name)) {
        continue
    }

    if (-not $derivedClassesByFile.ContainsKey($class.File)) {
        $derivedClassesByFile[$class.File] = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::Ordinal)
    }

    [void]$derivedClassesByFile[$class.File].Add($class.Name)
}

$directAssignmentPattern = New-Object System.Text.RegularExpressions.Regex(
    '^\s*(?:this\.)?_[A-Za-z][A-Za-z0-9_]*\s*=',
    [System.Text.RegularExpressions.RegexOptions]::Compiled
)

$setArrowPattern = New-Object System.Text.RegularExpressions.Regex(
    '\bset\s*=>\s*(.+)',
    [System.Text.RegularExpressions.RegexOptions]::Compiled
)

$setBlockPattern = New-Object System.Text.RegularExpressions.Regex(
    '\bset\s*\{',
    [System.Text.RegularExpressions.RegexOptions]::Compiled
)

$violations = New-Object System.Collections.Generic.List[object]

foreach ($file in $files) {
    if (-not $derivedClassesByFile.ContainsKey($file.FullName)) {
        continue
    }

    $derivedNames = $derivedClassesByFile[$file.FullName]
    $lines = [System.IO.File]::ReadAllLines($file.FullName)

    $pendingClasses = New-Object System.Collections.Generic.Queue[object]
    $classStack = New-Object System.Collections.Generic.Stack[object]
    $braceDepth = 0
    $setterDepth = -1

    for ($lineIndex = 0; $lineIndex -lt $lines.Length; $lineIndex++) {
        $line = $lines[$lineIndex]
        $trim = $line.Trim()

        # Skip comment-only lines (catches //set => _field = value etc.)
        if ($trim.StartsWith('//') -or $trim.StartsWith('/*') -or $trim.StartsWith('*')) {
            # Still need to count braces inside block comments for depth tracking
            $countsLine = Get-BraceCounts -Line $line
            for ($j = 0; $j -lt $countsLine.Open; $j++) {
                $braceDepth++
                if ($pendingClasses.Count -gt 0) {
                    $pending = $pendingClasses.Dequeue()
                    $classStack.Push([pscustomobject]@{
                        Name = $pending.Name
                        IsDerived = $pending.IsDerived
                        StartDepth = $braceDepth
                        DeclLine = $pending.DeclLine
                    })
                }
            }
            for ($j = 0; $j -lt $countsLine.Close; $j++) {
                if ($setterDepth -gt 0 -and $braceDepth -eq $setterDepth) { $setterDepth = -1 }
                if ($classStack.Count -gt 0 -and $classStack.Peek().StartDepth -eq $braceDepth) { [void]$classStack.Pop() }
                if ($braceDepth -gt 0) { $braceDepth-- }
            }
            continue
        }

        $classMatch = $classPattern.Match($line)
        if ($classMatch.Success) {
            $className = $classMatch.Groups[1].Value
            $pendingClasses.Enqueue([pscustomobject]@{
                Name = $className
                IsDerived = $derivedNames.Contains($className)
                DeclLine = $lineIndex + 1
            })
        }

        # Only consider the INNERMOST class on the stack.
        # Nested non-XRBase classes inside XRBase-derived outer classes are not violations.
        $activeDerivedClass = $null
        if ($classStack.Count -gt 0) {
            $innermost = $classStack.Peek()
            if ($innermost.IsDerived) {
                $activeDerivedClass = $innermost
            }
        }

        if ($activeDerivedClass -ne $null) {
            if ($setterDepth -ge 0 -and $braceDepth -ge $setterDepth) {
                if ($directAssignmentPattern.IsMatch($trim) -and $trim -notmatch '\bSetField\s*\(') {
                    # Skip static properties — SetField is instance-only
                    if (-not (Test-IsStaticProperty -Lines $lines -SetterLineIndex $lineIndex)) {
                        $violations.Add([pscustomobject]@{
                            File = $file.FullName
                            Line = $lineIndex + 1
                            ClassName = $activeDerivedClass.Name
                            Code = $trim
                        })
                    }
                }
            }

            $setArrow = $setArrowPattern.Match($line)
            if ($setArrow.Success) {
                $expr = $setArrow.Groups[1].Value.Trim()
                if ($directAssignmentPattern.IsMatch($expr) -and $expr -notmatch '\bSetField\s*\(') {
                    # Skip static properties — SetField is instance-only
                    if (-not (Test-IsStaticProperty -Lines $lines -SetterLineIndex $lineIndex)) {
                        $violations.Add([pscustomobject]@{
                            File = $file.FullName
                            Line = $lineIndex + 1
                            ClassName = $activeDerivedClass.Name
                            Code = $trim
                        })
                    }
                }
            }

            if ($setterDepth -lt 0 -and $setBlockPattern.IsMatch($line)) {
                $counts = Get-BraceCounts -Line $line
                $setterDepth = $braceDepth + [Math]::Max(1, $counts.Open)
            }
        }

        $countsLine = Get-BraceCounts -Line $line

        for ($j = 0; $j -lt $countsLine.Open; $j++) {
            $braceDepth++
            if ($pendingClasses.Count -gt 0) {
                $pending = $pendingClasses.Dequeue()
                $classStack.Push([pscustomobject]@{
                    Name = $pending.Name
                    IsDerived = $pending.IsDerived
                    StartDepth = $braceDepth
                    DeclLine = $pending.DeclLine
                })
            }
        }

        for ($j = 0; $j -lt $countsLine.Close; $j++) {
            if ($setterDepth -gt 0 -and $braceDepth -eq $setterDepth) {
                $setterDepth = -1
            }

            if ($classStack.Count -gt 0 -and $classStack.Peek().StartDepth -eq $braceDepth) {
                [void]$classStack.Pop()
            }

            if ($braceDepth -gt 0) {
                $braceDepth--
            }
        }
    }
}

$violations = $violations | Sort-Object File, Line

New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null

$sw = New-Object System.IO.StreamWriter($OutFile, $false, (New-Object System.Text.UTF8Encoding($true)))
try {
    $sw.WriteLine('# XRBase direct field assignment report')
    $sw.WriteLine('')
    $sw.WriteLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
    $sw.WriteLine('')
    $sw.WriteLine('- Rule: classes deriving from `XRBase` should use `SetField(...)` instead of direct backing-field assignment in property setters.')
    $sw.WriteLine("- Scan root: $(Get-RelativePath -RootPath $RepoRoot -FullPath $RepoRoot)")
    $sw.WriteLine('- Heuristic: detects likely assignments like `_field = ...` inside `set { ... }` or `set => ...`.')
    $sw.WriteLine('- Excludes: commented-out code, static properties, nested non-XRBase classes.')
    $sw.WriteLine('')

    if (-not $violations -or $violations.Count -eq 0) {
        $sw.WriteLine('No likely violations found.')
        $sw.WriteLine('')
    }
    else {
        $currentFile = $null
        foreach ($v in $violations) {
            $relative = Get-RelativePath -RootPath $RepoRoot -FullPath $v.File
            if ($currentFile -ne $relative) {
                $sw.WriteLine('')
                $sw.WriteLine('## ' + $relative)
                $currentFile = $relative
            }

            $sw.WriteLine("- L$($v.Line) [$($v.ClassName)]: $($v.Code)")
        }

        $sw.WriteLine('')
    }

    $sw.WriteLine('---')
    $sw.WriteLine("Derived classes discovered: $($xrDerived.Count)")
    $sw.WriteLine("Likely violations: $($violations.Count)")
}
finally {
    $sw.Flush()
    $sw.Close()
}

Write-Host "Wrote report to: $OutFile"
