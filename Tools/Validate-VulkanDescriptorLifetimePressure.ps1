param(
    [ValidateSet('OpenXR', 'MonadoOpenXR')]
    [string]$UnitTestVrMode = 'OpenXR',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [int]$WarmupSec = 30,
    [int]$CaptureSec = 30,
    [int]$MaxLiveResources = 50000,
    [int]$MaxDescriptorSets = 25000,
    [switch]$VulkanValidation
)

$ErrorActionPreference = 'Stop'
$harness = Join-Path $PSScriptRoot 'Measure-GameLoopRenderPipeline.ps1'

if (-not (Test-Path -LiteralPath $harness)) {
    throw "Benchmark harness not found: $harness"
}

$arguments = @{
    WarmupSec = $WarmupSec
    CaptureSec = $CaptureSec
    Repetitions = 1
    Strategies = @('GpuIndirectZeroReadback')
    Configuration = $Configuration
    CacheMode = 'Warm'
    UnitTestVrMode = $UnitTestVrMode
    RunLabel = 'vulkan-descriptor-lifetime-pressure'
    FailOnSteadyStateResourceChurn = $true
    MaxSteadyStateVulkanLiveResources = $MaxLiveResources
    MaxSteadyStateVulkanDescriptorSets = $MaxDescriptorSets
    NoP3Logging = $true
}

if ($VulkanValidation) {
    $arguments.VulkanValidation = $true
}

Write-Host "[descriptor-pressure] mode=$UnitTestVrMode maxLive=$MaxLiveResources maxSets=$MaxDescriptorSets" -ForegroundColor Cyan
& $harness @arguments

