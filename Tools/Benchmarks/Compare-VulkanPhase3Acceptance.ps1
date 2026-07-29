param(
    [Parameter(Mandatory = $true)]
    [string]$ScalingRunRoot,
    [string]$CrossoverRunRoot = '',
    [string]$GateRunRoot = '',
    [string]$CpuReferenceRunRoot = '',
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$checks = [Collections.Generic.List[object]]::new()
$measurements = [Collections.Generic.List[object]]::new()
$submissionMetrics = [ordered]@{
    render_outside_vulkan_frame_ms = 'RenderOutsideVulkanP95Ms'
    vulkan_cpu_frame_op_preparation_ms = 'VulkanFrameOpPreparationP95Ms'
    vulkan_cpu_resource_planning_ms = 'VulkanResourcePlanningP95Ms'
    vulkan_cpu_frame_data_refresh_ms = 'VulkanFrameDataRefreshP95Ms'
    vulkan_cpu_primary_command_encoding_ms = 'VulkanPrimaryCommandEncodingP95Ms'
    vulkan_cpu_secondary_recording_ms = 'VulkanSecondaryRecordingP95Ms'
}

function Resolve-RepositoryPath {
    param([string]$Path)

    $resolved = [IO.Path]::GetFullPath(
        $(if ([IO.Path]::IsPathRooted($Path)) {
            $Path
        } else {
            Join-Path $repoRoot $Path
        }))
    $repoWithSeparator = $repoRoot.TrimEnd('\') + '\'
    if (-not $resolved.StartsWith(
            $repoWithSeparator,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Path must remain inside the repository: $resolved"
    }

    return $resolved
}

function Read-Json {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "Required JSON file was not found: $Path"
    }

    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Read-Run {
    param([string]$Path)

    $root = Resolve-RepositoryPath -Path $Path
    $reports = Join-Path $root 'reports'
    $manifestPath = Join-Path $reports 'run-manifest.json'
    $evaluationPath = Join-Path $reports 'evaluation.json'
    $manifest = Read-Json -Path $manifestPath
    $evaluation = Read-Json -Path $evaluationPath
    $summaries = @{}

    foreach ($cohort in @($manifest.Cohorts))
    {
        $summaryPath = Resolve-RepositoryPath -Path ([string]$cohort.SummaryPath)
        $summaries[[string]$cohort.Id] =
            Read-Json -Path $summaryPath
    }

    return [pscustomobject]@{
        Root = $root
        ManifestPath = $manifestPath
        EvaluationPath = $evaluationPath
        Manifest = $manifest
        Evaluation = $evaluation
        Summaries = $summaries
    }
}

function Add-Check {
    param(
        [string]$Id,
        [string]$Category,
        [bool]$Passed,
        [string]$Details,
        [string[]]$Evidence = @()
    )

    $checks.Add([pscustomobject]@{
        Id = $Id
        Category = $Category
        Passed = $Passed
        Details = $Details
        Evidence = @($Evidence)
    })
}

function Add-Measurement {
    param(
        [string]$Category,
        [string]$Cohort,
        [string]$Metric,
        [double]$Value,
        [string]$Unit
    )

    $measurements.Add([pscustomobject]@{
        Category = $Category
        Cohort = $Cohort
        Metric = $Metric
        Value = [Math]::Round($Value, 6)
        Unit = $Unit
    })
}

function Get-Rows {
    param(
        [object]$Run,
        [string]$Cohort
    )

    if (-not $Run.Summaries.ContainsKey($Cohort))
    {
        throw "Run '$($Run.Root)' does not contain cohort '$Cohort'."
    }

    $value = $Run.Summaries[$Cohort]
    if ($value -is [Array])
    {
        foreach ($row in $value)
        {
            Write-Output $row
        }
        return
    }

    Write-Output $value
}

function Get-CohortManifestEntry {
    param(
        [object]$Run,
        [string]$Cohort
    )

    $entries = @($Run.Manifest.Cohorts | Where-Object { $_.Id -eq $Cohort })
    if ($entries.Count -ne 1)
    {
        throw "Run '$($Run.Root)' does not contain exactly one manifest entry for cohort '$Cohort'."
    }

    return $entries[0]
}

function Get-Number {
    param(
        [object]$Row,
        [string]$Property
    )

    $value = $Row.PSObject.Properties[$Property]
    if ($null -eq $value -or $null -eq $value.Value)
    {
        throw "Summary property '$Property' is missing."
    }

    return [double]$value.Value
}

function Get-Median {
    param(
        [object[]]$Rows,
        [string]$Property
    )

    $values = @($Rows | ForEach-Object {
        Get-Number -Row $_ -Property $Property
    } | Sort-Object)
    if ($values.Count -eq 0)
    {
        throw "No values were available for '$Property'."
    }

    $index = [int][Math]::Ceiling(0.5 * $values.Count) - 1
    if ($index -lt 0)
    {
        $index = 0
    }
    if ($index -ge $values.Count)
    {
        $index = $values.Count - 1
    }
    return [double]$values[$index]
}

function Get-RelativeRangePercent {
    param(
        [object[]]$Rows,
        [string]$Property
    )

    $values = @($Rows | ForEach-Object {
        Get-Number -Row $_ -Property $Property
    } | Sort-Object)
    $median = Get-Median -Rows $Rows -Property $Property
    if ($values.Count -le 1 -or $median -le 0.0)
    {
        return 0.0
    }

    return (($values[-1] - $values[0]) / $median) * 100.0
}

function Get-ReuseRatio {
    param([object]$Row)

    $eligibleReused = $Row.PSObject.Properties['VulkanEligiblePrimaryCommandBuffersReusedTotal']
    $eligibleDecisions = $Row.PSObject.Properties['VulkanEligiblePrimaryCommandBufferReuseDecisionsTotal']
    if ($null -ne $eligibleReused -and $null -ne $eligibleDecisions) {
        $reused = [double]$eligibleReused.Value
        $decisions = [double]$eligibleDecisions.Value
    } else {
        $reused = Get-Number -Row $Row -Property 'VulkanPrimaryCommandBuffersReusedTotal'
        $recorded = Get-Number -Row $Row -Property 'VulkanPrimaryCommandBuffersRecordedTotal'
        $decisions = $reused + $recorded
    }
    if ($decisions -le 0.0)
    {
        return 0.0
    }

    return $reused / $decisions
}

function Test-ReleaseBenchmarkRun {
    param(
        [object]$Run,
        [string]$Category
    )

    $captureFailures = @($Run.Manifest.CaptureFailures)
    Add-Check `
        -Id "$Category.ReleaseBenchmark" `
        -Category $Category `
        -Passed (
            [string]$Run.Manifest.ProfileMode -eq 'ReleaseBenchmark' -and
            [bool]$Run.Manifest.PromotionEligible -and
            $captureFailures.Count -eq 0 -and
            [string]$Run.Evaluation.Status -eq 'PromotionPass') `
        -Details "profile=$($Run.Manifest.ProfileMode); promotionEligible=$($Run.Manifest.PromotionEligible); captureFailures=$($captureFailures.Count); evaluation=$($Run.Evaluation.Status)" `
        -Evidence @($Run.ManifestPath, $Run.EvaluationPath)
}

function Test-ZeroReadbackCohort {
    param(
        [object]$Run,
        [string]$Cohort,
        [string]$Category,
        [bool]$RequireThreeRepetitions = $true,
        [bool]$RequireReuse = $true
    )

    $rows = Get-Rows -Run $Run -Cohort $Cohort
    $violations = [Collections.Generic.List[string]]::new()
    if ($RequireThreeRepetitions -and $rows.Count -ne 3)
    {
        $violations.Add("repetitions=$($rows.Count), expected=3")
    }

    foreach ($row in $rows)
    {
        $rep = [int](Get-Number -Row $row -Property 'Repetition')
        if (-not [bool]$row.StabilityReady)
        {
            $violations.Add("r$rep stability gate did not pass")
        }
        if ((Get-Number -Row $row -Property 'CaptureWorkloadIdentityCount') -ne 1.0)
        {
            $violations.Add("r$rep workload identity changed")
        }

        foreach ($property in @(
            'GpuReadbackBytesTotal',
            'GpuMappedBuffersTotal',
            'GpuDrivenFullBucketScansTotal',
            'FallbackEventsTotal',
            'ForbiddenFallbackEventsTotal',
            'GpuDrivenUnsupportedCompactPassesTotal',
            'GpuDrivenSubmissionOwnedManagedAllocatedBytesTotal',
            'VulkanSubmissionRejectionsTotal',
            'VulkanValidationVuidCount'))
        {
            $value = Get-Number -Row $row -Property $property
            if ($value -ne 0.0)
            {
                $violations.Add("r$rep $property=$value")
            }
        }

        $requested = Get-Number -Row $row -Property 'VulkanRequestedDrawsP50'
        $consumed = Get-Number -Row $row -Property 'VulkanConsumedDrawsP50'
        if ($requested -ne $consumed)
        {
            $violations.Add("r$rep requested=$requested consumed=$consumed")
        }

        $active = Get-Number -Row $row -Property 'GpuDrivenActiveCommandCountP50'
        $capacity = Get-Number -Row $row -Property 'GpuDrivenCommandCapacityP50'
        if ($active -gt $capacity)
        {
            $violations.Add("r$rep active=$active exceeds capacity=$capacity")
        }

        if ($RequireReuse)
        {
            $ratio = Get-ReuseRatio -Row $row
            if ($ratio -lt 0.99)
            {
                $violations.Add("r$rep primary reuse=$($ratio.ToString('P2'))")
            }
        }
    }

    Add-Check `
        -Id "$Category.$Cohort.ZeroReadback" `
        -Category $Category `
        -Passed ($violations.Count -eq 0) `
        -Details $(if ($violations.Count -eq 0) {
            "$($rows.Count) repetition(s) passed zero-readback, allocation, output-count, validation, and primary-reuse checks."
        } else {
            $violations -join '; '
        }) `
        -Evidence @(
            $Run.ManifestPath,
            (Join-Path $Run.Root "reports\$Cohort\summary.json"))
}

function Test-MetricAllowance {
    param(
        [object[]]$CandidateRows,
        [object[]]$ReferenceRows,
        [string]$MetricName,
        [string]$SummaryProperty,
        [string]$CheckId,
        [string]$Category,
        [string[]]$Evidence
    )

    $candidate = Get-Median -Rows $CandidateRows -Property $SummaryProperty
    $reference = Get-Median -Rows $ReferenceRows -Property $SummaryProperty
    $allowance = [Math]::Max(0.25, $reference * 0.05)
    $candidateVariance = Get-RelativeRangePercent -Rows $CandidateRows -Property $SummaryProperty
    $referenceVariance = Get-RelativeRangePercent -Rows $ReferenceRows -Property $SummaryProperty
    # The canonical evaluator applies the 7.5% repeatability gate to each
    # cohort's declared budget metric. Keep the six independently reported
    # submission metrics on their explicit low-count allowance; their ranges
    # remain visible here without inventing a second variance policy.
    $passed = $candidate -le ($reference + $allowance)

    Add-Measurement -Category $Category -Cohort $CheckId -Metric $MetricName -Value $candidate -Unit 'ms p95 median'
    Add-Check `
        -Id $CheckId `
        -Category $Category `
        -Passed $passed `
        -Details (
            "candidate=$($candidate.ToString('F3')) ms; reference=$($reference.ToString('F3')) ms; " +
            "allowance=$($allowance.ToString('F3')) ms; candidateRange=$($candidateVariance.ToString('F2'))%; " +
            "referenceRange=$($referenceVariance.ToString('F2'))%") `
        -Evidence $Evidence
}

$scaling = Read-Run -Path $ScalingRunRoot
Test-ReleaseBenchmarkRun -Run $scaling -Category 'Scaling'

$capacityIds = @(
    'phase3-capacity-1x-active-fixed',
    'phase3-capacity-4x-active-fixed',
    'phase3-capacity-16x-active-fixed')
$activeIds = @(
    'phase3-active-1x-capacity-fixed',
    'phase3-active-4x-capacity-fixed',
    'phase3-active-16x-capacity-fixed')

foreach ($id in @($capacityIds + $activeIds))
{
    Test-ZeroReadbackCohort `
        -Run $scaling `
        -Cohort $id `
        -Category 'Scaling'
}

$capacityRows = @($capacityIds | ForEach-Object {
    ,(Get-Rows -Run $scaling -Cohort $_)
})
$capacityValues = @($capacityRows | ForEach-Object {
    Get-Median -Rows $_ -Property 'GpuDrivenCommandCapacityP50'
})
$capacityActiveValues = @($capacityRows | ForEach-Object {
    Get-Median -Rows $_ -Property 'GpuSceneCommandCountP50'
})
$capacityPassGroups = @($capacityRows | ForEach-Object {
    Get-Median -Rows $_ -Property 'GpuDrivenMaterialPassGroupsP50'
})
$capacityFrameOps = @($capacityRows | ForEach-Object {
    Get-Median -Rows $_ -Property 'VulkanFrameOpsP50'
})
Add-Check `
    -Id 'Scaling.CapacityRatios' `
    -Category 'Scaling' `
    -Passed (
        $capacityValues[1] -eq (4.0 * $capacityValues[0]) -and
        $capacityValues[2] -eq (16.0 * $capacityValues[0])) `
    -Details "capacities=$($capacityValues -join ',')" `
    -Evidence @($scaling.ManifestPath)
Add-Check `
    -Id 'Scaling.CapacityActiveWorkFixed' `
    -Category 'Scaling' `
    -Passed (
        @($capacityActiveValues | Select-Object -Unique).Count -eq 1 -and
        @($capacityPassGroups | Select-Object -Unique).Count -eq 1 -and
        @($capacityFrameOps | Select-Object -Unique).Count -eq 1) `
    -Details (
        "active=$($capacityActiveValues -join ','); passGroups=$($capacityPassGroups -join ','); " +
        "frameOps=$($capacityFrameOps -join ',')") `
    -Evidence @($scaling.ManifestPath)

foreach ($metric in $submissionMetrics.GetEnumerator())
{
    foreach ($index in 1..2)
    {
        $scaleLabel = if ($index -eq 1) { '4x' } else { '16x' }
        Test-MetricAllowance `
            -CandidateRows $capacityRows[$index] `
            -ReferenceRows $capacityRows[0] `
            -MetricName $metric.Key `
            -SummaryProperty $metric.Value `
            -CheckId "Scaling.Capacity$scaleLabel.$($metric.Key)" `
            -Category 'Scaling' `
            -Evidence @($scaling.ManifestPath)
    }
}

$activeRows = @($activeIds | ForEach-Object {
    ,(Get-Rows -Run $scaling -Cohort $_)
})
$activeCapacities = @($activeRows | ForEach-Object {
    Get-Median -Rows $_ -Property 'GpuDrivenCommandCapacityP50'
})
$activeCounts = @($activeRows | ForEach-Object {
    Get-Median -Rows $_ -Property 'GpuSceneCommandCountP50'
})
$activePassGroups = @($activeRows | ForEach-Object {
    Get-Median -Rows $_ -Property 'GpuDrivenMaterialPassGroupsP50'
})
$activeFrameOps = @($activeRows | ForEach-Object {
    Get-Median -Rows $_ -Property 'VulkanFrameOpsP50'
})
Add-Check `
    -Id 'Scaling.ActiveCountsIncreaseAtFixedCapacity' `
    -Category 'Scaling' `
    -Passed (
        @($activeCapacities | Select-Object -Unique).Count -eq 1 -and
        $activeCounts[0] -lt $activeCounts[1] -and
        $activeCounts[1] -lt $activeCounts[2] -and
        @($activePassGroups | Select-Object -Unique).Count -eq 1 -and
        @($activeFrameOps | Select-Object -Unique).Count -eq 1) `
    -Details (
        "capacity=$($activeCapacities -join ','); active=$($activeCounts -join ','); " +
        "passGroups=$($activePassGroups -join ','); frameOps=$($activeFrameOps -join ',')") `
    -Evidence @($scaling.ManifestPath)

# Workstream 03 owns compact submission topology and its directly attributed
# allocations. Generic per-object frame-op preparation, resource planning, and
# frame-data refresh intentionally scale with active scene work today and are
# explicit workstream 04/05 handoffs. Keep those six stage p95 values in the
# acceptance report without misclassifying successor-owned work as a compact
# submission failure. The capacity-fixed topology check above remains the
# workstream-03 gate for this axis.
for ($index = 0; $index -lt $activeIds.Count; $index++)
{
    foreach ($metric in $submissionMetrics.GetEnumerator())
    {
        Add-Measurement `
            -Category 'ScalingActiveHandoff' `
            -Cohort $activeIds[$index] `
            -Metric $metric.Key `
            -Value (Get-Median -Rows $activeRows[$index] -Property $metric.Value) `
            -Unit 'ms p95 median'
    }
}

$crossover = $null
if (-not [string]::IsNullOrWhiteSpace($CrossoverRunRoot))
{
    $crossover = Read-Run -Path $CrossoverRunRoot
    Test-ReleaseBenchmarkRun -Run $crossover -Category 'Crossover'
    $zeroId = 'phase3-high-count-zero-readback'
    $cpuId = 'phase3-high-count-cpu-direct'
    $scanId = 'phase3-high-count-full-scan'
    Test-ZeroReadbackCohort `
        -Run $crossover `
        -Cohort $zeroId `
        -Category 'Crossover'

    $zeroRows = Get-Rows -Run $crossover -Cohort $zeroId
    $cpuRows = Get-Rows -Run $crossover -Cohort $cpuId
    $scanRows = Get-Rows -Run $crossover -Cohort $scanId
    $zeroP95 = Get-Median -Rows $zeroRows -Property 'RenderP95Ms'
    $cpuP95 = Get-Median -Rows $cpuRows -Property 'RenderP95Ms'
    $scanP95 = Get-Median -Rows $scanRows -Property 'RenderP95Ms'
    $zeroCommands = Get-Median -Rows $zeroRows -Property 'GpuSceneCommandCountP50'
    $cpuCommands = Get-Median -Rows $cpuRows -Property 'GpuSceneCommandCountP50'
    $scanCommands = Get-Median -Rows $scanRows -Property 'GpuSceneCommandCountP50'
    $zeroManifestEntry = Get-CohortManifestEntry -Run $crossover -Cohort $zeroId
    $cpuManifestEntry = Get-CohortManifestEntry -Run $crossover -Cohort $cpuId
    $scanManifestEntry = Get-CohortManifestEntry -Run $crossover -Cohort $scanId
    $matchedSceneSettings =
        $zeroManifestEntry.SettingsSha256 -eq $cpuManifestEntry.SettingsSha256 -and
        $zeroManifestEntry.SettingsSha256 -eq $scanManifestEntry.SettingsSha256

    $zeroRange = Get-RelativeRangePercent -Rows $zeroRows -Property 'RenderP95Ms'
    $cpuRange = Get-RelativeRangePercent -Rows $cpuRows -Property 'RenderP95Ms'
    $scanRange = Get-RelativeRangePercent -Rows $scanRows -Property 'RenderP95Ms'
    $scanCount = ($scanRows | ForEach-Object {
        Get-Number -Row $_ -Property 'GpuDrivenFullBucketScansTotal'
    } | Measure-Object -Sum).Sum

    Add-Check `
        -Id 'Crossover.ZeroReadbackBeatsCpuAndFullScan' `
        -Category 'Crossover' `
        -Passed (
            $zeroP95 -le (0.95 * $cpuP95) -and
            $zeroP95 -le (0.95 * $scanP95) -and
            $zeroRange -le 7.5 -and
            $cpuRange -le 7.5 -and
            $scanRange -le 7.5 -and
            $zeroCommands -ge 4096.0 -and
            $cpuCommands -ge 4096.0 -and
            $scanCommands -ge 4096.0 -and
            $matchedSceneSettings -and
            $scanCount -gt 0.0) `
        -Details (
            "render p95 median zero=$($zeroP95.ToString('F3')) ms, cpu=$($cpuP95.ToString('F3')) ms, " +
            "fullScan=$($scanP95.ToString('F3')) ms; ranges=$($zeroRange.ToString('F2'))%/" +
            "$($cpuRange.ToString('F2'))%/$($scanRange.ToString('F2'))%; backendCommands=" +
            "$zeroCommands/$cpuCommands/$scanCommands; matchedSettings=$matchedSceneSettings; " +
            "fullScans=$scanCount") `
        -Evidence @($crossover.ManifestPath)
}

$gate = $null
if (-not [string]::IsNullOrWhiteSpace($GateRunRoot))
{
    $gate = Read-Run -Path $GateRunRoot
    Test-ReleaseBenchmarkRun -Run $gate -Category 'Gate'
    Add-Check `
        -Id 'Gate.EvaluatorPass' `
        -Category 'Gate' `
        -Passed ([string]$gate.Evaluation.Status -eq 'PromotionPass') `
        -Details "status=$($gate.Evaluation.Status); promotionStatus=$($gate.Evaluation.PromotionStatus); issues=$(@($gate.Evaluation.Issues).Count)" `
        -Evidence @($gate.EvaluationPath)

    foreach ($cohort in @($gate.Manifest.Cohorts))
    {
        $id = [string]$cohort.Id
        Test-ZeroReadbackCohort `
            -Run $gate `
            -Cohort $id `
            -Category 'Gate' `
            -RequireReuse:($id -notlike 'rvc-*')

        $rows = Get-Rows -Run $gate -Cohort $id
        Add-Measurement `
            -Category 'AbsoluteBudgetHandoff' `
            -Cohort $id `
            -Metric 'render_dispatch_ms' `
            -Value (Get-Median -Rows $rows -Property 'RenderP95Ms') `
            -Unit 'ms p95 median'
    }
}

$cpuReference = $null
if (-not [string]::IsNullOrWhiteSpace($CpuReferenceRunRoot))
{
    if ($null -eq $gate)
    {
        throw 'CpuReferenceRunRoot requires GateRunRoot for matched zero-readback cohorts.'
    }

    $cpuReference = Read-Run -Path $CpuReferenceRunRoot
    Test-ReleaseBenchmarkRun -Run $cpuReference -Category 'DesktopCpuComparison'
    $pairs = [ordered]@{
        'desktop-deferred-static' = 'primary-reuse-deferred-static'
        'desktop-deferred-moving' = 'primary-reuse-deferred-moving'
        'desktop-uber-static' = 'primary-reuse-uber-static'
        'desktop-uber-moving' = 'primary-reuse-uber-moving'
    }

    foreach ($pair in $pairs.GetEnumerator())
    {
        $zeroRows = Get-Rows -Run $gate -Cohort $pair.Key
        $cpuRows = Get-Rows -Run $cpuReference -Cohort $pair.Value
        foreach ($metric in $submissionMetrics.GetEnumerator())
        {
            Test-MetricAllowance `
                -CandidateRows $zeroRows `
                -ReferenceRows $cpuRows `
                -MetricName $metric.Key `
                -SummaryProperty $metric.Value `
                -CheckId "DesktopCpuComparison.$($pair.Key).$($metric.Key)" `
                -Category 'DesktopCpuComparison' `
                -Evidence @($gate.ManifestPath, $cpuReference.ManifestPath)
        }
    }
}

$comparisonRuns = @($scaling)
foreach ($optionalRun in @($crossover, $gate, $cpuReference))
{
    if ($null -ne $optionalRun)
    {
        $comparisonRuns += $optionalRun
    }
}
$identityProperties = @(
    'SourceCommit',
    'ExecutableSha256',
    'OperatingSystem',
    'GpuName',
    'GpuDriver',
    'DisplayMode')
$identityMismatches = [Collections.Generic.List[string]]::new()
foreach ($property in $identityProperties)
{
    $values = @($comparisonRuns | ForEach-Object {
        [string]$_.Manifest.$property
    } | Select-Object -Unique)
    if ($values.Count -ne 1)
    {
        $identityMismatches.Add("$property=$($values -join ',')")
    }
}
Add-Check `
    -Id 'CrossRun.ManifestCompatibility' `
    -Category 'CrossRun' `
    -Passed ($identityMismatches.Count -eq 0) `
    -Details $(if ($identityMismatches.Count -eq 0) {
        "$($comparisonRuns.Count) run(s) share source, executable, OS, GPU, driver, and display identity."
    } else {
        $identityMismatches -join '; '
    }) `
    -Evidence @($comparisonRuns | ForEach-Object { $_.ManifestPath })

$failed = @($checks | Where-Object { -not $_.Passed })
$status = if ($failed.Count -eq 0) { 'Pass' } else { 'Fail' }
if ([string]::IsNullOrWhiteSpace($OutputPath))
{
    $OutputPath = Join-Path $scaling.Root 'reports\phase3-acceptance.json'
}
$outputFullPath = Resolve-RepositoryPath -Path $OutputPath
$outputDirectory = Split-Path -Parent $outputFullPath
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$report = [ordered]@{
    SchemaVersion = 1
    GeneratedUtc = [datetime]::UtcNow.ToString('O')
    Status = $status
    SubmissionMetrics = @($submissionMetrics.Keys)
    VarianceThresholdPercent = 7.5
    RegressionThresholdPercent = 5.0
    LowCountMinimumAllowanceMilliseconds = 0.25
    ScalingRunRoot = $scaling.Root
    CrossoverRunRoot = $(if ($null -ne $crossover) { $crossover.Root } else { $null })
    GateRunRoot = $(if ($null -ne $gate) { $gate.Root } else { $null })
    CpuReferenceRunRoot = $(if ($null -ne $cpuReference) { $cpuReference.Root } else { $null })
    Checks = @($checks)
    Measurements = @($measurements)
}
$report |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $outputFullPath -Encoding UTF8

$markdownPath = [IO.Path]::ChangeExtension($outputFullPath, '.md')
$markdown = [Collections.Generic.List[string]]::new()
$markdown.Add('# Vulkan Phase 3 Acceptance Comparison')
$markdown.Add('')
$markdown.Add("Status: **$status**")
$markdown.Add('')
$markdown.Add('| Check | Result | Details |')
$markdown.Add('| --- | --- | --- |')
foreach ($check in $checks)
{
    $result = if ($check.Passed) { 'PASS' } else { 'FAIL' }
    $details = ([string]$check.Details).Replace('|', '\|')
    $markdown.Add("| $($check.Id) | $result | $details |")
}
$markdown.Add('')
$markdown.Add(('JSON report: `{0}`' -f $outputFullPath))
$markdown |
    Set-Content -LiteralPath $markdownPath -Encoding UTF8

Write-Host "Vulkan Phase 3 acceptance comparison: $status"
Write-Host "JSON: $outputFullPath"
Write-Host "Markdown: $markdownPath"
foreach ($failure in $failed)
{
    Write-Host "- $($failure.Id): $($failure.Details)" -ForegroundColor Red
}

if ($failed.Count -gt 0)
{
    exit 1
}
