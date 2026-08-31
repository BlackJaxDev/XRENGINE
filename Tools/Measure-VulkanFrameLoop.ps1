# Measures the Vulkan frame loop with a focused default-pipeline run.
param(
    [int]$WarmupSec = 25,
    [int]$CaptureSec = 60,
    [int]$Repetitions = 1,
    [string[]]$Strategies = @('GpuIndirectZeroReadback'),
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('Cold', 'Warm')]
    [string]$CacheMode = 'Warm',
    [ValidateSet('FullBucketScanDiagnostic', 'ActiveBucketListReadbackDiagnostic', 'MaterialTable', 'BindlessMaterialTable', 'FullBucketScan', 'ActiveBucketList')]
    [string]$ZeroReadbackMaterialDrawPath = 'BindlessMaterialTable',
    [string]$ProfileScene = '',
    [string]$ProfileCamera = '',
    [string]$ProfileLights = '',
    [string]$ProfileViewport = '',
    [string]$RenderScale = '',
    [string]$GpuClockPolicy = 'Unspecified',
    [double]$TargetRefreshHz = 0,
    [switch]$GpuTimestampDense,
    [switch]$NoClearCachesBetweenVariants,
    [switch]$NoP3Logging,
    [switch]$FailOnSteadyStateResourceChurn,
    [switch]$FailOnSteadyStateCommandBufferChurn,
    [switch]$FailOnSteadyStateCommandBufferAllocations,
    [double]$MinSteadyStateCommandBufferCleanReuseRatio = 0,
    [long]$MaxSteadyStateRecordCommandBufferAllocatedBytes = 0,
    [int]$StabilityWindowSec = 5,
    [int]$StabilityTimeoutSec = 120,
    [ValidateSet('FullResourceQuiet', 'OutputScheduling')]
    [string]$StabilityProfile = 'FullResourceQuiet',
    [switch]$NoStabilityGate,
    [int]$ShutdownGraceSec = 20,
    [int]$NoSampleHangSec = 15,
    [int]$RetainedRunCount = 5,
    [string]$RunLabel = 'vulkan-frame-loop',
    [string]$OutputDirectory = '',
    [string]$EditorExecutablePath = '',
    [switch]$DisableMcpDiagnostics,
    [ValidateSet('Diagnostics', 'DevelopmentProfile', 'CleanProfile', 'ReleaseBenchmark')]
    [string]$ProfileMode = 'DevelopmentProfile',
    [ValidateSet('Configured', 'Desktop', 'Emulated', 'MonadoOpenXR', 'OpenVR', 'OpenXR')]
    [string]$UnitTestVrMode = 'Desktop',
    [ValidateSet('Configured', 'Disabled', 'CpuQueryAsync', 'CpuSoftwareOcclusion', 'GpuHiZ')]
    [string]$OcclusionCullingMode = 'Configured',
    [ValidateSet('Configured', 'Off', 'StandardValidation', 'SyncValidation', 'GpuAssisted', 'BestPractices', 'CrashDiagnostics', 'RenderDocFriendly')]
    [string]$VulkanDiagnosticPreset = 'Configured',
    [switch]$VulkanCommandBufferLabels
)

$ErrorActionPreference = 'Stop'

$measureScript = Join-Path $PSScriptRoot 'Measure-GameLoopRenderPipeline.ps1'
if (-not (Test-Path -LiteralPath $measureScript)) {
    throw "Measurement script not found: $measureScript"
}

$arguments = @{
    WarmupSec = $WarmupSec
    CaptureSec = $CaptureSec
    Repetitions = $Repetitions
    Strategies = $Strategies
    Configuration = $Configuration
    CacheMode = $CacheMode
    ZeroReadbackMaterialDrawPath = $ZeroReadbackMaterialDrawPath
    ProfileScene = $ProfileScene
    ProfileCamera = $ProfileCamera
    ProfileLights = $ProfileLights
    ProfileViewport = $ProfileViewport
    RenderScale = $RenderScale
    GpuClockPolicy = $GpuClockPolicy
    TargetRefreshHz = $TargetRefreshHz
    MaxSteadyStateRecordCommandBufferAllocatedBytes = $MaxSteadyStateRecordCommandBufferAllocatedBytes
    MinSteadyStateCommandBufferCleanReuseRatio = $MinSteadyStateCommandBufferCleanReuseRatio
    StabilityWindowSec = $StabilityWindowSec
    StabilityTimeoutSec = $StabilityTimeoutSec
    StabilityProfile = $StabilityProfile
    ShutdownGraceSec = $ShutdownGraceSec
    NoSampleHangSec = $NoSampleHangSec
    RetainedRunCount = $RetainedRunCount
    RunLabel = $RunLabel
    OutputDirectory = $OutputDirectory
    EditorExecutablePath = $EditorExecutablePath
    ProfileMode = $ProfileMode
    OcclusionCullingMode = $OcclusionCullingMode
    VulkanDiagnosticPreset = $VulkanDiagnosticPreset
    UnitTestVrMode = $UnitTestVrMode
}

if ($GpuTimestampDense) {
    $arguments['GpuTimestampDense'] = $true
}

if ($NoClearCachesBetweenVariants) {
    $arguments['NoClearCachesBetweenVariants'] = $true
}

if ($NoP3Logging) {
    $arguments['NoP3Logging'] = $true
}

if ($FailOnSteadyStateResourceChurn) {
    $arguments['FailOnSteadyStateResourceChurn'] = $true
}

if ($FailOnSteadyStateCommandBufferChurn) {
    $arguments['FailOnSteadyStateCommandBufferChurn'] = $true
}

if ($FailOnSteadyStateCommandBufferAllocations) {
    $arguments['FailOnSteadyStateCommandBufferAllocations'] = $true
}

if ($NoStabilityGate) {
    $arguments['NoStabilityGate'] = $true
}

if ($VulkanCommandBufferLabels) {
    $arguments['VulkanCommandBufferLabels'] = $true
}

if ($DisableMcpDiagnostics) {
    $arguments['DisableMcpDiagnostics'] = $true
}

& $measureScript @arguments
