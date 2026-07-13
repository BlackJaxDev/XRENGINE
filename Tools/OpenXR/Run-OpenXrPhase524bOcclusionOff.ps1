[CmdletBinding()]
param(
    [Parameter()]
    [string]$RuntimeJson,

    [Parameter()]
    [ValidateRange(1, 10000)]
    [int]$WarmupFrames = 100,

    [Parameter()]
    [ValidateRange(1, 10000)]
    [int]$RetainedFrames = 300,

    [Parameter()]
    [ValidateRange(0, 10000)]
    [int]$CaptureSkipFrames = 10,

    [Parameter()]
    [ValidateSet("Off", "Fixed", "EyeTracked", "RuntimePreferred")]
    [string]$FoveationMode = "Off",

    [Parameter()]
    [ValidateRange(0.5, 1.0)]
    [double]$TsrResolutionScale = 1.0,

    [Parameter()]
    [AllowEmptyCollection()]
    [string[]]$ExternallyOwnedValidationAllowlist = @(),

    [Parameter()]
    [ValidateRange(30, 3600)]
    [int]$TimeoutSeconds = 900,

    [Parameter()]
    [string]$RunRoot,

    [Parameter()]
    [switch]$NoBuild,

    [Parameter()]
    [switch]$StartService
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$LASTEXITCODE = 0

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$runner = Join-Path $PSScriptRoot "Run-OpenXrMonadoSmoke.ps1"
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path $repoRoot "Build\_AgentValidation\$(Get-Date -Format yyyyMMdd-HHmmss)-phase524b-occlusion-off"
}
elseif (-not [System.IO.Path]::IsPathRooted($RunRoot)) {
    $RunRoot = Join-Path $repoRoot $RunRoot
}
$RunRoot = [System.IO.Path]::GetFullPath($RunRoot)
$captureDirectory = Join-Path $RunRoot "mcp-captures"
[System.IO.Directory]::CreateDirectory($captureDirectory) | Out-Null
if ($WarmupFrames -lt 72) {
    throw "WarmupFrames=$WarmupFrames must be at least 72 so the deterministic temporal sequence completes before the retained occlusion-off cohort."
}
Get-ChildItem -LiteralPath $captureDirectory -File -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -like "DefaultPipelineSps_*.png" -or
        $_.Name -like "DefaultPipelineSps_*.png.metrics.json" -or
        $_.Name -like "DefaultPipelineDesktop_*.png" -or
        $_.Name -like "DefaultPipelineDesktop_*.png.metrics.json" -or
        $_.Name -like "OpenXrSps_*.png" -or
        $_.Name -like "OpenXrSps_*.png.metrics.json"
    } |
    Remove-Item -Force

$environment = [ordered]@{
    XRE_UNIT_TEST_VR_VIEW_RENDER_MODE = "SinglePassStereo"
    XRE_UNIT_TEST_VR_FOVEATION_MODE = $FoveationMode
    XRE_UNIT_TEST_RENDER_WINDOWS_WHILE_IN_VR = "1"
    XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS = "1"
    XRE_OCCLUSION_CULLING_MODE = "Disabled"
    XRE_VK_RENDER_TARGET_MODE = "DynamicRendering"
    XRE_VULKAN_DIAGNOSTIC_PRESET = "SyncValidation"
    XRE_VULKAN_VALIDATION = "1"
    XRE_VULKAN_SYNC_VALIDATION = "1"
    XRE_VULKAN_COMMAND_CHAIN_VALIDATE = "1"
    XRE_VULKAN_CAPTURE_EYE_OUTPUTS = "1"
    XRE_DIAG_POSTPROCESS = "1"
    XRE_VULKAN_PHASE524B_VALIDATION = "1"
    XRE_VULKAN_PHASE524B_TSR_RESOLUTION_SCALE = $TsrResolutionScale.ToString("0.####", [System.Globalization.CultureInfo]::InvariantCulture)
    XRE_CAPTURE_DEFAULT_PIPELINE_FBO = "1"
    XRE_CAPTURE_DEFAULT_PIPELINE_SKIP_FRAMES = [string]$CaptureSkipFrames
    XRE_CAPTURE_DEFAULT_PIPELINE_OUTPUT_DIR = $captureDirectory
    XRE_VULKAN_EXTERNAL_VALIDATION_ALLOWLIST = ($ExternallyOwnedValidationAllowlist -join ";")
}
$previousEnvironment = @{}
foreach ($entry in $environment.GetEnumerator()) {
    $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
    [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, "Process")
}

try {
    $runnerArguments = @{
        Renderer = "Vulkan"
        SmokeFrames = $RetainedFrames
        WarmupFrames = $WarmupFrames
        TimeoutSeconds = $TimeoutSeconds
        RunRoot = $RunRoot
        SkipAllocationAudit = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($RuntimeJson)) { $runnerArguments.RuntimeJson = $RuntimeJson }
    if ($NoBuild) { $runnerArguments.NoBuild = $true }
    if ($StartService) { $runnerArguments.StartService = $true }

    & $runner @runnerArguments
    exit $LASTEXITCODE
}
finally {
    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }
}
