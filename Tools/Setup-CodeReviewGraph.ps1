[CmdletBinding()]
param(
    [Parameter()]
    [switch]$InstallPrerequisites,

    [Parameter()]
    [switch]$ForceRecreateEnvironment,

    [Parameter()]
    [switch]$ForceRebuild,

    [Parameter()]
    [switch]$SkipGraph
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$toolRoot = Join-Path $repoRoot "Build\Dependencies\CodeReviewGraph"
$venvRoot = Join-Path $toolRoot "venv"
$venvPython = Join-Path $venvRoot "Scripts\python.exe"
$requirementsPath = Join-Path $PSScriptRoot "Dependencies\code-review-graph-requirements.txt"
$graphDatabasePath = Join-Path $repoRoot ".code-review-graph\graph.db"

function Invoke-Native {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter()]
        [string[]]$Arguments = @(),

        [Parameter()]
        [string]$WorkingDirectory = $repoRoot
    )

    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-PythonLauncher {
    $candidates = [System.Collections.Generic.List[object]]::new()
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($null -ne $py) {
        $candidates.Add([pscustomobject]@{ FilePath = $py.Source; Prefix = @("-3") })
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($null -ne $python) {
        $candidates.Add([pscustomobject]@{ FilePath = $python.Source; Prefix = @() })
    }

    foreach ($minorVersion in 13, 12, 11, 10) {
        $commonPath = Join-Path $env:LOCALAPPDATA "Programs\Python\Python3$minorVersion\python.exe"
        if (Test-Path -LiteralPath $commonPath -PathType Leaf) {
            $candidates.Add([pscustomobject]@{ FilePath = $commonPath; Prefix = @() })
        }
    }

    foreach ($candidate in $candidates) {
        try {
            $versionNumber = & $candidate.FilePath @($candidate.Prefix) -c "import sys; print(sys.version_info.major * 100 + sys.version_info.minor)" 2>$null
            if ($LASTEXITCODE -eq 0 -and [int]$versionNumber -ge 310) {
                return $candidate
            }
        }
        catch {
            continue
        }
    }

    return $null
}

function Install-PythonPrerequisite {
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if ($null -eq $winget) {
        throw "Python 3.10 or newer is required and winget is unavailable. Install Python, then rerun this script."
    }

    Write-Host "Python 3.10+ was not found. Installing Python 3.13 with winget..." -ForegroundColor Yellow
    Invoke-Native -FilePath $winget.Source -Arguments @(
        "install",
        "--id", "Python.Python.3.13",
        "--exact",
        "--source", "winget",
        "--accept-package-agreements",
        "--accept-source-agreements"
    )
}

if (-not (Test-Path -LiteralPath $requirementsPath -PathType Leaf)) {
    throw "Pinned requirements file not found: $requirementsPath"
}

$launcher = Get-PythonLauncher
if ($null -eq $launcher -and $InstallPrerequisites) {
    Install-PythonPrerequisite
    $launcher = Get-PythonLauncher
}

if ($null -eq $launcher) {
    throw "Python 3.10 or newer is required. Install it or rerun with -InstallPrerequisites."
}

if ($ForceRecreateEnvironment -and (Test-Path -LiteralPath $venvRoot -PathType Container)) {
    $resolvedToolRoot = [System.IO.Path]::GetFullPath($toolRoot)
    $resolvedVenvRoot = [System.IO.Path]::GetFullPath($venvRoot)
    if (-not $resolvedVenvRoot.StartsWith($resolvedToolRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove virtual environment outside the code-review-graph tool root: $resolvedVenvRoot"
    }

    Remove-Item -LiteralPath $resolvedVenvRoot -Recurse -Force
}

if (-not (Test-Path -LiteralPath $venvPython -PathType Leaf)) {
    [System.IO.Directory]::CreateDirectory($toolRoot) | Out-Null
    Write-Host "Creating isolated code-review-graph Python environment..." -ForegroundColor Yellow
    Invoke-Native -FilePath $launcher.FilePath -Arguments (@($launcher.Prefix) + @("-m", "venv", $venvRoot))
}

Write-Host "Installing pinned code-review-graph dependencies..." -ForegroundColor Yellow
Invoke-Native -FilePath $venvPython -Arguments @(
    "-m", "pip", "install",
    "--disable-pip-version-check",
    "--requirement", $requirementsPath
)

# The package probes parsers with Python isolated mode. Test that exact path so a
# user-site-only installation cannot silently produce a zero-node graph.
Invoke-Native -FilePath $venvPython -Arguments @(
    "-I", "-c",
    "from tree_sitter_language_pack import get_parser; get_parser('csharp'); import code_review_graph; print('code-review-graph parser probe passed')"
)

if ($SkipGraph) {
    Write-Host "Environment setup complete; graph build was skipped." -ForegroundColor Green
    exit 0
}

$graphCommand = if ($ForceRebuild -or -not (Test-Path -LiteralPath $graphDatabasePath -PathType Leaf)) { "build" } else { "update" }
Write-Host "Running code-review-graph $graphCommand..." -ForegroundColor Yellow
Invoke-Native -FilePath $venvPython -Arguments @("-m", "code_review_graph", $graphCommand, "--repo", $repoRoot)

Write-Host "code-review-graph setup complete." -ForegroundColor Green
Write-Host "Restart Codex after the first setup so it loads the project MCP server and hooks." -ForegroundColor Cyan
