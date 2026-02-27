param(
    [string]$RepoRoot = (Resolve-Path ".").Path,
    [string]$OutFile = (Join-Path (Resolve-Path ".").Path "docs\work\audit\static-classes.md")
)

$ErrorActionPreference = "Stop"

$excludeDirs = @(
    '\.git\',
    '\Build\',
    '\ThirdParty\',
    '\Submodules\',
    '\generated\',
    '\bin\',
    '\obj\',
    '\docs\docfx\',
    '\docs\api\',
    '\docs\licenses\',
    '\docs\architecture\',
    '\docs\features\',
    '\docs\work\',
    '\XREngine.Editor\Build\',
    '\XREngine\Build\',
    '\XRENGINE\Build\'
)

function Should-ExcludePath([string]$fullPath) {
    foreach ($dir in $excludeDirs) {
        if ($fullPath -like "*$dir*") { return $true }
    }
    return $false
}

$csFiles = Get-ChildItem -Path $RepoRoot -Recurse -Filter "*.cs" -File | Where-Object {
    -not (Should-ExcludePath $_.FullName)
}

$results = New-Object System.Collections.Generic.List[object]

$typeDeclRegex = [regex]"^\s*(?:(?:public|internal|protected|private|abstract|sealed|partial|static|unsafe|new|readonly|file)\s+)*(?:class|struct|interface|record(?:\s+class|\s+struct)?)\s+([A-Za-z0-9_]+)\b"
$staticClassRegex = [regex]"^\s*(public|internal|protected|private)?\s*static\s+(partial\s+)?class\s+([A-Za-z0-9_]+)\b"

function Get-BraceCounts([string]$line) {
    $clean = $line
    $commentIndex = $clean.IndexOf('//')
    if ($commentIndex -ge 0) {
        $clean = $clean.Substring(0, $commentIndex)
    }

    return [pscustomobject]@{
        Open = ([regex]::Matches($clean, '\{')).Count
        Close = ([regex]::Matches($clean, '\}')).Count
    }
}

foreach ($file in $csFiles) {
    $lines = Get-Content -Path $file.FullName
    $namespace = $null
    $pendingTypes = New-Object System.Collections.Generic.Queue[object]
    $typeStack = New-Object System.Collections.Generic.Stack[object]
    $braceDepth = 0

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]

        if (-not $namespace) {
            if ($line -match "^\s*namespace\s+([A-Za-z0-9_\.]+)\s*;\s*$") {
                $namespace = $Matches[1]
            } elseif ($line -match "^\s*namespace\s+([A-Za-z0-9_\.]+)\s*\{\s*$") {
                $namespace = $Matches[1]
            }
        }

        $typeMatch = $typeDeclRegex.Match($line)
        if ($typeMatch.Success) {
            $pendingTypes.Enqueue([pscustomobject]@{ DeclLine = $i + 1; Name = $typeMatch.Groups[1].Value })
        }

        $staticMatch = $staticClassRegex.Match($line)
        if ($staticMatch.Success) {
            $className = $staticMatch.Groups[3].Value
            $repoRootPath = if ($RepoRoot -is [System.Management.Automation.PathInfo]) { $RepoRoot.Path } else { $RepoRoot }
            $relativePath = $file.FullName.Substring($repoRootPath.Length).TrimStart("\", "/") -replace "\\", "/"
            $isNested = $typeStack.Count -gt 0
            $scope = if ($isNested) { "Nested" } else { "TopLevel" }
            $parentType = if ($typeStack.Count -gt 0) { $typeStack.Peek().Name } else { $null }
            $results.Add([pscustomobject]@{
                Namespace = $namespace
                ClassName = $className
                File = $relativePath
                Line = $i + 1
                Scope = $scope
                ParentType = $parentType
            })
        }

        $braceCounts = Get-BraceCounts -line $line

        for ($j = 0; $j -lt $braceCounts.Open; $j++) {
            $braceDepth++
            if ($pendingTypes.Count -gt 0) {
                $pending = $pendingTypes.Dequeue()
                    $typeStack.Push([pscustomobject]@{ StartDepth = $braceDepth; DeclLine = $pending.DeclLine; Name = $pending.Name })
            }
        }

        for ($j = 0; $j -lt $braceCounts.Close; $j++) {
            if ($typeStack.Count -gt 0 -and $typeStack.Peek().StartDepth -eq $braceDepth) {
                [void]$typeStack.Pop()
            }

            if ($braceDepth -gt 0) {
                $braceDepth--
            }
        }
    }
}

$results = $results | Sort-Object Scope, Namespace, ClassName, File, Line
$topLevelResults = @($results | Where-Object { $_.Scope -eq "TopLevel" })
$nestedResults = @($results | Where-Object { $_.Scope -eq "Nested" })

New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null

$linesOut = New-Object System.Collections.Generic.List[string]
$linesOut.Add("# Static Classes")
$linesOut.Add("")
$linesOut.Add("Generated: $(Get-Date -Format "yyyy-MM-dd")")
$linesOut.Add("")
$linesOut.Add("Total: $($results.Count)")
$linesOut.Add("Top-level: $($topLevelResults.Count)")
$linesOut.Add("Nested: $($nestedResults.Count)")
$linesOut.Add("")

$linesOut.Add("## Top-level static classes")
$linesOut.Add("")
if ($topLevelResults.Count -eq 0) {
    $linesOut.Add("None")
    $linesOut.Add("")
}
else {
    $linesOut.Add("| Namespace | Class | File | Line |")
    $linesOut.Add("| --- | --- | --- | --- |")
    foreach ($item in $topLevelResults) {
        $ns = if ($item.Namespace) { $item.Namespace } else { "(none)" }
        $linesOut.Add("| $ns | $($item.ClassName) | $($item.File) | $($item.Line) |")
    }
    $linesOut.Add("")
}

$linesOut.Add("## Nested static classes")
$linesOut.Add("")
if ($nestedResults.Count -eq 0) {
    $linesOut.Add("None")
    $linesOut.Add("")
}
else {
    $linesOut.Add("| Namespace | Parent Type | Class | File | Line |")
    $linesOut.Add("| --- | --- | --- | --- | --- |")
    foreach ($item in $nestedResults) {
        $ns = if ($item.Namespace) { $item.Namespace } else { "(none)" }
        $parentType = if ($item.ParentType) { $item.ParentType } else { "(unknown)" }
        $linesOut.Add("| $ns | $parentType | $($item.ClassName) | $($item.File) | $($item.Line) |")
    }
}

Set-Content -Path $OutFile -Value $linesOut -Encoding UTF8

Write-Host "Wrote $($results.Count) static classes to $OutFile"