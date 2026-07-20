[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command = "status",

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArguments = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$venvPython = Join-Path $repoRoot "Build\Dependencies\CodeReviewGraph\venv\Scripts\python.exe"

if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    throw "code-review-graph is not set up for this checkout. Run 'powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Setup-CodeReviewGraph.ps1' first."
}

# FastMCP communicates over UTF-8 JSON. Force Python's stdio encoding on Windows.
$env:PYTHONUTF8 = "1"

Push-Location $repoRoot
try {
    & $venvPython -m code_review_graph $Command @RemainingArguments
    if ($LASTEXITCODE -ne 0) {
        throw "code-review-graph $Command failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
