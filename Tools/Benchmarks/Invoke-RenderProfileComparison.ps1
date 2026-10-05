<#
.SYNOPSIS
Compare repeated RenderBench component captures under one bounded run root.
.DESCRIPTION
Inputs must be independent process results captured in repeated A/B/B/A order.
Only name, worker_counts, and mutation may vary in recipes, and the latter two
require explicit switches. The command never changes a result or recipe file.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][string]$BaselineRecipePath,
    [Parameter(Mandatory)][string]$CandidateRecipePath,
    [Parameter(Mandatory)][string[]]$BaselineResultPaths,
    [Parameter(Mandatory)][string[]]$CandidateResultPaths,
    [ValidateSet('component', 'presentationless', 'desktop_wsi', 'open_xr')][string]$Lane = 'component',
    [ValidateSet('Auto', 'Cpu', 'Gpu')][string]$Metric = 'Auto',
    [switch]$AllowWorkerVariation,
    [switch]$AllowMutationVariation,
    [switch]$DiagnosticComparison,
    [switch]$ObserverOverhead,
    [ValidateRange(0, 100)][double]$MaximumCoefficientOfVariationPercent = 5,
    [ValidateRange(0, 100)][double]$MaximumRegressionPercent = 2,
    [ValidateRange(0, 100000)][double]$MaximumP95Milliseconds = 100000,
    [ValidateRange(0, 100000)][double]$FullFrameBaselineP95Milliseconds = 0,
    [string]$FullFrameComparisonReportPath,
    [string]$AcceptedBaselinePath,
    [switch]$AcceptBaseline,
    [switch]$ReplaceAcceptedBaseline
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-UnderRoot([string]$Path, [string]$Root) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Evidence path is outside the bounded run root: $Path"
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Missing evidence: $resolved" }
    return $resolved
}

function Read-Json([string]$Path, [string]$Root) {
    $resolved = Assert-UnderRoot $Path $Root
    return Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json -Depth 100
}

function Required($Object, [string]$Name, [string]$Label) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value -or
        ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace($property.Value))) {
        throw "$Label lacks required field '$Name'."
    }
    return $property.Value
}

function Present($Object, [string]$Name, [string]$Label) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { throw "$Label lacks required field '$Name'." }
    return $property.Value
}

function Optional-Property($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Stable-Value($Value) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [ValueType]) { return $Value }
    if ($Value -is [array]) {
        $items = @($Value | ForEach-Object { Stable-Value $_ })
        return ,$items
    }
    $properties = if ($Value -is [Collections.IDictionary]) { @($Value.Keys) } else {
        @($Value.PSObject.Properties | ForEach-Object { $_.Name })
    }
    $sorted = [ordered]@{}
    foreach ($key in ($properties | Sort-Object -CaseSensitive)) {
        $child = if ($Value -is [Collections.IDictionary]) { $Value[$key] } else { $Value.PSObject.Properties[$key].Value }
        $sorted[$key] = Stable-Value $child
    }
    return $sorted
}

function Canonical($Object) { return ConvertTo-Json -InputObject (Stable-Value $Object) -Depth 100 -Compress }

function Assert-RequestedRecipe($Requested, $Emitted, [string]$Path) {
    if ($Requested -is [pscustomobject]) {
        foreach ($property in $Requested.PSObject.Properties) {
            $actual = $Emitted.PSObject.Properties[$property.Name]
            if ($null -eq $actual) { throw "Emitted recipe lacks requested '$Path.$($property.Name)'." }
            Assert-RequestedRecipe $property.Value $actual.Value "$Path.$($property.Name)"
        }
        return
    }
    if ($Requested -is [array]) {
        if ($Emitted -isnot [array] -or $Requested.Count -ne $Emitted.Count) {
            throw "Emitted recipe differs from requested '$Path'."
        }
        for ($index = 0; $index -lt $Requested.Count; $index++) {
            Assert-RequestedRecipe $Requested[$index] $Emitted[$index] "$Path[$index]"
        }
        return
    }
    if ($Path -eq 'recipe.instrumentation') {
        $requestedFlags = @(([string]$Requested).Split(',') | ForEach-Object { $_.Trim() } | Sort-Object)
        $emittedFlags = @(([string]$Emitted).Split(',') | ForEach-Object { $_.Trim() } | Sort-Object)
        if ((Canonical $requestedFlags) -ceq (Canonical $emittedFlags)) { return }
    } elseif ((Canonical $Requested) -ceq (Canonical $Emitted)) { return }
    throw "Emitted recipe differs from requested '$Path'."
}

function Normalize-Recipe($Recipe, [bool]$Workers, [bool]$Mutation, [bool]$Observer) {
    $copy = ConvertFrom-Json -InputObject (Canonical $Recipe) -Depth 100
    $copy.PSObject.Properties.Remove('name')
    if ($Workers) { $copy.PSObject.Properties.Remove('worker_counts') }
    if ($Mutation) { $copy.PSObject.Properties.Remove('mutation') }
    if ($Observer) {
        foreach ($field in @('profile_mode', 'instrumentation', 'label_policy',
                             'hardware_counter_policy', 'cpu_sampling_policy', 'cpu_profiling',
                             'gpu_profiling', 'enable_validation', 'enable_synchronization_validation')) {
            $copy.PSObject.Properties.Remove($field)
        }
    }
    return Canonical $copy
}

function Normalize-Fixture($Fixture, [bool]$Workers, [bool]$Mutation) {
    $copy = ConvertFrom-Json -InputObject (Canonical $Fixture) -Depth 100
    if ($Workers) { $copy.PSObject.Properties.Remove('workerCount') }
    if ($Mutation) { $copy.PSObject.Properties.Remove('mutationPolicy') }
    return Canonical $copy
}

function Compare-Identity($A, $B, [string]$Label) {
    foreach ($key in @('schemaVersion', 'backend', 'executionMode', 'fixture', 'workloadSha256',
                      'adapterName', 'vendorId', 'deviceId', 'driverVersion', 'presentationDescription',
                      'output', 'outputSha256', 'layoutPolicy')) {
        if ((Canonical (Required $A $key 'baseline result')) -cne
            (Canonical (Required $B $key $Label))) {
            throw "$Label is incompatible at '$key'."
        }
    }
    if ((Normalize-Fixture (Required $A 'fixtureManifest' 'baseline result') $AllowWorkerVariation $AllowMutationVariation) -cne
        (Normalize-Fixture (Required $B 'fixtureManifest' $Label) $AllowWorkerVariation $AllowMutationVariation)) {
        throw "$Label has an incompatible fixture manifest."
    }
    foreach ($key in @('presentationTarget', 'outputCount', 'graphicsQueueFamily', 'presentPolicy')) {
        if ((Canonical (Required $A.targetManifest $key 'baseline target')) -cne
            (Canonical (Required $B.targetManifest $key "$Label target"))) {
            throw "$Label is incompatible at targetManifest.$key."
        }
    }
    $environmentFields = @('operatingSystem', 'processPriority', 'extensionManifest',
                           'powerPolicy', 'clockPolicy', 'targetRefreshHertz')
    if (-not $ObserverOverhead) { $environmentFields += @('profileMode', 'instrumentationManifest') }
    foreach ($key in $environmentFields) {
        if ((Canonical (Present $A.environment $key 'baseline environment')) -cne
            (Canonical (Present $B.environment $key "$Label environment"))) {
            throw "$Label is incompatible at environment.$key."
        }
    }
    if (-not $DiagnosticComparison) {
        if ([string](Required $A.environment 'profileMode' 'baseline environment') -notin @('CleanProfile', 'ReleaseBenchmark') -or
            [string](Required $B.environment 'profileMode' "$Label environment") -notin @('CleanProfile', 'ReleaseBenchmark')) {
            throw 'Intrusive or diagnostic captures cannot be used for clean comparison.'
        }
    }
}

function Validate-Result($Result, [string]$Label, [double]$MaximumVariance, [double]$MaximumP95) {
    if ((Required $Result 'schemaVersion' $Label) -ne 2) { throw "$Label uses an unsupported result schema." }
    $expectedMode = switch ($Lane) {
        'component' { 'Component' }
        'presentationless' { 'Presentationless' }
        'desktop_wsi' { 'DesktopWsi' }
        'open_xr' { 'OpenXr' }
    }
    if ((Required $Result 'executionMode' $Label) -ne $expectedMode) {
        throw "$Label is not a $Lane capture."
    }
    $cpu = Required $Result 'cpuFrameStatistics' $Label
    $gpu = Required $Result 'gpuFrameStatistics' $Label
    if ($cpu.n -ne @($Result.cpuFrameNanoseconds).Count -or
        $gpu.n -ne @($Result.gpuFrameNanoseconds).Count -or
        $cpu.n -ne [int](Required $Result 'captureFrames' $Label)) {
        throw "$Label statistics do not match the raw capture frame counts."
    }
    foreach ($metric in @($cpu, $gpu)) {
        foreach ($key in @('n', 'p50', 'p90', 'p95', 'p99', 'worst', 'mean', 'standardDeviation', 'medianAbsoluteDeviation')) {
            [void](Required $metric $key "$Label metric")
        }
        if ($metric.n -lt 1 -or $metric.mean -le 0 -or $metric.p95 -gt $MaximumP95) {
            throw "$Label has empty, invalid, or over-budget metric samples."
        }
        if (($metric.standardDeviation / $metric.mean * 100) -gt $MaximumVariance) {
            throw "$Label exceeds the variance limit of $MaximumVariance percent."
        }
    }
    foreach ($gate in (Required $Result 'stabilityGates' $Label)) {
        if (-not $gate.passed) { throw "$Label failed validity gate '$($gate.name)'." }
    }
    if (@($Result.stabilityGates).Count -eq 0) { throw "$Label has no validity gates." }
    $source = Required $Result 'source' $Label
    $executablePath = Required $Result 'executablePath' $Label
    $executableHash = Required $Result 'executableSha256' $Label
    if ($executableHash -cne (Required $source 'executableSha256' "$Label source")) {
        throw "$Label source executable hash differs from the result."
    }
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash -cne $executableHash) {
        throw "$Label executable bytes do not match its recorded SHA-256."
    }
    [void](Required $Result 'targetManifest' $Label)
    [void](Required $Result 'workCounters' $Label)
    [void](Required $Result 'outputSha256' $Label)
    if (-not $DiagnosticComparison -and [bool](Required $Result 'isIntrusive' $Label)) {
        throw "$Label is intrusive and cannot be used for clean comparison."
    }
    if (-not $DiagnosticComparison -and $null -eq (Present $Result 'allocatedBytesOnFixtureWorkers' $Label)) {
        throw "$Label has unmeasured worker allocations and cannot establish clean allocation evidence."
    }
    foreach ($key in @('commit', 'executableSha256', 'buildConfiguration', 'backendModuleGeneration')) {
        [void](Required $source $key "$Label source")
    }
    if (-not $DiagnosticComparison -and [bool](Required $source 'dirtyWorktree' "$Label source")) {
        throw "$Label comes from a dirty source tree."
    }
    $environment = Required $Result 'environment' $Label
    foreach ($key in @('operatingSystem', 'processPriority', 'profileMode', 'instrumentationManifest', 'extensionManifest')) {
        [void](Required $environment $key "$Label environment")
    }
    foreach ($key in @('powerPolicy', 'clockPolicy', 'targetRefreshHertz', 'thermalNotes', 'competingWorkloadWarnings')) {
        [void](Present $environment $key "$Label environment")
    }
    if (-not $DiagnosticComparison -and
        [string]$environment.profileMode -notin @('CleanProfile', 'ReleaseBenchmark')) {
        throw "$Label is not a clean capture."
    }
    foreach ($key in @('warmupStartUtc', 'warmupEndUtc', 'stabilityStartUtc', 'stabilityEndUtc',
                      'captureStartUtc', 'captureEndUtc', 'drainStartUtc', 'drainEndUtc',
                      'processStartUtc', 'processEndUtc')) {
        [void](Required $Result.intervals $key "$Label intervals")
    }
    [void](Required $Result 'intervals' $Label)
    $artifacts = Required $Result 'artifactManifest' $Label
    foreach ($key in @('runRoot', 'recipePath', 'effectiveConfigurationPath', 'workloadIdentityPath', 'resultPath')) {
        [void](Required $artifacts $key "$Label artifacts")
    }
    $recipePath = Assert-UnderRoot $artifacts.recipePath $root
    if ((Get-FileHash -LiteralPath $recipePath -Algorithm SHA256).Hash -cne
        (Required $Result 'recipeSha256' $Label)) {
        throw "$Label emitted recipe bytes do not match its recorded SHA-256."
    }
}

$root = [IO.Path]::GetFullPath($RunRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Run root does not exist: $root" }
if ($AcceptBaseline -and [string]::IsNullOrWhiteSpace($AcceptedBaselinePath)) {
    throw '-AcceptBaseline requires -AcceptedBaselinePath.'
}
if ($ReplaceAcceptedBaseline -and -not $AcceptBaseline) { throw '-ReplaceAcceptedBaseline requires -AcceptBaseline.' }
if ($DiagnosticComparison -and $AcceptBaseline) { throw 'Diagnostic comparisons cannot accept a baseline.' }
if ($ObserverOverhead -and -not $DiagnosticComparison) { throw '-ObserverOverhead requires -DiagnosticComparison.' }
if ($BaselineResultPaths.Count -lt 4 -or $CandidateResultPaths.Count -lt 4 -or
    $BaselineResultPaths.Count -ne $CandidateResultPaths.Count -or
    ($BaselineResultPaths.Count % 2) -ne 0) {
    throw 'Comparison requires at least four independent process results per side in complete A/B/B/A blocks.'
}
$baselineRecipe = Read-Json $BaselineRecipePath $root
$candidateRecipe = Read-Json $CandidateRecipePath $root
$baseline = @($BaselineResultPaths | ForEach-Object { Read-Json $_ $root })
$candidate = @($CandidateResultPaths | ForEach-Object { Read-Json $_ $root })
$baselineEmitted = Read-Json $baseline[0].artifactManifest.recipePath $root
$candidateEmitted = Read-Json $candidate[0].artifactManifest.recipePath $root
Assert-RequestedRecipe $baselineRecipe $baselineEmitted 'recipe'
Assert-RequestedRecipe $candidateRecipe $candidateEmitted 'recipe'
if ((Normalize-Recipe $baselineEmitted $AllowWorkerVariation $AllowMutationVariation $ObserverOverhead) -cne
    (Normalize-Recipe $candidateEmitted $AllowWorkerVariation $AllowMutationVariation $ObserverOverhead)) {
    throw 'Recipes differ beyond explicitly permitted name, worker count, mutation, or observer settings.'
}
foreach ($entry in @(@{ Results = $baseline; Recipe = $baselineRecipe; Emitted = $baselineEmitted },
                     @{ Results = $candidate; Recipe = $candidateRecipe; Emitted = $candidateEmitted })) {
    foreach ($result in $entry.Results) {
        $emittedRecipe = Read-Json $result.artifactManifest.recipePath $root
        Assert-RequestedRecipe $entry.Recipe $emittedRecipe 'recipe'
        if ((Normalize-Recipe $emittedRecipe $AllowWorkerVariation $AllowMutationVariation $ObserverOverhead) -cne
            (Normalize-Recipe $entry.Emitted $AllowWorkerVariation $AllowMutationVariation $ObserverOverhead)) {
            throw "Run $($result.runId) emitted recipe is incompatible with its declared comparison recipe."
        }
    }
}
if ($ObserverOverhead) {
    if ($baseline[0].environment.profileMode -notin @('CleanProfile', 'ReleaseBenchmark')) {
        throw 'Observer overhead baseline must use CleanProfile or ReleaseBenchmark.'
    }
    if ($candidate[0].environment.profileMode -notin @('DevelopmentProfile', 'Diagnostics')) {
        throw 'Observer overhead candidate must use DevelopmentProfile or Diagnostics.'
    }
    if ([string]$baseline[0].environment.instrumentationManifest -ceq
        [string]$candidate[0].environment.instrumentationManifest) {
        throw 'Observer overhead comparison requires a recorded instrumentation difference.'
    }
}
$ordered = @()
for ($i = 0; $i -lt $baseline.Count; $i += 2) {
    $ordered += [pscustomobject]@{ Side = 'A'; Result = $baseline[$i] }
    $ordered += [pscustomobject]@{ Side = 'B'; Result = $candidate[$i] }
    $ordered += [pscustomobject]@{ Side = 'B'; Result = $candidate[$i + 1] }
    $ordered += [pscustomobject]@{ Side = 'A'; Result = $baseline[$i + 1] }
}
$runIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$processIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $ordered) {
    $result = $entry.Result
    $label = "run $($result.runId)"
    Validate-Result $result $label $MaximumCoefficientOfVariationPercent $MaximumP95Milliseconds
    Compare-Identity $baseline[0] $result $label
    if (-not $runIds.Add([string](Required $result 'runId' $label))) { throw 'Repeated runId in comparison.' }
    $processKey = "$($result.processId):$($result.startedUtc)"
    if (-not $processIds.Add($processKey)) { throw 'Repeated process capture in comparison.' }
}
foreach ($group in @(@{ Name = 'baseline'; Results = $baseline },
                     @{ Name = 'candidate'; Results = $candidate })) {
    $expected = $group.Results[0].source
    foreach ($item in $group.Results) {
        foreach ($key in @('commit', 'dirtyWorktree', 'executableSha256', 'buildConfiguration', 'backendModuleGeneration')) {
            if ((Canonical (Required $expected $key 'variant source')) -cne
                (Canonical (Required $item.source $key 'variant source'))) {
                throw "$($group.Name) runs differ in source.$key."
            }
        }
        foreach ($key in @('profileMode', 'instrumentationManifest')) {
            if ((Canonical (Required $group.Results[0].environment $key 'variant environment')) -cne
                (Canonical (Required $item.environment $key 'variant environment'))) {
                throw "$($group.Name) runs differ in environment.$key."
            }
        }
        foreach ($key in @('workerCount', 'mutationPolicy')) {
            if ((Canonical (Required $group.Results[0].fixtureManifest $key 'variant fixture')) -cne
                (Canonical (Required $item.fixtureManifest $key 'variant fixture'))) {
                throw "$($group.Name) runs differ in fixtureManifest.$key."
            }
        }
    }
}
for ($i = 1; $i -lt $ordered.Count; $i++) {
    if ([datetimeoffset]$ordered[$i].Result.startedUtc -le [datetimeoffset]$ordered[$i - 1].Result.completedUtc) {
        throw 'Capture intervals overlap or violate A/B/B/A time order.'
    }
}
$metricUsed = if ($Metric -ne 'Auto') { $Metric } elseif ($baseline[0].fixtureManifest.kind -in @('GpuPass', 'FullPresentationless')) { 'Gpu' } else { 'Cpu' }
$metricField = if ($metricUsed -eq 'Cpu') { 'cpuFrameStatistics' } else { 'gpuFrameStatistics' }
$aP95 = [double](($baseline | ForEach-Object { [double]$_.$metricField.p95 } | Measure-Object -Average).Average)
$bP95 = [double](($candidate | ForEach-Object { [double]$_.$metricField.p95 } | Measure-Object -Average).Average)
$aCpuP95 = [double](($baseline | ForEach-Object { [double]$_.cpuFrameStatistics.p95 } | Measure-Object -Average).Average)
$bCpuP95 = [double](($candidate | ForEach-Object { [double]$_.cpuFrameStatistics.p95 } | Measure-Object -Average).Average)
$aGpuP95 = [double](($baseline | ForEach-Object { [double]$_.gpuFrameStatistics.p95 } | Measure-Object -Average).Average)
$bGpuP95 = [double](($candidate | ForEach-Object { [double]$_.gpuFrameStatistics.p95 } | Measure-Object -Average).Average)
if ($aP95 -le 0) { throw "Baseline $metricUsed p95 is invalid." }
$aValues = @($baseline | ForEach-Object { [double]$_.$metricField.p95 })
$bValues = @($candidate | ForEach-Object { [double]$_.$metricField.p95 })
foreach ($side in @(@{ Name = 'baseline'; Values = $aValues; Mean = $aP95 },
                     @{ Name = 'candidate'; Values = $bValues; Mean = $bP95 })) {
    $sumSquares = 0.0
    foreach ($value in $side.Values) { $sumSquares += [math]::Pow($value - $side.Mean, 2) }
    $cv = [math]::Sqrt($sumSquares / ($side.Values.Count - 1)) / $side.Mean * 100
    if ($cv -gt $MaximumCoefficientOfVariationPercent) {
        throw "$($side.Name) process-to-process $metricUsed p95 variance $([math]::Round($cv, 3))% exceeds $MaximumCoefficientOfVariationPercent%."
    }
}
$deltaPercent = ($bP95 / $aP95 - 1) * 100
$issues = @()
if (-not $DiagnosticComparison -and $deltaPercent -gt $MaximumRegressionPercent) {
    $issues += "$metricUsed p95 regression $([math]::Round($deltaPercent, 3))% exceeds $MaximumRegressionPercent%."
}
if (-not $DiagnosticComparison) {
    foreach ($field in @('cpuFrameStatistics', 'gpuFrameStatistics')) {
        foreach ($tail in @('p95', 'p99')) {
            $before = [double](($baseline | ForEach-Object { [double]$_.$field.$tail } | Measure-Object -Average).Average)
            $after = [double](($candidate | ForEach-Object { [double]$_.$field.$tail } | Measure-Object -Average).Average)
            if ($before -le 0 -or $after -gt $before * (1 + $MaximumRegressionPercent / 100)) {
                $issues += "$field $tail regressed or lacks a valid baseline."
            }
        }
    }
}
$laneSavings = $aP95 - $bP95
$evidenceScope = if ($Lane -eq 'component') {
    'component'
} elseif ($baseline[0].fixtureManifest.name -in @('presentationless-deferred', 'presentationless-uber') -or
    $candidate[0].fixtureManifest.name -in @('presentationless-deferred', 'presentationless-uber') -or
    [string](Optional-Property $baseline[0].fixtureManifest 'evidenceScope') -cne 'productionFullFrame' -or
    [string](Optional-Property $candidate[0].fixtureManifest 'evidenceScope') -cne 'productionFullFrame') {
    'syntheticProxy'
} else {
    'productionFullFrame'
}
$share = if ($FullFrameBaselineP95Milliseconds -gt 0) { $aP95 / $FullFrameBaselineP95Milliseconds } else { $null }
$theoreticalCeiling = if ($null -ne $share) { [math]::Min(100, $share * 100) } else { $null }
$fullFrameSavings = $null
if ($FullFrameComparisonReportPath) {
    $fullFrameReport = Read-Json $FullFrameComparisonReportPath $root
    if ($fullFrameReport.status -ne 'pass' -or $fullFrameReport.lane -ne 'presentationless' -or
        $fullFrameReport.evidenceKind -ne 'cleanComparison' -or
        (Optional-Property $fullFrameReport 'evidenceScope') -cne 'productionFullFrame') {
        throw 'Full-frame evidence must be a passing, compatible production full-frame presentationless comparison report.'
    }
    foreach ($side in @('baseline', 'candidate')) {
        $componentSource = if ($side -eq 'baseline') { $baseline[0].source } else { $candidate[0].source }
        $frameVariant = Required $fullFrameReport.variantIdentity $side 'full-frame variant identity'
        foreach ($key in @('commit', 'dirtyWorktree', 'executableSha256', 'buildConfiguration', 'backendModuleGeneration')) {
            if ((Canonical (Required $componentSource $key "component $side source")) -cne
                (Canonical (Required $frameVariant.source $key "full-frame $side source"))) {
                throw "Full-frame $side source.$key does not match the component variant."
            }
        }
        $componentFixture = if ($side -eq 'baseline') { $baseline[0].fixtureManifest } else { $candidate[0].fixtureManifest }
        foreach ($key in @('workerCount', 'mutationPolicy')) {
            if ((Canonical (Required $componentFixture $key "component $side fixture")) -cne
                (Canonical (Required $frameVariant $key "full-frame $side variant"))) {
                throw "Full-frame $side $key does not match the component experiment."
            }
        }
    }
    $fullFrameSavings = [double](Required $fullFrameReport.scoreboard 'demonstratedFullFrameSavingsMilliseconds' 'full-frame scoreboard')
    if (-not [double]::IsFinite($fullFrameSavings) -or $fullFrameSavings -le 0) {
        throw 'Full-frame evidence has no finite positive demonstrated saving.'
    }
}
$report = [ordered]@{
    schemaVersion = 1
    generatedUtc = [datetimeoffset]::UtcNow
    lane = $Lane
    evidenceScope = $evidenceScope
    status = if ($issues.Count -gt 0) { 'fail' } elseif ($DiagnosticComparison) { 'diagnostic' } else { 'pass' }
    evidenceKind = if ($ObserverOverhead) { 'observerOverhead' } elseif ($DiagnosticComparison) { 'diagnosticComparison' } else { 'cleanComparison' }
    baselineRecipePath = (Assert-UnderRoot $BaselineRecipePath $root)
    candidateRecipePath = (Assert-UnderRoot $CandidateRecipePath $root)
    baselineResultPaths = @($BaselineResultPaths | ForEach-Object { Assert-UnderRoot $_ $root })
    candidateResultPaths = @($CandidateResultPaths | ForEach-Object { Assert-UnderRoot $_ $root })
    ordering = 'ABBA'
    repetitionsPerVariant = $baseline.Count
    permittedVariation = @{ workerCounts = [bool]$AllowWorkerVariation; mutation = [bool]$AllowMutationVariation; observer = [bool]$ObserverOverhead }
    variantIdentity = @{
        baseline = @{ source = $baseline[0].source; profileMode = $baseline[0].environment.profileMode;
            instrumentationManifest = $baseline[0].environment.instrumentationManifest;
            workerCount = $baseline[0].fixtureManifest.workerCount; mutationPolicy = $baseline[0].fixtureManifest.mutationPolicy }
        candidate = @{ source = $candidate[0].source; profileMode = $candidate[0].environment.profileMode;
            instrumentationManifest = $candidate[0].environment.instrumentationManifest;
            workerCount = $candidate[0].fixtureManifest.workerCount; mutationPolicy = $candidate[0].fixtureManifest.mutationPolicy }
    }
    thresholds = @{ maximumCoefficientOfVariationPercent = $MaximumCoefficientOfVariationPercent; maximumRegressionPercent = $MaximumRegressionPercent; maximumP95Milliseconds = $MaximumP95Milliseconds }
    metric = $metricUsed
    baselineSelectedP95Milliseconds = $aP95
    candidateSelectedP95Milliseconds = $bP95
    baselineGpuP95Milliseconds = $aGpuP95
    candidateGpuP95Milliseconds = $bGpuP95
    baselineCpuP95Milliseconds = $aCpuP95
    candidateCpuP95Milliseconds = $bCpuP95
    deltaPercent = $deltaPercent
    cpuDeltaPercent = if ($aCpuP95 -gt 0) { ($bCpuP95 / $aCpuP95 - 1) * 100 } else { $null }
    workCounterBaseline = $baseline[0].workCounters
    workCounterCandidate = $candidate[0].workCounters
    issues = $issues
    scoreboard = @{
        targetCostMilliseconds = $aP95
        fullFrameSharePercent = if ($null -ne $share) { $share * 100 } else { $null }
        theoreticalOpportunityPercent = $theoreticalCeiling
        componentSavingsMilliseconds = if ($Lane -eq 'component') { $laneSavings } else { $null }
        demonstratedFullFrameSavingsMilliseconds = if ($evidenceScope -eq 'productionFullFrame' -and $Lane -eq 'presentationless') { $laneSavings } else { $fullFrameSavings }
        broaderLaneResult = if (($evidenceScope -eq 'productionFullFrame' -and $Lane -eq 'presentationless') -or $null -ne $fullFrameSavings) { 'measured' } else { 'unresolved' }
    }
}
$reportPath = Join-Path $root 'render-profile-comparison.json'
if (Test-Path -LiteralPath $reportPath) { throw "Comparison report already exists: $reportPath" }
$report | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
if ($AcceptBaseline) {
    if ($issues.Count -gt 0) { throw 'A failing comparison cannot be accepted as baseline.' }
    $acceptedPath = [IO.Path]::GetFullPath($AcceptedBaselinePath)
    $acceptedDirectory = [IO.Path]::GetDirectoryName($acceptedPath)
    if (-not (Test-Path -LiteralPath $acceptedDirectory -PathType Container)) { throw 'Accepted baseline directory does not exist.' }
    if ((Test-Path -LiteralPath $acceptedPath) -and -not $ReplaceAcceptedBaseline) {
        throw 'Accepted baseline already exists; explicit -ReplaceAcceptedBaseline is required.'
    }
    $acceptance = @{
        schemaVersion = 1; acceptedUtc = [datetimeoffset]::UtcNow; comparisonReportPath = $reportPath
        recipeSha256 = (Get-FileHash -LiteralPath (Assert-UnderRoot $CandidateRecipePath $root) -Algorithm SHA256).Hash
        resultPaths = $report.candidateResultPaths
        resultSha256 = @($report.candidateResultPaths | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash })
    }
    $temporaryPath = "$acceptedPath.tmp"
    $acceptance | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporaryPath -Destination $acceptedPath -Force
}
$report | ConvertTo-Json -Depth 100
if ($issues.Count -gt 0) { exit 1 }
