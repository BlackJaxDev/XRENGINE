param(
    [string]$RepoRoot = (Resolve-Path ".").Path,
    [string]$OutFile = (Join-Path (Resolve-Path ".").Path "docs\work\audit\static-classes.md")
)

$ErrorActionPreference = "Stop"

$excludeDirs = @(
    "\\.git\\",
    "\\Build\\",
    "\\ThirdParty\\",
    "\\generated\\",
    "\\bin\\",
    "\\obj\\",
    "\\docs\\docfx\\",
    "\\docs\\api\\",
    "\\docs\\licenses\\",
    "\\docs\\architecture\\",
    "\\docs\\features\\",
    "\\docs\\work\\",
    "\\XREngine\.Editor\\Build\\",
    "\\XREngine\\Build\\",
    "\\XRENGINE\\Build\\"
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

foreach ($file in $csFiles) {
    $lines = Get-Content -Path $file.FullName
    $namespace = $null

    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]

        if (-not $namespace) {
            if ($line -match "^\s*namespace\s+([A-Za-z0-9_\.]+)\s*;\s*$") {
                $namespace = $Matches[1]
            } elseif ($line -match "^\s*namespace\s+([A-Za-z0-9_\.]+)\s*\{\s*$") {
                $namespace = $Matches[1]
            }
        }

        if ($line -match "^\s*(public|internal|protected|private)?\s*static\s+(partial\s+)?class\s+([A-Za-z0-9_]+)\b") {
            $className = $Matches[3]
            $repoRootPath = if ($RepoRoot -is [System.Management.Automation.PathInfo]) { $RepoRoot.Path } else { $RepoRoot }
            $relativePath = $file.FullName.Substring($repoRootPath.Length).TrimStart("\", "/") -replace "\\", "/"
            $results.Add([pscustomobject]@{
                Namespace = $namespace
                ClassName = $className
                File = $relativePath
                Line = $i + 1
            })
        }
    }
}

$results = $results | Sort-Object Namespace, ClassName, File, Line

New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null

$linesOut = New-Object System.Collections.Generic.List[string]
$linesOut.Add("# Static Classes")
$linesOut.Add("")
$linesOut.Add("Generated: $(Get-Date -Format "yyyy-MM-dd")")
$linesOut.Add("")
$linesOut.Add("Total: $($results.Count)")
$linesOut.Add("")
$linesOut.Add("| Namespace | Class | File | Line |")
$linesOut.Add("| --- | --- | --- | --- |")

foreach ($item in $results) {
    $ns = if ($item.Namespace) { $item.Namespace } else { "(none)" }
    $linesOut.Add("| $ns | $($item.ClassName) | $($item.File) | $($item.Line) |")
}

Set-Content -Path $OutFile -Value $linesOut -Encoding UTF8

Write-Host "Wrote $($results.Count) static classes to $OutFile"