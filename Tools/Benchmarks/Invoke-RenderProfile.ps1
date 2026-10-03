[CmdletBinding()]
param(
    [ValidateSet('Quick', 'Compare', 'Gate')][string]$Preset = 'Quick',
    [string]$ExecutablePath = '',
    [string]$BaselineExecutablePath = '',
    [string]$CandidateExecutablePath = '',
    [string]$RecipePath = 'docs/examples/profiling/recipes/component-quick.jsonc',
    [string]$BaselineRecipePath = '',
    [string]$CandidateRecipePath = '',
    [string]$RunRoot = '',
    [ValidateSet('component', 'presentationless')][string]$Lane = 'component',
    [switch]$DiagnosticComparison,
    [switch]$ObserverOverhead,
    [switch]$AllowWorkerVariation,
    [switch]$AllowMutationVariation,
    [ValidateRange(0, 100)][double]$MaximumCoefficientOfVariationPercent = 5,
    [ValidateRange(0, 100)][double]$MaximumRegressionPercent = 2,
    [ValidateRange(0, 100000)][double]$MaximumP95Milliseconds = 100000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$validationRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'Build\_AgentValidation'))
function Resolve-InputPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
}
function Assert-File([string]$Path, [string]$Description) {
    $resolved = Resolve-InputPath $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "$Description does not exist: $resolved" }
    return $resolved
}

if ($ObserverOverhead -and -not $DiagnosticComparison) {
    throw '-ObserverOverhead requires -DiagnosticComparison.'
}
if ($Preset -eq 'Gate' -and $DiagnosticComparison) {
    throw 'Gate requires clean comparison evidence; use Compare for diagnostics.'
}
if ($Preset -eq 'Quick') {
    if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
        throw 'Quick requires -ExecutablePath for an already built RenderBench executable.'
    }
    $quickExecutable = Assert-File $ExecutablePath 'RenderBench executable'
    $quickRecipe = Assert-File $RecipePath 'RenderBench recipe'
} else {
    if ([string]::IsNullOrWhiteSpace($BaselineExecutablePath) -or
        [string]::IsNullOrWhiteSpace($CandidateExecutablePath)) {
        throw "$Preset requires -BaselineExecutablePath and -CandidateExecutablePath for independently identified variants."
    }
    $baselineExecutable = Assert-File $BaselineExecutablePath 'Baseline RenderBench executable'
    $candidateExecutable = Assert-File $CandidateExecutablePath 'Candidate RenderBench executable'
    $baselineRecipe = Assert-File $(if ($BaselineRecipePath) { $BaselineRecipePath } else { $RecipePath }) 'Baseline recipe'
    $candidateRecipe = Assert-File $(if ($CandidateRecipePath) { $CandidateRecipePath } else { $RecipePath }) 'Candidate recipe'
}

$createRunRoot = [string]::IsNullOrWhiteSpace($RunRoot)
if ($createRunRoot) {
    & (Join-Path $repoRoot 'Tools\Limit-AgentValidation.ps1') -ReserveTaskRun | Out-Null
    $RunRoot = "Build\_AgentValidation\$(Get-Date -Format yyyyMMdd-HHmmss)-render-profile-$($Preset.ToLowerInvariant())"
}
$run = Resolve-InputPath $RunRoot
if (-not [IO.Path]::GetDirectoryName($run).Equals($validationRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($run) -notmatch '^\d{8}-\d{6}-[A-Za-z0-9][A-Za-z0-9._-]*$') {
    throw 'RunRoot must be one new task directory directly under Build/_AgentValidation.'
}
if ($createRunRoot) {
    if (Test-Path -LiteralPath $run) { throw "New RunRoot already exists: $run" }
} elseif (-not (Test-Path -LiteralPath $run -PathType Container)) {
    throw "Existing RunRoot does not exist: $run"
}
$invocation = Join-Path (Join-Path $run 'reports') "profile-$(Get-Date -Format yyyyMMdd-HHmmss)-$([guid]::NewGuid().ToString('N'))"
$reports = Join-Path $invocation 'reports'
$logs = Join-Path $invocation 'logs'
$scratch = Join-Path $invocation 'scratch'
foreach ($directory in @($run, $invocation, $reports, $logs, $scratch)) {
    [void][IO.Directory]::CreateDirectory($directory)
}

function Invoke-Child([string]$Label, [string]$Executable, [string]$Recipe) {
    $output = Join-Path $reports $Label
    [void][IO.Directory]::CreateDirectory($output)
    $stdoutPath = Join-Path $logs "$Label.stdout.log"
    $stderrPath = Join-Path $logs "$Label.stderr.log"
    $start = [Diagnostics.ProcessStartInfo]::new()
    $extension = [IO.Path]::GetExtension($Executable)
    if ($extension.Equals('.dll', [StringComparison]::OrdinalIgnoreCase)) {
        $start.FileName = 'dotnet'
        [void]$start.ArgumentList.Add($Executable)
    } elseif ($extension.Equals('.exe', [StringComparison]::OrdinalIgnoreCase)) {
        $start.FileName = $Executable
    } else {
        throw "RenderBench child must be a .dll or .exe: $Executable"
    }
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    [void]$start.ArgumentList.Add('--output-dir')
    [void]$start.ArgumentList.Add($output)
    [void]$start.ArgumentList.Add('--recipe-file')
    [void]$start.ArgumentList.Add($Recipe)
    [void]$start.ArgumentList.Add('--mcp-policy')
    [void]$start.ArgumentList.Add('Disabled')
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw "Could not start $Label." }
    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $recipeData = Get-Content -LiteralPath $Recipe -Raw | ConvertFrom-Json -Depth 100
        $limitSeconds = [math]::Max(30, [int]$recipeData.timeout_seconds + 30)
        $timedOut = $false
        if (-not $process.WaitForExit($limitSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            $timedOut = $true
        }
        [IO.File]::WriteAllText($stdoutPath, $stdoutTask.GetAwaiter().GetResult())
        [IO.File]::WriteAllText($stderrPath, $stderrTask.GetAwaiter().GetResult())
        if ($timedOut) { throw "$Label exceeded its $limitSeconds second process limit; see $stderrPath" }
        if ($process.ExitCode -ne 0) { throw "$Label exited $($process.ExitCode); see $stderrPath" }
    } finally {
        $process.Dispose()
    }
    $matches = @(Get-ChildItem -LiteralPath (Join-Path $output 'profiles') -Filter 'render-bench-result.json' -File -Recurse -ErrorAction SilentlyContinue)
    if ($matches.Count -ne 1) { throw "$Label produced $($matches.Count) result files; expected one." }
    return $matches[0].FullName
}

if ($Preset -eq 'Quick') {
    $copiedRecipe = Join-Path $scratch 'quick.jsonc'
    Copy-Item -LiteralPath $quickRecipe -Destination $copiedRecipe
    $result = Invoke-Child 'quick' $quickExecutable $copiedRecipe
    Write-Output "Render profile result: $result"
    return
}

$copiedBaseline = Join-Path $scratch 'baseline.jsonc'
$copiedCandidate = Join-Path $scratch 'candidate.jsonc'
Copy-Item -LiteralPath $baselineRecipe -Destination $copiedBaseline
Copy-Item -LiteralPath $candidateRecipe -Destination $copiedCandidate
$baselineResults = [Collections.Generic.List[string]]::new()
$candidateResults = [Collections.Generic.List[string]]::new()
foreach ($label in @('a1', 'b1', 'b2', 'a2', 'a3', 'b3', 'b4', 'a4')) {
    if ($label.StartsWith('a')) {
        $baselineResults.Add((Invoke-Child $label $baselineExecutable $copiedBaseline))
    } else {
        $candidateResults.Add((Invoke-Child $label $candidateExecutable $copiedCandidate))
    }
}
$comparison = Join-Path $PSScriptRoot 'Invoke-RenderProfileComparison.ps1'
$arguments = @{
    RunRoot = $invocation
    BaselineRecipePath = $copiedBaseline
    CandidateRecipePath = $copiedCandidate
    BaselineResultPaths = $baselineResults.ToArray()
    CandidateResultPaths = $candidateResults.ToArray()
    Lane = $Lane
    MaximumCoefficientOfVariationPercent = $MaximumCoefficientOfVariationPercent
    MaximumRegressionPercent = $MaximumRegressionPercent
    MaximumP95Milliseconds = $MaximumP95Milliseconds
    DiagnosticComparison = $DiagnosticComparison
    ObserverOverhead = $ObserverOverhead
    AllowWorkerVariation = $AllowWorkerVariation
    AllowMutationVariation = $AllowMutationVariation
}
& $comparison @arguments
if (-not $?) { throw "Comparison failed; see $(Join-Path $invocation 'render-profile-comparison.json')." }
Move-Item -LiteralPath (Join-Path $invocation 'render-profile-comparison.json') -Destination (Join-Path $reports 'render-profile-comparison.json')
