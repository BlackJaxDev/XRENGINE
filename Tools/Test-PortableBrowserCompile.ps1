[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectList = Join-Path $repositoryRoot 'Build/Portable/PortableProjects.tsv'
$projects = @(Get-Content -LiteralPath $projectList |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and -not $_.StartsWith('#', [StringComparison]::Ordinal) })

if ($projects.Count -eq 0) {
    throw 'The reviewed portable project set is empty.'
}

foreach ($project in $projects) {
    $projectFile = Join-Path $repositoryRoot "$project/$project.csproj"
    if (-not (Test-Path -LiteralPath $projectFile -PathType Leaf)) {
        throw "Portable project file is missing: $project"
    }

    Write-Host "Compiling $project for browser-wasm ($Configuration)."
    & dotnet build $projectFile --configuration $Configuration --runtime browser-wasm --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Browser compile failed for $project (exit code $LASTEXITCODE)."
    }
}
