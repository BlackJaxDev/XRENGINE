param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [int]$Iterations = 20,
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'

if ($Iterations -lt 1) {
    throw 'Iterations must be greater than zero.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputRoot = Join-Path $repoRoot "Build\_AgentValidation\$stamp-physics-debug-frame-benchmark\reports"
}

$outputPath = [System.IO.Path]::GetFullPath($OutputRoot)
$repoPrefix = $repoRoot.TrimEnd('\') + '\'
if (-not $outputPath.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must remain inside the repository: $outputPath"
}

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$resultPath = Join-Path $outputPath 'physics-debug-frame-benchmarks.trx'
$env:XRE_PHYSICS_DEBUG_BENCH_ITERATIONS = $Iterations.ToString(
    [System.Globalization.CultureInfo]::InvariantCulture)

try {
    dotnet test (Join-Path $repoRoot 'XREngine.UnitTests\XREngine.UnitTests.csproj') `
        --configuration $Configuration `
        --filter 'FullyQualifiedName~PhysicsDebugFrameBenchmarks' `
        -- NUnit.ExplicitMode=Only `
        --logger "trx;LogFileName=$resultPath"
}
finally {
    Remove-Item Env:\XRE_PHYSICS_DEBUG_BENCH_ITERATIONS -ErrorAction SilentlyContinue
}

Write-Host "Physics debug frame benchmark results: $resultPath"
