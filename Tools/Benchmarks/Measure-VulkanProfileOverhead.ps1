param(
    [string]$Cohort = 'desktop-deferred-static',
    [int]$WarmupSec = 10,
    [int]$CaptureSec = 15,
    [string]$EditorExecutablePath = 'Build\Editor\Release\AnyCPU\Release\net10.0-windows7.0\XREngine.Editor.exe',
    [string]$ContractPath = 'XREngine.Benchmarks\VulkanPerformance\vulkan-performance-cohorts.json',
    [string]$RunRoot = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$contractFullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $ContractPath))
$contract = Get-Content -Raw -LiteralPath $contractFullPath | ConvertFrom-Json
$cohortDefinition = @($contract.cohorts | Where-Object { $_.id -eq $Cohort })
if ($cohortDefinition.Count -ne 1) {
    throw "Expected exactly one cohort named '$Cohort'."
}
$cohortDefinition = $cohortDefinition[0]

if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $RunRoot = "Build\_AgentValidation\$stamp-vulkan-profile-overhead"
}
$runFullPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $RunRoot))
$validationRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'Build\_AgentValidation')).TrimEnd('\') + '\'
if (-not $runFullPath.StartsWith($validationRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "RunRoot must be under Build\_AgentValidation: $runFullPath"
}
New-Item -ItemType Directory -Path (Join-Path $runFullPath 'reports') -Force | Out-Null

$measureScript = Join-Path $repoRoot 'Tools\Measure-GameLoopRenderPipeline.ps1'
$settingsPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ([string]$cohortDefinition.settingsPath)))
$editorPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $EditorExecutablePath))
$results = [System.Collections.Generic.List[object]]::new()

foreach ($mode in @('ReleaseBenchmark', 'CleanProfile', 'DevelopmentProfile', 'Diagnostics')) {
    $modeOutput = Join-Path $runFullPath "reports\$mode"
    $intrusive = $mode -in @('DevelopmentProfile', 'Diagnostics')
    $profileDefinition = $contract.profileModes.$mode
    $arguments = @{
        WarmupSec = $WarmupSec
        CaptureSec = $CaptureSec
        Repetitions = 1
        Strategies = @([string]$cohortDefinition.strategy)
        Configuration = 'Release'
        RenderBackend = 'Vulkan'
        UnitTestingWorldSettingsPath = $settingsPath
        CacheMode = 'Warm'
        ZeroReadbackMaterialDrawPath = [string]$cohortDefinition.zeroReadbackMaterialDrawPath
        ProfileScene = [string]$cohortDefinition.scene
        ProfileCamera = [string]$cohortDefinition.camera
        ProfileLights = [string]$cohortDefinition.lights
        ProfileViewport = [string]$cohortDefinition.viewport
        RenderScale = [string]$cohortDefinition.renderScale
        TargetRefreshHz = 200.0
        NoClearCachesBetweenVariants = $true
        RetainedRunCount = 20
        RunLabel = "vulkan-profile-overhead-$($mode.ToLowerInvariant())-$Cohort"
        OutputDirectory = $modeOutput
        EditorExecutablePath = $editorPath
        ProfileMode = $mode
        DisableMcpDiagnostics = $true
        UnitTestVrMode = [string]$cohortDefinition.vrMode
        VulkanRenderTargetMode = 'DynamicRendering'
        VulkanPrimaryReuse = 'Enabled'
        VulkanCommandChains = 'Enabled'
        VulkanParallelCommandChainRecording = 'Enabled'
        VulkanParallelSecondaryRecording = 'Enabled'
        OcclusionCullingMode = 'Disabled'
        VulkanDiagnosticPreset = $(if ($intrusive) { 'StandardValidation' } else { 'Off' })
    }
    if (-not $intrusive) {
        $arguments.NoP3Logging = $true
    } else {
        $arguments.GpuTimestampDense = $true
        $arguments.VulkanValidation = $true
        $arguments.VulkanCommandBufferLabels = $true
    }

    try {
        & $measureScript @arguments
        $summaryPath = Join-Path $modeOutput 'summary.json'
        $summary = @(Get-Content -Raw -LiteralPath $summaryPath | ConvertFrom-Json)[0]
        $results.Add([pscustomobject]@{
            ProfileMode = $mode
            ContractCleanComparisonSuitable = [bool]$profileDefinition.cleanComparisonSuitable
            ExpectedObserverOverhead = [string]$profileDefinition.expectedOverhead
            Status = 'Measured'
            Failure = $null
            RenderP50Ms = $summary.RenderP50Ms
            RenderP90Ms = $summary.RenderP90Ms
            RenderP95Ms = $summary.RenderP95Ms
            RenderP99Ms = $summary.RenderP99Ms
            RenderWorstMs = $summary.RenderWorstMs
            Samples = $summary.Samples
            SummaryPath = $summaryPath
        })
    } catch {
        $results.Add([pscustomobject]@{
            ProfileMode = $mode
            ContractCleanComparisonSuitable = [bool]$profileDefinition.cleanComparisonSuitable
            ExpectedObserverOverhead = [string]$profileDefinition.expectedOverhead
            Status = 'UnsupportedOrFailed'
            Failure = $_.Exception.Message
            RenderP50Ms = $null
            RenderP90Ms = $null
            RenderP95Ms = $null
            RenderP99Ms = $null
            RenderWorstMs = $null
            Samples = 0
            SummaryPath = $null
        })
    }
}

$releaseResult = $results | Where-Object {
    $_.ProfileMode -eq 'ReleaseBenchmark' -and $_.Status -eq 'Measured'
}
$releaseP95 = if ($null -ne $releaseResult) {
    [double]$releaseResult.RenderP95Ms
} else {
    $null
}
$report = @($results | ForEach-Object {
    $p95 = if ($_.Status -eq 'Measured') {
        [double]$_.RenderP95Ms
    } else {
        $null
    }
    [pscustomobject]@{
        ProfileMode = $_.ProfileMode
        ContractCleanComparisonSuitable = $_.ContractCleanComparisonSuitable
        ExpectedObserverOverhead = $_.ExpectedObserverOverhead
        Status = $_.Status
        Failure = $_.Failure
        RenderP50Ms = $_.RenderP50Ms
        RenderP90Ms = $_.RenderP90Ms
        RenderP95Ms = $_.RenderP95Ms
        RenderP99Ms = $_.RenderP99Ms
        RenderWorstMs = $_.RenderWorstMs
        Samples = $_.Samples
        P95OverheadMs = $(if ($null -ne $p95 -and $null -ne $releaseP95) { $p95 - $releaseP95 } else { $null })
        P95OverheadPercent = $(if ($null -ne $p95 -and $releaseP95 -gt 0.0) { (($p95 / $releaseP95) - 1.0) * 100.0 } else { $null })
        SummaryPath = $_.SummaryPath
    }
})
$reportPath = Join-Path $runFullPath 'reports\profile-overhead.json'
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding UTF8
$report | Format-Table -AutoSize
Write-Host "Profile overhead report: $reportPath"
