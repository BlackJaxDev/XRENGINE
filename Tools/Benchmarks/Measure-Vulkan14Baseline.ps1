<#
.SYNOPSIS
Captures Vulkan 1.4 workload baselines using the standard frame profiler.
.DESCRIPTION
Requires an already-built Release editor. Stores isolated Vulkan caches, exact
arguments, summaries and raw logs under one caller-owned agent-validation run.
ColdStart measures an empty engine cache, not a flushed OS or driver cache.
AdvancedRenderPipeline requires native Advanced execution. Use descriptor indexing;
the Advanced descriptor-heap implementation currently reports unsupported. Run
Summarize-Vulkan14Baseline.py afterward to apply retained actual-path validation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EditorExecutablePath,
    [Parameter(Mandatory)][string]$RunRoot,
    [ValidateSet('DescriptorHeap', 'DescriptorIndexing')]
    [string[]]$Bindings = @('DescriptorHeap', 'DescriptorIndexing'),
    [ValidateSet('Static', 'Moving', 'MaterialEdits', 'Streaming', 'VolatileUi', 'Resize', 'ColdStart')]
    [string[]]$Workloads = @('Static', 'Moving', 'MaterialEdits', 'Streaming', 'VolatileUi', 'Resize', 'ColdStart'),
    [ValidateSet('DefaultRenderPipeline', 'AdvancedRenderPipeline')]
    [string]$RenderPipeline = 'DefaultRenderPipeline',
    [ValidateSet('Allowed', 'ForceRecording')]
    [string[]]$ReusePolicies = @('Allowed'),
    [ValidateRange(1, 20)][int]$Repetitions = 3,
    [ValidateRange(1, 20)][int]$FirstRepetition = 1,
    [ValidateRange(1, 600)][int]$WarmupSec = 25,
    [ValidateRange(1, 3600)][int]$CaptureSec = 60,
    [switch]$SkipCacheSeed,
    [switch]$ContinueOnInvalidCohort
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$RunRoot = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($RunRoot)) { $RunRoot } else { Join-Path $repoRoot $RunRoot }))
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'Build/_AgentValidation')) + [IO.Path]::DirectorySeparatorChar
if (-not $RunRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'RunRoot must be inside Build/_AgentValidation; reserve one task root with Limit-AgentValidation first.'
}
$EditorExecutablePath = (Resolve-Path -LiteralPath $EditorExecutablePath).Path
$settings = Join-Path $repoRoot 'XREngine.Benchmarks/VulkanPerformance/Cohorts/vulkan14-phase-c.jsonc'
$asset = Join-Path $repoRoot 'Assets/Rive/SplashScreen.scale-200.png'
$measure = Join-Path $repoRoot 'Tools/Measure-GameLoopRenderPipeline.ps1'
New-Item -ItemType Directory -Force -Path $RunRoot | Out-Null

# Scope diagnostic changes to child processes and restore the caller's environment.
$environment = @{
    XRE_VULKAN_SYNC_VALIDATION = '0'; XRE_VULKAN_GPU_ASSISTED_VALIDATION = '0'
    XRE_VULKAN_BEST_PRACTICES = '0'; XRE_VULKAN_DIAGNOSTIC_FLAGS = 'None'
    XRE_VULKAN_DEVICE_FAULT = '0'; XRE_VULKAN_CRASH_BREADCRUMBS = '0'
    XRE_VULKAN_NV_DIAGNOSTIC_CHECKPOINTS = '0'; XRE_VULKAN_NV_DIAGNOSTICS_CONFIG = '0'
    XRE_VULKAN_DEVICE_ADDRESS_BINDING_REPORT = '0'; XRE_VULKAN_VALIDATE_SPIRV = '0'
    XRE_VULKAN_SHADER_DIAGNOSTICS_DIR = $null; XRE_VULKAN_FRAMEOP_TRACE = '0'
    XRE_VULKAN_TARGET_TRACE = '0'; XRE_VULKAN_INDIRECT_TRACE = '0'
    XRE_VULKAN_COUNTER_DIAGNOSTICS = '0'; XRE_VULKAN_DESCRIPTOR_TRACE = '0'
    XRE_VULKAN_COMMAND_CHAIN_TRACE = '0'; XRE_VULKAN_COMMAND_CHAIN_VALIDATE = '0'
    XRE_VULKAN_PARALLEL_RECORDING_VALIDATE = '0'; XRE_OPENXR_VULKAN_TRACE = '0'
    XRE_VULKAN_RECORDING_DIAG = '0'; XRE_VULKAN_RECORDING_PROFILE_DETAIL = '0'
    XRE_VULKAN_FINAL_PRESENT_LEDGER = '0'; XRE_VULKAN_ALLOW_CPU_MESH_SAFETY_NET = '0'
    XRE_PROFILE_STREAMING_ASSET = $asset; XRE_PROFILE_MUTATION_WORKLOAD = $null
    XRE_VULKAN_PIPELINE_CACHE_ROOT = $null; XRE_VK_DESCRIPTOR_BACKEND = $null
    XRE_UNIT_TEST_RENDER_PIPELINE = $RenderPipeline
    XRE_ADVANCED_RENDER_PIPELINE_MODE = $(if ($RenderPipeline -eq 'AdvancedRenderPipeline') { 'Required' } else { $null })
}
$previous = @{}
$invalidCohorts = [Collections.Generic.List[string]]::new()
foreach ($key in $environment.Keys) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
}

function Get-ReusePolicyOptions([string]$ReusePolicy) {
    switch ($ReusePolicy) {
        'Allowed' {
            return [ordered]@{
                VulkanPrimaryReuse = 'Enabled'
                VulkanCommandChains = 'Enabled'
                VulkanCommandChainBenchmarkForceRerecord = $false
            }
        }
        'ForceRecording' {
            # Keep command chains enabled so this isolates reuse from their
            # scheduling. The engine's benchmark flag forces their rerecord.
            return [ordered]@{
                VulkanPrimaryReuse = 'Disabled'
                VulkanCommandChains = 'Enabled'
                VulkanCommandChainBenchmarkForceRerecord = $true
            }
        }
        default { throw "Unsupported reuse policy: $ReusePolicy" }
    }
}

function Invoke-Cohort([string]$Binding, [string]$Workload, [string]$ReusePolicy, [string]$Label, [string]$CacheRoot, [bool]$Seed) {
    $output = Join-Path $RunRoot "reports/$Label"
    if (Test-Path -LiteralPath $output) { throw "Refusing to overwrite cohort evidence: $output" }
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    $environment.XRE_VK_DESCRIPTOR_BACKEND = $Binding
    $environment.XRE_VULKAN_PIPELINE_CACHE_ROOT = $CacheRoot
    $environment.XRE_PROFILE_MUTATION_WORKLOAD = if ($Workload -in @('MaterialEdits', 'Streaming', 'VolatileUi', 'Resize')) { $Workload } else { $null }
    foreach ($key in $environment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $environment[$key], 'Process')
    }
    $reuseOptions = Get-ReusePolicyOptions $ReusePolicy
    $arguments = @{
        EditorExecutablePath = $EditorExecutablePath; OutputDirectory = $output
        Configuration = 'Release'; RenderBackend = 'Vulkan'; UnitTestVrMode = 'Desktop'
        UnitTestingWorldSettingsPath = $settings; Strategies = @('GpuIndirectZeroReadback')
        ZeroReadbackMaterialDrawPath = 'BindlessMaterialTable'; ProfileMode = 'ReleaseBenchmark'
        ProfileScene = $(if ($RenderPipeline -eq 'AdvancedRenderPipeline') { "Vulkan14-Advanced-$Workload" } else { "Vulkan14-$Workload" })
        ProfileCamera = $(if ($Workload -eq 'Moving') { 'Moving' } else { 'Static' })
        ProfileLights = 'CanonicalDirectional'; ProfileViewport = '1920x1080'; RenderScale = '1.0'
        WindowWidth = 1920; WindowHeight = 1080; SampleIntervalFrames = 10
        VulkanPresentationProfile = 'Uncapped'; GpuClockPolicy = 'UnmanagedBoost'
        VulkanRenderTargetMode = 'DynamicRendering'; VulkanDiagnosticPreset = 'Off'
        VulkanPrimaryReuse = $reuseOptions.VulkanPrimaryReuse; VulkanCommandChains = $reuseOptions.VulkanCommandChains
        VulkanCommandChainBenchmarkForceRerecord = $reuseOptions.VulkanCommandChainBenchmarkForceRerecord
        VulkanParallelCommandChainRecording = 'Enabled'; VulkanParallelSecondaryRecording = 'Enabled'
        OcclusionCullingMode = 'GpuHiZ'; DisableMcpDiagnostics = $true
        NoClearCachesBetweenVariants = $true; NoP3Logging = $true
        CacheMode = $(if ($Workload -eq 'ColdStart' -or $Seed) { 'Cold' } else { 'Warm' })
        WarmupSec = $WarmupSec; CaptureSec = $(if ($Seed) { 5 } else { $CaptureSec })
        Repetitions = 1; RunLabel = $Label; RetainedRunCount = 50
        StabilityWindowSec = 5; StabilityTimeoutSec = 120; StabilityProfile = 'OutputScheduling'
        # The generic gate requires GPURenderPassCollection material-table rows;
        # Advanced owns a different native table. Its retained-sample validator
        # must verify native pipeline/indirect evidence instead.
        NoStabilityGate = $RenderPipeline -eq 'AdvancedRenderPipeline' -or $Workload -in @('MaterialEdits', 'Streaming', 'VolatileUi', 'Resize', 'ColdStart')
        MinSteadyStateGpuSceneCommandCount = $(if ($RenderPipeline -eq 'AdvancedRenderPipeline') { 0 } else { 1 })
        AllowWorkloadIdentityChanges = $Workload -eq 'Resize'
    }
    $metadata = [ordered]@{
        startedUtc = [DateTime]::UtcNow.ToString('O'); binding = $Binding; workload = $Workload; reusePolicy = $ReusePolicy
        # Persist the exact pinned switches the summarizer must reject if a
        # caller changes. These are distinct from broad output-path policy.
        expectedReuseOptions = $reuseOptions
        seed = $Seed; arguments = $arguments; environment = $environment.Clone()
        gitHead = (& git -C $repoRoot rev-parse HEAD); settingsSha256 = (Get-FileHash $settings).Hash
        harnessSha256 = (Get-FileHash $measure).Hash; runnerSha256 = (Get-FileHash $PSCommandPath).Hash
        executableSha256 = (Get-FileHash $EditorExecutablePath).Hash; streamingAssetSha256 = (Get-FileHash $asset).Hash
        assemblyHashes = @(Get-ChildItem -LiteralPath (Split-Path $EditorExecutablePath) -Filter 'XREngine*.dll' -File |
            Get-FileHash | Select-Object Path, Hash)
    }
    $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'invocation.json')
    if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
        & nvidia-smi --query-gpu=name,driver_version,pstate,clocks.current.graphics,clocks.current.memory,temperature.gpu,power.draw --format=csv |
            Set-Content -LiteralPath (Join-Path $output 'gpu-before.csv')
    }
    Write-Host "[phase-e] $Label starting at $($metadata.startedUtc)"
    $invalidReasons = [Collections.Generic.List[string]]::new()
    try {
        & $measure @arguments *> (Join-Path $output 'harness.log')
    }
    catch {
        $invalidReasons.Add($_.Exception.Message)
    }
    $summaryPath = Join-Path $output 'summary.json'
    $runs = @()
    if (Test-Path -LiteralPath $summaryPath) {
        $runs = @(Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json)
        if ($runs.Count -ne 1) {
            $invalidReasons.Add("Expected one harness summary, found $($runs.Count).")
        }
    }
    else {
        $invalidReasons.Add('The harness produced no summary.')
    }
    foreach ($run in $runs) {
        $raw = Join-Path $output 'raw'
        New-Item -ItemType Directory -Force -Path $raw | Out-Null
        if ([string]::IsNullOrWhiteSpace($run.LogDir) -or -not (Test-Path -LiteralPath $run.LogDir -PathType Container)) {
            $invalidReasons.Add('The editor log directory is unavailable.')
        }
        else {
            Get-ChildItem -LiteralPath $run.LogDir -File | Copy-Item -Destination $raw
        }
        if ($run.Samples -lt 10 -or -not $run.StabilityReady -or $run.Note -match 'exited early|no render-stats progress|forced stop|violation|requested backend|requested strategy|rejected submissions|unapproved output policy') {
            $invalidReasons.Add("Incomplete or invalid capture: samples=$($run.Samples), stable=$($run.StabilityReady), note=$($run.Note)")
        }
    }
    [ordered]@{ valid = $invalidReasons.Count -eq 0; reasons = @($invalidReasons) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'validation.json')
    if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) {
        & nvidia-smi --query-gpu=pstate,clocks.current.graphics,clocks.current.memory,temperature.gpu,power.draw --format=csv |
            Set-Content -LiteralPath (Join-Path $output 'gpu-after.csv')
    }
    if ($invalidReasons.Count -gt 0) {
        $invalidCohorts.Add($Label)
        $message = "Invalid cohort $Label; inspect validation.json, summary.json and raw logs."
        if (-not $ContinueOnInvalidCohort) { throw $message }
        Write-Warning $message
        return
    }
    Write-Host "[phase-e] $Label complete: samples=$($runs[0].Samples), render CPU p50=$($runs[0].RenderP50Ms) ms, completed GPU command buffer p50=$($runs[0].VulkanGpuCommandBufferP50Ms) ms"
}

try {
    if (-not $SkipCacheSeed) {
        foreach ($binding in $Bindings) {
            Invoke-Cohort $binding 'Static' 'Allowed' "$binding-cache-seed" (Join-Path $RunRoot "scratch/cache-$binding-warm") $true
        }
    }
    for ($rep = $FirstRepetition; $rep -lt $FirstRepetition + $Repetitions; $rep++) {
        $order = @($Bindings)
        if ($rep % 2 -eq 0) { [array]::Reverse($order) }
        $reuseOrder = @($ReusePolicies)
        if ($rep % 2 -eq 0) { [array]::Reverse($reuseOrder) }
        foreach ($workload in $Workloads) {
            foreach ($binding in $order) {
                foreach ($reusePolicy in $reuseOrder) {
                    $cache = if ($workload -eq 'ColdStart') { "cache-$binding-$reusePolicy-cold-r$rep" } else { "cache-$binding-warm" }
                    # Retain legacy names for the existing default-only matrix.
                    $policyLabel = if ($ReusePolicies.Count -eq 1 -and $reusePolicy -eq 'Allowed') { '' } else { "-$reusePolicy" }
                    Invoke-Cohort $binding $workload $reusePolicy "$binding$policyLabel-$workload-r$rep" (Join-Path $RunRoot "scratch/$cache") $false
                }
            }
        }
    }
}
finally {
    foreach ($key in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process')
    }
}

if ($invalidCohorts.Count -gt 0) {
    throw "The matrix finished with invalid cohorts: $($invalidCohorts -join ', '). Retained evidence remains available."
}
