param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('Disabled', 'Shapes', 'Contacts', 'Joints', 'Simulation', 'All')]
    [string[]]$Presets = @('Disabled', 'Shapes', 'Contacts', 'Joints', 'Simulation', 'All'),
    [int]$WarmupSec = 25,
    [int]$CaptureSec = 30,
    [int]$Repetitions = 1,
    [string]$Strategy = 'GpuIndirectZeroReadback',
    [ValidateSet('Configured', 'Desktop', 'Emulated', 'MonadoOpenXR', 'OpenVR', 'OpenXR')]
    [string]$ViewMode = 'Desktop'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$measureScript = Join-Path $repoRoot 'Tools\Measure-GameLoopRenderPipeline.ps1'

foreach ($preset in $Presets) {
    $env:XRE_PHYSICS_DEBUG_PRESET = $preset
    try {
        & $measureScript `
            -WarmupSec $WarmupSec `
            -CaptureSec $CaptureSec `
            -Repetitions $Repetitions `
            -Strategies $Strategy `
            -Configuration $Configuration `
            -CacheMode Warm `
            -UnitTestVrMode $ViewMode `
            -RunLabel "physics-debug-$($preset.ToLowerInvariant())" `
            -RetainedRunCount 20
    }
    finally {
        Remove-Item Env:\XRE_PHYSICS_DEBUG_PRESET -ErrorAction SilentlyContinue
    }
}
