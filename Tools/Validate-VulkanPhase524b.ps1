[CmdletBinding()]
param(
    [Parameter()]
    [string]$RuntimeJson,

    [Parameter()]
    [string]$VulkanSdkRoot,

    [Parameter()]
    [ValidateRange(0, 10000)]
    [int]$WarmupFrames = 100,

    [Parameter()]
    [ValidateRange(0, 10000)]
    [int]$CaptureSkipFrames = 10,

    [Parameter()]
    [ValidateRange(1, 10000)]
    [int]$CaptureSettleFrames = 50,

    [Parameter()]
    [ValidateRange(1, 10000)]
    [int]$RetainedFrames = 300,

    [Parameter()]
    [ValidateRange(30, 3600)]
    [int]$TimeoutSeconds = 900,

    [Parameter()]
    [ValidateRange(0.0, 1000.0)]
    [double]$MinimumObservedFramesPerSecond = 0.0,

    [Parameter()]
    [ValidateRange(0.0, 1000.0)]
    [double]$MaximumCpuFrameP95Milliseconds = 0.0,

    [Parameter()]
    [ValidateRange(1, 120)]
    [int]$MaximumOcclusionRecoveryAgeFrames = 8,

    [Parameter()]
    [ValidateRange(1, 120)]
    [int]$MaximumOcclusionResultAgeFrames = 12,

    [Parameter()]
    [ValidateRange(1, 1000000)]
    [int]$MaximumLiveResourceCount = 100000,

    [Parameter()]
    [ValidateRange(1, 1000000)]
    [int]$MaximumTrackedDescriptorSetCount = 100000,

    [Parameter()]
    [ValidateRange(1, 128)]
    [int]$MaximumPlannerStateCount = 16,

    [Parameter()]
    [ValidateRange(1, 128)]
    [int]$MaximumCommandVariantCount = 16,

    [Parameter()]
    [ValidateRange(1, 150)]
    [int]$SteadyStateWindowFrames = 30,

    [Parameter()]
    [ValidateRange(1, 16384)]
    [int]$ExpectedSpsWidth = 896,

    [Parameter()]
    [ValidateRange(1, 16384)]
    [int]$ExpectedSpsHeight = 1007,

    [Parameter()]
    [AllowEmptyCollection()]
    [string[]]$ExternallyOwnedValidationAllowlist = @(),

    [Parameter()]
    [ValidateSet("Off", "Fixed", "EyeTracked", "RuntimePreferred")]
    [string]$FoveationMode = "Off",

    [Parameter()]
    [ValidateRange(0.5, 1.0)]
    [double]$TsrResolutionScale = 1.0,

    [Parameter()]
    [ValidateRange(0.5, 0.99)]
    [double]$SubNativeTsrResolutionScale = 0.67,

    [Parameter()]
    [string]$RunRoot,

    [Parameter()]
    [string]$StrictFailureReportPath,

    [Parameter()]
    [string]$OcclusionOffSummaryPath,

    [Parameter()]
    [string]$EditorDll,

    [Parameter()]
    [switch]$NoBuild,

    [Parameter()]
    [switch]$StartService,

    [Parameter()]
    [switch]$SkipSubNativeCompanion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$LASTEXITCODE = 0

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$runner = Join-Path $repoRoot "Tools\OpenXR\Run-OpenXrMonadoSmoke.ps1"

if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $RunRoot = Join-Path $repoRoot "Build\_AgentValidation\$stamp-vulkan-phase524b"
}
elseif (-not [System.IO.Path]::IsPathRooted($RunRoot)) {
    $RunRoot = Join-Path $repoRoot $RunRoot
}
$RunRoot = [System.IO.Path]::GetFullPath($RunRoot)
$reports = Join-Path $RunRoot "reports"
$logs = Join-Path $RunRoot "logs"
$resultPath = Join-Path $reports "vulkan-phase524b-validation.json"
$failures = [System.Collections.Generic.List[string]]::new()
[System.IO.Directory]::CreateDirectory($reports) | Out-Null
[System.IO.Directory]::CreateDirectory($logs) | Out-Null

$currentPowerShellExecutable = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
if ([string]::IsNullOrWhiteSpace($currentPowerShellExecutable) -or
    -not (Test-Path -LiteralPath $currentPowerShellExecutable -PathType Leaf)) {
    throw "Unable to resolve the executable for the current PowerShell host."
}
$currentPowerShellExecutable = [System.IO.Path]::GetFullPath($currentPowerShellExecutable)

trap {
    $fatalErrorRecord = $_
    $fatalMessage = "Validator terminated before completing the report: $($fatalErrorRecord.Exception.Message)"
    if (-not $failures.Contains($fatalMessage)) {
        $failures.Add($fatalMessage)
    }
    $fatalResult = [ordered]@{
        schemaVersion = 3
        capturedAtUtc = [DateTimeOffset]::UtcNow
        passed = $false
        runRoot = $RunRoot
        configuration = [ordered]@{
            powerShellExecutable = $currentPowerShellExecutable
            powerShellVersion = [string]$PSVersionTable.PSVersion
        }
        fatalError = [ordered]@{
            message = $fatalErrorRecord.Exception.Message
            exceptionType = $fatalErrorRecord.Exception.GetType().FullName
            scriptStackTrace = $fatalErrorRecord.ScriptStackTrace
        }
        failures = @($failures)
    }
    try {
        $fatalResult | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    }
    catch {
        [Console]::Error.WriteLine("Failed to write fatal Vulkan Phase 5.2.4b report '$resultPath': $($_.Exception.Message)")
    }
    [Console]::Error.WriteLine($fatalMessage)
    exit 1
}

if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw "OpenXR Monado smoke runner was not found: $runner"
}

$captureMotionSampleCount = 3
$captureMotionIntervalFrames = 15
$temporalScenarioSequenceCompleteFrame = 72
$boundaryKeyProbe = @{}
$probeStage = "PublishStaging"
$probeViewKind = "LeftEye"
$probeMotionIndex = 2
$boundaryKeyProbe["${probeStage}:${probeViewKind}:${probeMotionIndex}"] = $true
if (-not $boundaryKeyProbe.ContainsKey("PublishStaging:LeftEye:2")) {
    throw "Boundary capture attribution-key interpolation is invalid."
}
$captureSequenceEndFrame = $CaptureSkipFrames + (($captureMotionSampleCount - 1) * $captureMotionIntervalFrames)
if ($WarmupFrames -lt ($captureSequenceEndFrame + $CaptureSettleFrames)) {
    throw "WarmupFrames=$WarmupFrames must be at least captureSequenceEnd+CaptureSettleFrames=$($captureSequenceEndFrame + $CaptureSettleFrames) so all motion/readback captures finish before the retained cohort."
}
if ($WarmupFrames -lt $temporalScenarioSequenceCompleteFrame) {
    throw "WarmupFrames=$WarmupFrames must be at least temporalScenarioSequenceCompleteFrame=$temporalScenarioSequenceCompleteFrame so every deterministic temporal sample is rendered before the retained cohort."
}

function Get-JsonArray {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return @()
    }
    if ($Value -is [System.Array]) {
        return @($Value)
    }
    $values = $Value.PSObject.Properties['$values']
    if ($null -ne $values) {
        return @($values.Value)
    }
    return @($Value)
}

function Get-Percentile {
    param(
        [Parameter(Mandatory)][double[]]$Sorted,
        [Parameter(Mandatory)][double]$Percentile
    )

    if ($Sorted.Count -eq 0) {
        return 0.0
    }
    if ($Sorted.Count -eq 1) {
        return $Sorted[0]
    }

    $clampedPercentile = [Math]::Min(1.0, [Math]::Max(0.0, $Percentile))
    $position = $clampedPercentile * ($Sorted.Count - 1)
    $lower = [Math]::Floor($position)
    $upper = [Math]::Ceiling($position)
    if ($lower -eq $upper) {
        return $Sorted[$lower]
    }
    $weight = $position - $lower
    return $Sorted[$lower] + (($Sorted[$upper] - $Sorted[$lower]) * $weight)
}

function Get-PropertySum {
    param(
        [AllowEmptyCollection()][object[]]$Items,
        [Parameter(Mandatory)][string]$PropertyName
    )

    $sum = 0L
    foreach ($item in @($Items)) {
        if ($null -eq $item) {
            continue
        }
        $property = $item.PSObject.Properties[$PropertyName]
        if ($null -ne $property -and $null -ne $property.Value) {
            $sum += [long]$property.Value
        }
    }
    return $sum
}

function Resolve-VulkanSdkRoot {
    param([string]$RequestedRoot)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($RequestedRoot)) {
        $candidates.Add($RequestedRoot)
    }
    if (-not [string]::IsNullOrWhiteSpace($env:VULKAN_SDK)) {
        $candidates.Add($env:VULKAN_SDK)
    }
    foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
        $sdkParent = Join-Path $drive.Root "VulkanSDK"
        if (-not (Test-Path -LiteralPath $sdkParent -PathType Container)) {
            continue
        }
        foreach ($directory in Get-ChildItem -LiteralPath $sdkParent -Directory -ErrorAction SilentlyContinue) {
            $candidates.Add($directory.FullName)
        }
    }

    $valid = foreach ($candidate in $candidates | Select-Object -Unique) {
        $fullPath = [System.IO.Path]::GetFullPath($candidate)
        $manifestPath = Join-Path $fullPath "Bin\VkLayer_khronos_validation.json"
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            continue
        }

        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $apiVersionText = [string]$manifest.layer.api_version
        $parsedVersion = $null
        if (-not [Version]::TryParse($apiVersionText, [ref]$parsedVersion)) {
            continue
        }

        [pscustomobject]@{
            Root = $fullPath
            ApiVersion = $parsedVersion
            ApiVersionText = $apiVersionText
            ManifestPath = $manifestPath
        }
    }

    $selected = $valid | Sort-Object ApiVersion -Descending | Select-Object -First 1
    if ($null -eq $selected) {
        throw "No Vulkan SDK with VK_LAYER_KHRONOS_validation was found. Pass -VulkanSdkRoot or install it through ExecTool."
    }
    if ($selected.ApiVersion -lt [Version]"1.4.0") {
        throw "Vulkan validation layer $($selected.ApiVersionText) at '$($selected.Root)' is too old for Phase 5.2.4b; Vulkan 1.4 validation is required."
    }

    return $selected
}

$selectedVulkanSdk = Resolve-VulkanSdkRoot -RequestedRoot $VulkanSdkRoot
$vulkanSdkBin = Join-Path $selectedVulkanSdk.Root "Bin"

function Test-FiniteDouble {
    param([Parameter(Mandatory)][double]$Value)

    return -not [double]::IsNaN($Value) -and -not [double]::IsInfinity($Value)
}

function ConvertTo-HexString {
    param(
        [Parameter(Mandatory)][byte[]]$Bytes,
        [Parameter(Mandatory)][int]$Offset,
        [Parameter(Mandatory)][int]$Count
    )

    if ($Count -le 0) {
        return ""
    }
    return [BitConverter]::ToString($Bytes, $Offset, $Count).Replace("-", "")
}

function Get-LogLineTimestamp {
    param([Parameter(Mandatory)][string]$Line)

    $match = [regex]::Match(
        $Line,
        '^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2})')
    if (-not $match.Success) {
        return $null
    }

    $parsed = [DateTimeOffset]::MinValue
    $parsedSuccessfully = [DateTimeOffset]::TryParseExact(
        $match.Groups['timestamp'].Value,
        'yyyy-MM-dd HH:mm:ss.fff zzz',
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::None,
        [ref]$parsed)
    if (-not $parsedSuccessfully) {
        return $null
    }
    return $parsed
}

function Test-ExactStringArray {
    param(
        [AllowNull()][object]$Actual,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Expected
    )

    $actualValues = @(Get-JsonArray $Actual)
    if ($actualValues.Count -ne $Expected.Count) {
        return $false
    }
    for ($i = 0; $i -lt $Expected.Count; $i++) {
        if ([string]$actualValues[$i] -cne $Expected[$i]) {
            return $false
        }
    }
    return $true
}

function Measure-SteadyStateGauge {
    param(
        [Parameter(Mandatory)][object[]]$Frames,
        [Parameter(Mandatory)][string]$PropertyName,
        [Parameter(Mandatory)][int]$WindowFrames,
        [Parameter(Mandatory)][long]$Maximum
    )

    $values = @($Frames | ForEach-Object { [long]($_.$PropertyName) })
    $window = [Math]::Min([Math]::Max(1, $WindowFrames), $values.Count)
    $first = @($values | Select-Object -First $window)
    $last = @($values | Select-Object -Last $window)
    $firstMeasure = $first | Measure-Object -Minimum -Maximum -Average
    $lastMeasure = $last | Measure-Object -Minimum -Maximum -Average
    $overall = $values | Measure-Object -Minimum -Maximum
    $hasPositiveDrift = [double]$lastMeasure.Average -gt [double]$firstMeasure.Average -or
        [long]$lastMeasure.Maximum -gt [long]$firstMeasure.Maximum

    [pscustomobject]@{
        property = $PropertyName
        maximumAllowed = $Maximum
        minimumObserved = [long]$overall.Minimum
        maximumObserved = [long]$overall.Maximum
        firstWindowAverage = [double]$firstMeasure.Average
        firstWindowMaximum = [long]$firstMeasure.Maximum
        lastWindowAverage = [double]$lastMeasure.Average
        lastWindowMaximum = [long]$lastMeasure.Maximum
        hasPositiveDrift = $hasPositiveDrift
        passed = [long]$overall.Minimum -ge 0 -and [long]$overall.Maximum -le $Maximum -and -not $hasPositiveDrift
    }
}

function Get-PngHeaderInfo {
    param([Parameter(Mandatory)][string]$Path)

    $header = [byte[]]::new(24)
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $length = $stream.Read($header, 0, $header.Length)
    }
    finally {
        $stream.Dispose()
    }
    $signature = if ($length -ge 8) { ConvertTo-HexString $header 0 8 } else { "" }
    $width = if ($length -ge 24) {
        ([uint32]$header[16] -shl 24) -bor ([uint32]$header[17] -shl 16) -bor ([uint32]$header[18] -shl 8) -bor [uint32]$header[19]
    }
    else { 0 }
    $height = if ($length -ge 24) {
        ([uint32]$header[20] -shl 24) -bor ([uint32]$header[21] -shl 16) -bor ([uint32]$header[22] -shl 8) -bor [uint32]$header[23]
    }
    else { 0 }
    return [pscustomobject]@{ signature = $signature; width = $width; height = $height }
}

function Get-FingerprintRmse {
    param(
        [AllowNull()][object]$First,
        [AllowNull()][object]$Second
    )

    $firstValues = @(Get-JsonArray $First)
    $secondValues = @(Get-JsonArray $Second)
    if ($firstValues.Count -eq 0 -or $firstValues.Count -ne $secondValues.Count) {
        return [double]::PositiveInfinity
    }
    $squaredError = 0.0
    for ($i = 0; $i -lt $firstValues.Count; $i++) {
        $delta = [double]$firstValues[$i] - [double]$secondValues[$i]
        $squaredError += $delta * $delta
    }
    return [Math]::Sqrt($squaredError / $firstValues.Count)
}

$requiredCaptureStages = @(
    "07_Velocity",
    "07b_VelocityFBO",
    "08_BloomMip0",
    "09_BloomMip1",
    "09b_BloomMip2",
    "09c_BloomMip3",
    "10_BloomMip4",
    "11_TemporalColorInput",
    "11b_CurrentDepth",
    "11c_HistoryDepth",
    "12_PostProcessOutput",
    "13_FinalPostProcessOutput",
    "13b_PreTsrHistoryColor",
    "13c_MonoTsrReference",
    "14_TsrOutput",
    "14b_TsrHistoryColor"
)
$desktopFinalCaptureStage = "15_FinalOutput"
$captureStageMipLevels = @{
    "08_BloomMip0" = 0
    "09_BloomMip1" = 1
    "09b_BloomMip2" = 2
    "09c_BloomMip3" = 3
    "10_BloomMip4" = 4
}
$temporalScenarioCaptureStages = @(
    "07_Velocity",
    "09_BloomMip1",
    "13c_MonoTsrReference",
    "14_TsrOutput"
)
$temporalScenarioDefinitions = @(
    [pscustomobject]@{ scenario = "ObjectMotion"; sample = "ObjectMotionActive"; velocityOracle = "PositiveX"; start = 8; end = 10; convergence = $false; disocclusionBaseline = $false; disocclusionResult = $false },
    [pscustomobject]@{ scenario = "StaticPose"; sample = "StaticPoseSettled"; velocityOracle = "Zero"; start = 16; end = 18; convergence = $true; disocclusionBaseline = $false; disocclusionResult = $false },
    [pscustomobject]@{ scenario = "HeadRotation"; sample = "HeadRotationActive"; velocityOracle = "PositiveX"; start = 24; end = 26; convergence = $false; disocclusionBaseline = $false; disocclusionResult = $false },
    [pscustomobject]@{ scenario = "HeadTranslation"; sample = "HeadTranslationActive"; velocityOracle = "NegativeX"; start = 32; end = 34; convergence = $false; disocclusionBaseline = $false; disocclusionResult = $false },
    [pscustomobject]@{ scenario = "Disocclusion"; sample = "DisocclusionOccluded"; velocityOracle = "Zero"; start = 38; end = 40; convergence = $false; disocclusionBaseline = $true; disocclusionResult = $false },
    [pscustomobject]@{ scenario = "Disocclusion"; sample = "DisocclusionRevealed"; velocityOracle = "Zero"; start = 50; end = 51; convergence = $true; disocclusionBaseline = $false; disocclusionResult = $true },
    [pscustomobject]@{ scenario = "MotionStop"; sample = "MotionStopMoving"; velocityOracle = "PositiveX"; start = 56; end = 58; convergence = $false; disocclusionBaseline = $false; disocclusionResult = $false },
    [pscustomobject]@{ scenario = "MotionStop"; sample = "MotionStopSettled"; velocityOracle = "Zero"; start = 68; end = 70; convergence = $true; disocclusionBaseline = $false; disocclusionResult = $false }
)

$captureDirectory = Join-Path $RunRoot "mcp-captures"
[System.IO.Directory]::CreateDirectory($captureDirectory) | Out-Null
Get-ChildItem -LiteralPath $captureDirectory -Filter "DefaultPipelineSps_*_layer*.png" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
Get-ChildItem -LiteralPath $captureDirectory -Filter "DefaultPipelineDesktop_*_layer*.png" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
Get-ChildItem -LiteralPath $captureDirectory -Filter "OpenXrSps_*.png" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
Get-ChildItem -LiteralPath $captureDirectory -Filter "*.png.metrics.json" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$environment = [ordered]@{
    VULKAN_SDK                              = $selectedVulkanSdk.Root
    VK_LAYER_PATH                          = $vulkanSdkBin
    VK_LOADER_LAYERS_DISABLE               = "~implicit~"
    PATH                                   = "$vulkanSdkBin$([System.IO.Path]::PathSeparator)$env:PATH"
    XRE_UNIT_TEST_VR_VIEW_RENDER_MODE       = "SinglePassStereo"
    XRE_UNIT_TEST_VR_FOVEATION_MODE        = $FoveationMode
    XRE_UNIT_TEST_RENDER_WINDOWS_WHILE_IN_VR = "1"
    # The acceptance workload requires the independent desktop plus the submitted
    # strict-SPS output. Per-eye editor preview viewports are a separate diagnostic
    # workload and would add two redundant scene renders to every measured frame.
    XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS  = "0"
    XRE_OCCLUSION_CULLING_MODE             = "CpuQueryAsync"
    # CpuQueryAsync is the CPU-direct hardware-query path. GPU-indirect uses its
    # own occlusion implementation and cannot satisfy this cohort's per-POV proof.
    XRE_FORCE_MESH_SUBMISSION_STRATEGY     = "CpuDirect"
    XRE_VK_RENDER_TARGET_MODE              = "DynamicRendering"
    XRE_VULKAN_DIAGNOSTIC_PRESET           = "SyncValidation"
    XRE_VULKAN_VALIDATION                  = "1"
    XRE_VULKAN_SYNC_VALIDATION             = "1"
    XRE_VULKAN_COMMAND_CHAIN_VALIDATE      = "1"
    # Per-op/target traces are intentionally disabled for the acceptance cohort:
    # they serialize thousands of log writes and invalidate the framerate gate.
    XRE_VULKAN_FRAMEOP_TRACE               = "0"
    XRE_VULKAN_TARGET_TRACE                = "0"
    XRE_VULKAN_CAPTURE_EYE_OUTPUTS         = "1"
    XRE_DIAG_POSTPROCESS                   = "1"
    XRE_VULKAN_PHASE524B_VALIDATION        = "1"
    XRE_VULKAN_PHASE524B_INJECT_DESKTOP_REJECTION = "1"
    XRE_VULKAN_PHASE524B_TSR_RESOLUTION_SCALE = $TsrResolutionScale.ToString("0.####", [System.Globalization.CultureInfo]::InvariantCulture)
    XRE_FIRST_CHANCE_EXCEPTIONS            = "Vulkan"
    XRE_CAPTURE_DEFAULT_PIPELINE_FBO       = "1"
    XRE_CAPTURE_DEFAULT_PIPELINE_SKIP_FRAMES = [string]$CaptureSkipFrames
    XRE_CAPTURE_DEFAULT_PIPELINE_OUTPUT_DIR = $captureDirectory
    XRE_VULKAN_EXTERNAL_VALIDATION_ALLOWLIST = ($ExternallyOwnedValidationAllowlist -join ";")
}

$previousEnvironment = @{}
foreach ($entry in $environment.GetEnumerator()) {
    $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, "Process")
    [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, "Process")
}

$runnerExitCode = 1
try {
    # The smoke runner is also a standalone tool and uses process exit codes.
    # Run it in a child PowerShell process so its `exit` cannot terminate this
    # validator before the retained-frame ledger is checked.
    $runnerArguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $runner,
        "-Renderer", "Vulkan",
        "-SmokeFrames", [string]$RetainedFrames,
        "-WarmupFrames", [string]$WarmupFrames,
        "-TimeoutSeconds", [string]$TimeoutSeconds,
        "-RunRoot", $RunRoot,
        "-SkipAllocationAudit",
        "-SkipLoaderPreflight"
    )
    if (-not [string]::IsNullOrWhiteSpace($RuntimeJson)) {
        $runnerArguments += @("-RuntimeJson", $RuntimeJson)
    }
    if (-not [string]::IsNullOrWhiteSpace($EditorDll)) {
        $runnerArguments += @("-EditorDll", $EditorDll)
    }
    if ($NoBuild) {
        $runnerArguments += "-NoBuild"
    }
    if ($StartService) {
        $runnerArguments += @("-StartService", "-RequireOwnedService", "-SimulatedHmdPoseMode", "stationary")
    }

    $runnerProcess = Start-Process `
        -FilePath "powershell.exe" `
        -ArgumentList $runnerArguments `
        -WorkingDirectory $repoRoot `
        -PassThru `
        -WindowStyle Hidden
    $runnerDeadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds + 60)
    while (-not $runnerProcess.HasExited) {
        if ([DateTimeOffset]::UtcNow -ge $runnerDeadline) {
            try {
                $runnerProcess.Kill()
            }
            catch {
            }
            throw "OpenXR smoke runner exceeded the validator deadline of $($TimeoutSeconds + 60) seconds."
        }
        Start-Sleep -Milliseconds 250
        $runnerProcess.Refresh()
    }
    $runnerExitCode = $runnerProcess.ExitCode
    $runnerProcess.Dispose()
    Write-Host "Phase 5.2.4b smoke runner completed with exit code $runnerExitCode."
}
finally {
    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, "Process")
    }
}

$summaryPath = Join-Path $reports "openxr-smoke-summary.json"
if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
    throw "Phase 5.2.4b smoke summary was not written: $summaryPath"
}

$summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
$frameLedger = @(Get-JsonArray $summary.frameLedger)
$occlusionViewLedger = @(Get-JsonArray $summary.occlusionViewLedger)
$occlusionEvidenceLedger = @(Get-JsonArray $summary.occlusionEvidenceLedger)
$outputLedger = @(Get-JsonArray $summary.outputLedger)
$captureLedger = @(Get-JsonArray $summary.captureLedger)
$temporalScenarioMatrix = @(Get-JsonArray $summary.temporalScenarioMatrix)
$temporalScenarioCaptureLedger = @(Get-JsonArray $summary.temporalScenarioCaptureLedger)
$temporalStateLedger = @(Get-JsonArray $summary.temporalStateLedger)
$strictFailureStages = @("Capability", "Target", "Recording", "LifetimeValidation", "Submit", "Publish")
$strictFailureReport = $null
$strictFailureEntries = @()
if ([string]::IsNullOrWhiteSpace($StrictFailureReportPath)) {
    $failures.Add("StrictFailureReportPath is required; run Tools/OpenXR/Validate-OpenXrStrictSpsFailures.ps1 and pass its aggregate report.")
}
else {
    if (-not [System.IO.Path]::IsPathRooted($StrictFailureReportPath)) {
        $StrictFailureReportPath = Join-Path $repoRoot $StrictFailureReportPath
    }
    $StrictFailureReportPath = [System.IO.Path]::GetFullPath($StrictFailureReportPath)
    if (-not (Test-Path -LiteralPath $StrictFailureReportPath -PathType Leaf)) {
        $failures.Add("Strict SPS failure-matrix report was not found: $StrictFailureReportPath")
    }
    else {
        $strictFailureReport = Get-Content -LiteralPath $StrictFailureReportPath -Raw | ConvertFrom-Json
        $strictFailureEntries = @(Get-JsonArray $strictFailureReport.entries)
        if (-not [bool]$strictFailureReport.passed -or
            -not (Test-ExactStringArray $strictFailureReport.expectedStages $strictFailureStages) -or
            $strictFailureEntries.Count -ne $strictFailureStages.Count) {
            $failures.Add("Strict SPS failure-matrix report is incomplete or failed.")
        }
        foreach ($stage in $strictFailureStages) {
            $stageEntries = @($strictFailureEntries | Where-Object { [string]$_.stage -ceq $stage })
            if ($stageEntries.Count -ne 1 -or
                -not [bool]$stageEntries[0].passed -or
                [long]$stageEntries[0].completedFrameCount -ne 12 -or
                [int]$stageEntries[0].warmupFrameCount -ne 4 -or
                [int]$stageEntries[0].retainedFrameCount -ne 8 -or
                [long]$stageEntries[0].submittedFrameCount -lt 4 -or
                ([long]$stageEntries[0].submittedFrameCount + [long]$stageEntries[0].noLayerFrameCount) -ne 12 -or
                [long]$stageEntries[0].noLayerFrameCount -lt 1 -or
                [int]$stageEntries[0].retainedZeroLayerFrameCount -ne 1 -or
                [long]$stageEntries[0].successfulSpsSubmissionCount -lt 4 -or
                [int]$stageEntries[0].actualEndFrameResult -ne 0 -or
                [uint32]$stageEntries[0].actualEndFrameLayerCount -ne 0 -or
                -not [bool]$stageEntries[0].handled -or
                [uint32]$stageEntries[0].projectionLayerCount -ne 0 -or
                [bool]$stageEntries[0].sequentialFallbackRequested -or
                [long]$stageEntries[0].sequentialFallbackAttemptDelta -ne 0 -or
                [long]$stageEntries[0].globalSequentialFallbackAttemptCount -ne 0 -or
                [string]$stageEntries[0].viewRenderModeRequested -cne "SinglePassStereo" -or
                [string]$stageEntries[0].viewRenderModeEffective -cne "SinglePassStereo" -or
                [string]$stageEntries[0].viewRenderImplementationPath -cne "TrueSinglePassStereo") {
                $failures.Add("Strict SPS failure stage '$stage' did not prove handled/no-layer/no-fallback behavior.")
            }
        }
    }
}

if ($runnerExitCode -ne 0) {
    $failures.Add("Monado smoke runner exited with code $runnerExitCode.")
}
if ([int]$summary.schemaVersion -lt 8) {
    $failures.Add("OpenXR smoke schemaVersion=$($summary.schemaVersion); Phase 5.2.4b requires schema 8 or newer.")
}
if ($RetainedFrames -ne 300) {
    $failures.Add("Phase 5.2.4b requires exactly 300 retained frames; requested $RetainedFrames.")
}
if ([string]$summary.viewRenderModeRequested -ne "SinglePassStereo" -or
    [string]$summary.viewRenderModeEffective -ne "SinglePassStereo" -or
    [string]$summary.viewRenderImplementationPath -ne "TrueSinglePassStereo" -or
    -not [bool]$summary.viewRenderModeSupported) {
    $failures.Add("Strict SPS was not effective: requested=$($summary.viewRenderModeRequested) effective=$($summary.viewRenderModeEffective) path=$($summary.viewRenderImplementationPath) supported=$($summary.viewRenderModeSupported).")
}
if ([string]$summary.rendererBackend -ne "Vulkan" -or
    [string]$summary.vulkanRenderTargetModeRequested -ne "DynamicRendering" -or
    [string]$summary.vulkanRenderTargetModeEffective -ne "DynamicRendering") {
    $failures.Add("Vulkan dynamic rendering was not requested and effective: backend=$($summary.rendererBackend) requested=$($summary.vulkanRenderTargetModeRequested) effective=$($summary.vulkanRenderTargetModeEffective).")
}
if ([string]$summary.vulkanDiagnosticPresetRequested -ne "SyncValidation" -or
    [string]$summary.vulkanDiagnosticPresetEffective -ne "SyncValidation" -or
    -not [bool]$summary.vulkanValidationLayersEffective -or
    -not [bool]$summary.vulkanSynchronizationValidationEffective -or
    -not (Test-ExactStringArray $summary.vulkanValidationLayers @("VK_LAYER_KHRONOS_validation")) -or
    -not (Test-ExactStringArray $summary.vulkanValidationFeatures @("SynchronizationValidation"))) {
    $failures.Add("Effective Vulkan synchronization validation settings are incomplete or Off: requested=$($summary.vulkanDiagnosticPresetRequested) effective=$($summary.vulkanDiagnosticPresetEffective) layers=$(@(Get-JsonArray $summary.vulkanValidationLayers) -join ',') features=$(@(Get-JsonArray $summary.vulkanValidationFeatures) -join ',').")
}
if (-not (Test-ExactStringArray $summary.externallyOwnedValidationAllowlist $ExternallyOwnedValidationAllowlist)) {
    $failures.Add("The smoke summary did not record the exact externally owned validation allowlist.")
}
if ([string]$summary.antiAliasingModeEffective -ne "Tsr" -or
    [string]$summary.occlusionCullingModeRequested -ne "CpuQueryAsync" -or
    [string]$summary.occlusionCullingModeEffective -ne "CpuQueryAsync" -or
    [string]$summary.mirrorModeEffective -ne "FullIndependentRender") {
    $failures.Add("Effective rendering settings are incorrect: AA=$($summary.antiAliasingModeEffective) occlusionRequested=$($summary.occlusionCullingModeRequested) occlusionEffective=$($summary.occlusionCullingModeEffective) mirror=$($summary.mirrorModeEffective).")
}
if (-not (Test-FiniteDouble ([double]$summary.tsrResolutionScaleRequested)) -or
    -not (Test-FiniteDouble ([double]$summary.tsrResolutionScaleEffective)) -or
    [Math]::Abs([double]$summary.tsrResolutionScaleRequested - $TsrResolutionScale) -gt 0.0001 -or
    [Math]::Abs([double]$summary.tsrResolutionScaleEffective - $TsrResolutionScale) -gt 0.0001) {
    $failures.Add("TSR resolution scale was not requested/effective as ${TsrResolutionScale}: requested=$($summary.tsrResolutionScaleRequested) effective=$($summary.tsrResolutionScaleEffective).")
}
$desktopRejection = $summary.desktopRejectionEvidence
if ($null -eq $desktopRejection -or
    -not [bool]$desktopRejection.injected -or -not [bool]$desktopRejection.observed -or
    [bool]$desktopRejection.clearedTargetPublished -or
    ([bool]$desktopRejection.skippedPresent -eq [bool]$desktopRejection.presentedLastCompletedImage) -or
    ([bool]$desktopRejection.presentedLastCompletedImage -and -not [bool]$desktopRejection.presentAccepted) -or
    -not [bool]$desktopRejection.exposureFinite -or -not (Test-FiniteDouble ([double]$desktopRejection.exposure)) -or
    -not [bool]$desktopRejection.exposureHistoryFinite -or -not (Test-FiniteDouble ([double]$desktopRejection.exposureHistory)) -or
    ([bool]$desktopRejection.exposureNonZeroRequired -and [double]$desktopRejection.exposure -le 0.0) -or
    ([bool]$desktopRejection.exposureHistoryNonZeroRequired -and [double]$desktopRejection.exposureHistory -le 0.0) -or
    -not [bool]$desktopRejection.exposureOwnerMatchesDesktop -or
    [int]$desktopRejection.pipelineInstanceId -le 0 -or [uint64]$desktopRejection.outputId -eq 0 -or
    [uint64]$desktopRejection.renderFrameId -eq 0) {
    $failures.Add("Controlled rejected desktop frame lacks a legal no-cleared-target policy or finite/nonzero desktop-owned exposure/history evidence.")
}
$summaryCaptureDirectory = if ([string]::IsNullOrWhiteSpace([string]$summary.defaultPipelineCaptureOutputDirectory)) {
    ""
}
else {
    [System.IO.Path]::GetFullPath([string]$summary.defaultPipelineCaptureOutputDirectory)
}
if (-not [bool]$summary.defaultPipelineCaptureEnabled -or
    [int]$summary.defaultPipelineCaptureSkipFrames -ne $CaptureSkipFrames -or
    -not [string]::Equals($summaryCaptureDirectory, [System.IO.Path]::GetFullPath($captureDirectory), [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-ExactStringArray $summary.requiredCaptureStages $requiredCaptureStages) -or
    [string]$summary.desktopFinalCaptureStage -cne $desktopFinalCaptureStage) {
    $failures.Add("Default-pipeline both-layer capture configuration was not recorded exactly for the retained cohort.")
}
if ($null -eq $summary.retainedCohortStartedAtUtc) {
    $failures.Add("The retained-cohort start timestamp was not recorded.")
}
if ([int]$summary.warmupFrameCount -ne $WarmupFrames) {
    $failures.Add("Warmup frame count was $($summary.warmupFrameCount), expected $WarmupFrames.")
}
if ([int]$summary.retainedFrameCount -ne $RetainedFrames -or $frameLedger.Count -ne $RetainedFrames) {
    $failures.Add("Retained frame ledger contained $($frameLedger.Count) frames, expected exactly $RetainedFrames.")
}
if ([long]$summary.endFrameFailureCount -ne 0) {
    $failures.Add("xrEndFrame failure count was $($summary.endFrameFailureCount).")
}
if ([long]$summary.strictSinglePassStereoSequentialFallbackAttemptCount -ne 0) {
    $failures.Add("Strict SPS attempted sequential fallback $($summary.strictSinglePassStereoSequentialFallbackAttemptCount) time(s).")
}
if (@(Get-JsonArray $summary.failures).Count -ne 0) {
    $failures.Add("OpenXR smoke summary reported failure(s): $(@(Get-JsonArray $summary.failures) -join ' | ')")
}
if ([bool]$summary.occlusionViewLedgerOverflow) {
    $failures.Add("The per-frame keyed occlusion ledger overflowed its bounded capacity.")
}
if ([long]$summary.occlusionEvidenceOverflowCount -ne 0) {
    $failures.Add("The named occlusion evidence ring overflowed $($summary.occlusionEvidenceOverflowCount) time(s).")
}
if ([bool]$summary.outputLedgerOverflow) {
    $failures.Add("The per-frame output ledger overflowed its bounded capacity.")
}
if (-not [bool]$summary.teardownCompleted) {
    $failures.Add("OpenXR teardown did not complete.")
}

$frameTimes = [System.Collections.Generic.List[double]]::new($frameLedger.Count)
$gpuTimes = [System.Collections.Generic.List[double]]::new($frameLedger.Count)
$occlusionTestedTotal = 0L
$occlusionCulledTotal = 0L
$occlusionSubmittedTotal = 0L
$occlusionResolvedTotal = 0L
$maxOcclusionAge = 0
$allocationFrames = 0
$descriptorChurnFrames = 0
$retirementFrames = 0
$planReplacementFrames = 0
$recordFrames = 0
$reuseFrames = 0

for ($i = 0; $i -lt $frameLedger.Count; $i++) {
    $frame = $frameLedger[$i]
    if (-not [bool]$frame.projectionLayerSubmitted) {
        $failures.Add("Retained frame $i did not submit valid projection layers.")
    }
    if ([int]$frame.endFrameResult -ne 0 -or [uint32]$frame.endFrameLayerCount -ne 1) {
        $failures.Add("Retained frame $i did not complete xrEndFrame with one projection layer: result=$($frame.endFrameResult) layers=$($frame.endFrameLayerCount).")
    }
    if ([long]$frame.leftAcquireDelta -ne 1 -or [long]$frame.rightAcquireDelta -ne 1 -or
        [long]$frame.leftWaitDelta -ne 1 -or [long]$frame.rightWaitDelta -ne 1 -or
        [long]$frame.leftPublishDelta -ne 1 -or [long]$frame.rightPublishDelta -ne 1 -or
        [long]$frame.leftReleaseDelta -ne 1 -or [long]$frame.rightReleaseDelta -ne 1) {
        $failures.Add("Retained frame $i did not complete exactly one acquire/wait/publish/release per eye.")
    }
    if ([int]$frame.leftExternalImageSlot -lt 0 -or [int]$frame.rightExternalImageSlot -lt 0) {
        $failures.Add("Retained frame $i did not record both acquired external image slots.")
    }
    if ([long]$frame.strictSequentialFallbackAttemptCount -ne 0 -or [long]$frame.strictSequentialFallbackAttemptDelta -ne 0) {
        $failures.Add("Retained frame $i recorded a strict-SPS sequential fallback attempt.")
    }
    if ([int]$frame.submissionRejectionCount -ne 0 -or [int]$frame.globalInFlightWaitCount -ne 0 -or
        [int]$frame.forceFlushCount -ne 0 -or [int]$frame.unapprovedPolicyEventCount -ne 0) {
        $failures.Add("Retained frame $i violated submit/wait/flush policy: rejected=$($frame.submissionRejectionCount) waits=$($frame.globalInFlightWaitCount) flushes=$($frame.forceFlushCount) policy=$($frame.unapprovedPolicyEventCount).")
    }
    if ([int]$frame.queueSubmitCount -le 0) {
        $failures.Add("Retained frame $i recorded no Vulkan queue submit.")
    }
    if (-not [bool]$frame.vrActive -or [string]$frame.mirrorMode -ne "FullIndependentRender" -or
        [uint64]$frame.outputManifestFrameId -eq 0 -or [int]$frame.outputRequestCount -le 0) {
        $failures.Add("Retained frame $i has an invalid output manifest or mirror mode.")
    }
    if ([int]$frame.validationErrorCount -ne 0) {
        $failures.Add("Retained frame $i recorded $($frame.validationErrorCount) Vulkan validation errors.")
    }
    if (-not [bool]$frame.validationLayersEnabled -or
        -not [bool]$frame.synchronizationValidationEnabled -or
        -not [bool]$frame.lifetimeValidationEnabled -or
        -not [bool]$frame.lifetimeValidationPassed) {
        $failures.Add("Retained frame $i did not run and pass validation/lifetime checks: layers=$($frame.validationLayersEnabled) sync=$($frame.synchronizationValidationEnabled) lifetimeEnabled=$($frame.lifetimeValidationEnabled) lifetimePassed=$($frame.lifetimeValidationPassed).")
    }
    if (-not [bool]$frame.submitCompleted) {
        $failures.Add("Retained frame $i did not record a completed Vulkan/OpenXR submit with both eye submit phases.")
    }
    if ([int]$frame.globalFallbackInvalidationCount -ne 0) {
        $failures.Add("Retained frame $i used $($frame.globalFallbackInvalidationCount) global invalidation fallbacks.")
    }
    if ([int]$frame.resourcePlanReplacementCount -ne 0) {
        $planReplacementFrames++
    }
    if ([int]$frame.deviceLocalAllocationCount -ne 0 -or [int]$frame.uploadAllocationCount -ne 0) {
        $allocationFrames++
    }
    if ([int]$frame.descriptorPoolCreateCount -ne 0 -or
        [int]$frame.descriptorPoolDestroyCount -ne 0 -or
        [int]$frame.descriptorPoolResetCount -ne 0) {
        $descriptorChurnFrames++
    }
    if ([int]$frame.retiredResourceCount -ne 0) {
        $retirementFrames++
    }
    if ([int]$frame.commandBufferRecordCount -gt 0 -or [int]$frame.primaryCommandBufferRecordCount -gt 0) {
        $recordFrames++
    }
    if ([int]$frame.commandBufferCleanReuseCount -gt 0 -or [int]$frame.primaryCommandBufferReuseCount -gt 0) {
        $reuseFrames++
    }
    if ([int]$frame.sceneSwapchainWriterCount -le 0 -and [int]$frame.swapchainWriteCount -le 0) {
        $failures.Add("Retained frame $i recorded no desktop swapchain writer.")
    }
    if ([int]$frame.missingSceneSwapchainWriteCount -ne 0) {
        $failures.Add("Retained frame $i recorded a missing desktop scene swapchain write.")
    }
    if (-not [bool]$frame.desktopFinalWriteObserved) {
        $failures.Add("Retained frame $i did not record a desktop final write.")
    }
    if (-not [bool]$frame.desktopPresentObserved -or
        [int]$frame.desktopPresentAttemptCount -ne 1 -or
        -not [bool]$frame.desktopPresentAccepted -or
        [string]$frame.desktopPresentResult -ne "0") {
        $failures.Add("Retained frame $i did not complete exactly one successful desktop present: observed=$($frame.desktopPresentObserved) attempts=$($frame.desktopPresentAttemptCount) accepted=$($frame.desktopPresentAccepted) result=$($frame.desktopPresentResult).")
    }
    if ([uint64]$frame.resourcePlanGeneration -eq 0 -or [uint64]$frame.commandGeneration -eq 0 -or
        [int]$frame.plannerStateCount -le 0 -or [int]$frame.plannerStateCount -gt $MaximumPlannerStateCount -or
        [int]$frame.commandVariantCount -le 0 -or [int]$frame.commandVariantCount -gt $MaximumCommandVariantCount) {
        $failures.Add("Retained frame $i has invalid or unbounded plan/command generations: planGen=$($frame.resourcePlanGeneration) commandGen=$($frame.commandGeneration) plannerStates=$($frame.plannerStateCount) commandVariants=$($frame.commandVariantCount).")
    }

    $frameTimes.Add([double]$frame.frameTotalMilliseconds)
    $gpuTimes.Add([double]$frame.frameGpuMilliseconds)
    $occlusionTestedTotal += [long]$frame.cpuOcclusionTested
    $occlusionCulledTotal += [long]$frame.cpuOcclusionCulled
    $occlusionSubmittedTotal += [long]$frame.cpuOcclusionQueriesSubmitted
    $occlusionResolvedTotal += [long]$frame.cpuOcclusionQueriesResolved
    $maxOcclusionAge = [Math]::Max($maxOcclusionAge, [int]$frame.cpuOcclusionMaxQueryAge)
}

for ($i = 1; $i -lt $frameLedger.Count; $i++) {
    if ([long]$frameLedger[$i].completedFrameCount -ne [long]$frameLedger[$i - 1].completedFrameCount + 1 -or
        [long]$frameLedger[$i].submittedFrameCount -ne [long]$frameLedger[$i - 1].submittedFrameCount + 1 -or
        [long]$frameLedger[$i].noLayerFrameCount -ne [long]$frameLedger[$i - 1].noLayerFrameCount -or
        [uint64]$frameLedger[$i].renderFrameId -le [uint64]$frameLedger[$i - 1].renderFrameId -or
        [double]$frameLedger[$i].elapsedMilliseconds -le [double]$frameLedger[$i - 1].elapsedMilliseconds) {
        $failures.Add("Retained frame ledger is not a contiguous, monotonically increasing submitted-frame cohort at index $i.")
    }
}

$workloadHashes = @($frameLedger | ForEach-Object { [uint64]$_.outputWorkloadIdentityHash } | Sort-Object -Unique)
$framePlanGenerations = @($frameLedger | ForEach-Object { [uint64]$_.resourcePlanGeneration } | Sort-Object -Unique)
$frameCommandGenerations = @($frameLedger | ForEach-Object { [uint64]$_.commandGeneration } | Sort-Object -Unique)
if ($workloadHashes.Count -ne 1 -or [uint64]$workloadHashes[0] -eq 0 -or
    $framePlanGenerations.Count -ne 1 -or $frameCommandGenerations.Count -ne 1) {
    $failures.Add("The retained cohort changed workload/plan/command identity after warmup: workloads=$($workloadHashes -join ',') planGenerations=$($framePlanGenerations -join ',') commandGenerations=$($frameCommandGenerations -join ',').")
}

$retainedTemporalStateEntries = [System.Collections.Generic.List[object]]::new()
if ([long]$summary.temporalStateLedgerOverflowCount -ne 0) {
    $failures.Add("Temporal-state evidence overflowed its fixed-capacity ring: $($summary.temporalStateLedgerOverflowCount).")
}
foreach ($frame in $frameLedger) {
    $renderFrameId = [uint64]$frame.renderFrameId
    $entries = @($temporalStateLedger | Where-Object {
        [uint64]$_.renderFrameId -eq $renderFrameId -and [uint32]$_.expectedLayerMask -eq 3
    })
    if ($entries.Count -ne 2) {
        $failures.Add("Retained render frame $renderFrameId has $($entries.Count) strict-SPS temporal-state entries; expected exactly two eyes.")
        continue
    }

    $eyeIndices = @($entries | ForEach-Object { [int]$_.eyeIndex } | Sort-Object -Unique)
    if ($eyeIndices.Count -ne 2 -or $eyeIndices[0] -ne 0 -or $eyeIndices[1] -ne 1) {
        $failures.Add("Retained render frame $renderFrameId did not report independent left/right temporal states.")
    }
    foreach ($entry in $entries) {
        $retainedTemporalStateEntries.Add($entry)
        $eyeMask = [uint32](1 -shl [int]$entry.eyeIndex)
        if ([int]$entry.historyIsolationPolicy -ne 4 -or
            ([uint32]$entry.currentMatrixLayerMask -band 3) -ne 3 -or
            ([uint32]$entry.colorHistoryLayerMask -band 3) -ne 3 -or
            ([uint32]$entry.depthHistoryLayerMask -band 3) -ne 3 -or
            ([uint32]$entry.tsrHistoryLayerMask -band 3) -ne 3 -or
            ([uint32]$entry.committedLayerMask -band 3) -ne 3 -or
            -not [bool]$entry.historyReadyAfterCommit -or
            -not [bool]$entry.eyeHistoryReadyAfterCommit -or
            -not [bool]$entry.committedThisFrame -or
            [uint64]$entry.resetGeneration -eq 0 -or
            [uint64]$entry.seededGeneration -ne [uint64]$entry.resetGeneration -or
            [uint64]$entry.previousViewProjectionFingerprint -eq 0 -or
            [uint64]$entry.currentViewProjectionFingerprint -eq 0 -or
            [string]$entry.resetReason -ne "None" -or
            [bool]$entry.cameraCut -or
            (([uint32]$entry.committedLayerMask -band $eyeMask) -eq 0)) {
            $failures.Add("Retained render frame $renderFrameId eye $($entry.eyeIndex) has incomplete, reset, or unseeded temporal state.")
        }
    }

    $left = @($entries | Where-Object { [int]$_.eyeIndex -eq 0 })[0]
    $right = @($entries | Where-Object { [int]$_.eyeIndex -eq 1 })[0]
    if ([uint64]$left.currentViewProjectionFingerprint -eq [uint64]$right.currentViewProjectionFingerprint) {
        $failures.Add("Retained render frame $renderFrameId collapsed left/right current view-projection state.")
    }
    if ([Math]::Abs([double]$left.currentJitterX - [double]$right.currentJitterX) -gt 1.0e-7 -or
        [Math]::Abs([double]$left.currentJitterY - [double]$right.currentJitterY) -gt 1.0e-7) {
        $failures.Add("Retained render frame $renderFrameId used divergent left/right jitter under the shared stereo convention.")
    }
}

if ($retainedTemporalStateEntries.Count -ne ($RetainedFrames * 2)) {
    $failures.Add("Retained strict-SPS temporal-state ledger contains $($retainedTemporalStateEntries.Count) eye entries; expected $($RetainedFrames * 2).")
}
for ($eyeIndex = 0; $eyeIndex -lt 2; $eyeIndex++) {
    $eyeEntries = @($retainedTemporalStateEntries | Where-Object { [int]$_.eyeIndex -eq $eyeIndex })
    $resetGenerations = @($eyeEntries | ForEach-Object { [uint64]$_.resetGeneration } | Sort-Object -Unique)
    $profileGenerations = @($eyeEntries | ForEach-Object { [uint64]$_.profileGeneration } | Sort-Object -Unique)
    if ($resetGenerations.Count -ne 1 -or $profileGenerations.Count -ne 1) {
        $failures.Add("Eye $eyeIndex temporal reset/profile generation changed during ordinary head motion or external image rotation.")
    }
}
$rotatedImageSlots = @(
    @($frameLedger | ForEach-Object { [int]$_.leftExternalImageSlot }) +
    @($frameLedger | ForEach-Object { [int]$_.rightExternalImageSlot }) |
        Sort-Object -Unique
)
if ($rotatedImageSlots.Count -lt 2) {
    $failures.Add("The retained cohort did not exercise OpenXR external-image rotation while temporal generations were observed.")
}

$pipelineOutputs = @($outputLedger | Where-Object { [int]$_.pipelineInstanceId -gt 0 })
$expectSubNativeTsr = $TsrResolutionScale -lt 0.9999
$strictSpsResolutionOutputs = @($pipelineOutputs | Where-Object {
    [string]$_.targetClass -eq "RuntimeExternalImage" -and
    [uint32]$_.layerCount -eq 2 -and [uint32]$_.viewMask -eq 3 -and
    [bool]$_.rendered
})
if ($strictSpsResolutionOutputs.Count -eq 0) {
    $failures.Add("The TSR resolution cohort contained no rendered strict-SPS outputs.")
}
else {
    $invalidTsrResolutionOutputs = @($strictSpsResolutionOutputs | Where-Object {
        $isSubNative = [uint32]$_.internalWidth -lt [uint32]$_.displayWidth -and
            [uint32]$_.internalHeight -lt [uint32]$_.displayHeight
        $isNative = [uint32]$_.internalWidth -eq [uint32]$_.displayWidth -and
            [uint32]$_.internalHeight -eq [uint32]$_.displayHeight
        if ($expectSubNativeTsr) { -not $isSubNative } else { -not $isNative }
    })
    if ($invalidTsrResolutionOutputs.Count -gt 0) {
        $failures.Add("$($invalidTsrResolutionOutputs.Count) strict-SPS outputs did not use the expected $(if ($expectSubNativeTsr) { 'sub-native' } else { 'native' }) TSR resolution shape.")
    }
}
$expectedInternalWidth = if ($expectSubNativeTsr) {
    [Math]::Max(1, [int][Math]::Floor([double]$ExpectedSpsWidth * $TsrResolutionScale))
}
else { $ExpectedSpsWidth }
$expectedInternalHeight = if ($expectSubNativeTsr) {
    [Math]::Max(1, [int][Math]::Floor([double]$ExpectedSpsHeight * $TsrResolutionScale))
}
else { $ExpectedSpsHeight }
$invalidOutputs = @($pipelineOutputs | Where-Object {
    [int]$_.retainedIndex -lt 0 -or [int]$_.retainedIndex -ge $RetainedFrames -or
    [uint64]$_.renderFrameId -eq 0 -or
    [uint64]$_.outputId -eq 0 -or [uint64]$_.viewFamilyId -eq 0 -or
    [string]::IsNullOrWhiteSpace([string]$_.pipelineName) -or
    [int]$_.pipelineInstanceId -le 0 -or
    [uint64]$_.resourcePlanGeneration -eq 0 -or [uint64]$_.commandGeneration -eq 0 -or
    [uint64]$_.targetGeneration -ne [uint64]$_.resourcePlanGeneration -or
    [uint32]$_.displayWidth -eq 0 -or [uint32]$_.displayHeight -eq 0 -or
    [uint32]$_.internalWidth -eq 0 -or [uint32]$_.internalHeight -eq 0 -or
    [uint32]$_.layerCount -eq 0 -or -not [bool]$_.lifetimeValidationPassed
})
if ($invalidOutputs.Count -gt 0) {
    $failures.Add("$($invalidOutputs.Count) pipeline-output ledger entries have invalid identity, generations, lifetime, extent, or layer metadata.")
}
$desktopTsrOutputs = @($pipelineOutputs | Where-Object {
    [string]$_.targetClass -eq "DesktopSwapchain" -and
    [string]$_.antiAliasingMode -eq "Tsr" -and
    [bool]$_.rendered -and [bool]$_.sceneRendered -and
    [bool]$_.renderPhaseSceneRendered -and [bool]$_.finalWriteObserved -and
    [string]$_.workDisposition -eq "FreshRender" -and
    [uint32]$_.contentAgeFrames -eq 0 -and [bool]$_.policyAuthorized
})
$desktopTsrRetainedIndices = @($desktopTsrOutputs |
    ForEach-Object { [int]$_.retainedIndex } | Sort-Object -Unique)
if ($desktopTsrRetainedIndices.Count -ne $RetainedFrames) {
    $failures.Add("Desktop TSR output inventory covered $($desktopTsrRetainedIndices.Count) retained frames; expected $RetainedFrames independently of present scheduling.")
}
for ($i = 0; $i -lt $RetainedFrames; $i++) {
    $frame = $frameLedger[$i]
    $frameOutputs = @($outputLedger | Where-Object { [int]$_.retainedIndex -eq $i })
    $desktopOutputs = @($frameOutputs | Where-Object {
        [string]$_.targetClass -eq "DesktopSwapchain" -and
        [uint64]$_.renderFrameId -eq [uint64]$frame.outputManifestFrameId -and
        [bool]$_.rendered -and [bool]$_.sceneRendered -and [bool]$_.renderPhaseSceneRendered -and
        [bool]$_.finalWriteObserved -and [bool]$_.due -and -not [bool]$_.skipped -and
        [string]$_.workDisposition -eq "FreshRender" -and [uint32]$_.contentAgeFrames -eq 0 -and
        [bool]$_.policyAuthorized
    })
    $runtimeExternalOutputs = @($frameOutputs | Where-Object {
        [string]$_.targetClass -eq "RuntimeExternalImage" -and
        [int]$_.pipelineInstanceId -gt 0 -and
        [uint64]$_.renderFrameId -eq [uint64]$frame.renderFrameId -and
        [bool]$_.rendered -and [bool]$_.renderPhaseSceneRendered
    })
    $strictSpsOutputs = @($frameOutputs | Where-Object {
        [string]$_.targetClass -eq "RuntimeExternalImage" -and
        [int]$_.pipelineInstanceId -gt 0 -and
        [uint64]$_.renderFrameId -eq [uint64]$frame.renderFrameId -and
        [uint32]$_.viewMask -eq 3 -and
        [uint32]$_.layerCount -eq 2 -and [int]$_.externalImageSlot -ge 0 -and
        ([int]$_.externalImageSlot -eq [int]$frame.leftExternalImageSlot -or
         [int]$_.externalImageSlot -eq [int]$frame.rightExternalImageSlot) -and
        [bool]$_.rendered -and [bool]$_.sceneRendered -and [bool]$_.renderPhaseSceneRendered -and
        [string]$_.workDisposition -eq "FreshRender" -and [uint32]$_.contentAgeFrames -eq 0 -and
        [bool]$_.policyAuthorized -and [string]$_.antiAliasingMode -eq "Tsr"
    })
    $leftSubmits = @($frameOutputs | Where-Object {
        [string]$_.outputKind -eq "OpenXREyeSubmit" -and
        [string]$_.viewKind -eq "LeftEye" -and
        [uint64]$_.renderFrameId -eq [uint64]$frame.renderFrameId -and
        [bool]$_.submitObserved
    })
    $rightSubmits = @($frameOutputs | Where-Object {
        [string]$_.outputKind -eq "OpenXREyeSubmit" -and
        [string]$_.viewKind -eq "RightEye" -and
        [uint64]$_.renderFrameId -eq [uint64]$frame.renderFrameId -and
        [bool]$_.submitObserved
    })
    $desktopPresents = @($frameOutputs | Where-Object {
        [string]$_.outputKind -eq "Present" -and [bool]$_.presentObserved -and
        [uint64]$_.renderFrameId -eq [uint64]$frame.outputManifestFrameId -and
        [bool]$_.presentAccepted -and [string]$_.presentResult -eq "0"
    })
    if ($desktopOutputs.Count -eq 0 -or $strictSpsOutputs.Count -eq 0 -or
        $runtimeExternalOutputs.Count -ne $strictSpsOutputs.Count -or
        $leftSubmits.Count -eq 0 -or $rightSubmits.Count -eq 0 -or $desktopPresents.Count -ne 1) {
        $failures.Add("Retained frame $i lacks a fresh desktop final write/present or complete true-multiview OpenXR render+submit ledger.")
    }
}

$unstableOutputGroups = [System.Collections.Generic.List[string]]::new()
$pipelineOutputGroups = @($pipelineOutputs | Group-Object {
    "{0}:{1}:{2}:{3}" -f $_.outputId, $_.pipelineInstanceId, $_.targetClass, $_.stableTargetId
})
foreach ($group in $pipelineOutputGroups) {
    $entries = @($group.Group)
    $pipelineNames = @($entries | ForEach-Object { [string]$_.pipelineName } | Sort-Object -Unique)
    $resourceGenerations = @($entries | ForEach-Object { [uint64]$_.resourcePlanGeneration } | Sort-Object -Unique)
    $commandGenerations = @($entries | ForEach-Object { [uint64]$_.commandGeneration } | Sort-Object -Unique)
    $targetGenerations = @($entries | ForEach-Object { [uint64]$_.targetGeneration } | Sort-Object -Unique)
    $shapes = @($entries | ForEach-Object {
        "{0}x{1}:{2}x{3}:layers={4}:viewMask={5}" -f $_.displayWidth, $_.displayHeight, $_.internalWidth, $_.internalHeight, $_.layerCount, $_.viewMask
    } | Sort-Object -Unique)
    if ($pipelineNames.Count -ne 1 -or $resourceGenerations.Count -ne 1 -or
        $commandGenerations.Count -ne 1 -or $targetGenerations.Count -ne 1 -or $shapes.Count -ne 1) {
        $unstableOutputGroups.Add($group.Name)
    }
}
if ($unstableOutputGroups.Count -gt 0) {
    $failures.Add("$($unstableOutputGroups.Count) physical output identity group(s) changed pipeline/plan/command/target generation or shape after warmup.")
}

if ($planReplacementFrames -ne 0) {
    $failures.Add("Resource-plan replacement occurred in $planReplacementFrames retained frames.")
}
if ($allocationFrames -ne 0) {
    $failures.Add("Vulkan device-local/upload allocation occurred in $allocationFrames retained frames.")
}
if ($descriptorChurnFrames -ne 0) {
    $failures.Add("Descriptor-pool create/destroy/reset churn occurred in $descriptorChurnFrames retained frames.")
}
if ($retirementFrames -ne 0) {
    $failures.Add("Vulkan resource retirement occurred in $retirementFrames retained frames.")
}

$boundedGaugeResults = @(
    Measure-SteadyStateGauge -Frames $frameLedger -PropertyName "lifetimeLiveResourceCount" -WindowFrames $SteadyStateWindowFrames -Maximum $MaximumLiveResourceCount
    Measure-SteadyStateGauge -Frames $frameLedger -PropertyName "trackedDescriptorSetCount" -WindowFrames $SteadyStateWindowFrames -Maximum $MaximumTrackedDescriptorSetCount
    Measure-SteadyStateGauge -Frames $frameLedger -PropertyName "plannerStateCount" -WindowFrames $SteadyStateWindowFrames -Maximum $MaximumPlannerStateCount
    Measure-SteadyStateGauge -Frames $frameLedger -PropertyName "commandVariantCount" -WindowFrames $SteadyStateWindowFrames -Maximum $MaximumCommandVariantCount
)
foreach ($gauge in $boundedGaugeResults) {
    if (-not [bool]$gauge.passed) {
        $failures.Add("Gauge '$($gauge.property)' was unbounded or had positive steady-state drift: min=$($gauge.minimumObserved) max=$($gauge.maximumObserved)/$($gauge.maximumAllowed) firstAvg=$($gauge.firstWindowAverage) lastAvg=$($gauge.lastWindowAverage) firstMax=$($gauge.firstWindowMaximum) lastMax=$($gauge.lastWindowMaximum).")
    }
}
if ($occlusionTestedTotal -le 0 -or $occlusionSubmittedTotal -le 0 -or $occlusionResolvedTotal -le 0) {
    $failures.Add("CpuQueryAsync did not perform valid work: tested=$occlusionTestedTotal submitted=$occlusionSubmittedTotal resolved=$occlusionResolvedTotal.")
}
if ($occlusionCulledTotal -le 0) {
    $failures.Add("CpuQueryAsync produced no occlusion culls in the retained window.")
}

$desktopScopes = @("MonoDesktop", "EditorDesktopWhileVr", "MirrorOnly")
$vrScopes = @("VrSinglePassStereo", "VrFoveatedView", "VrStereoPair", "VrLeftEye", "VrRightEye")
$invalidOcclusionKeys = @($occlusionViewLedger | Where-Object {
    [int]$_.pipelineInstanceId -le 0 -or
    [uint64]$_.outputId -eq 0 -or
    [int]$_.povId -eq 0 -or
    [uint32]$_.coverageMask -eq 0 -or
    [uint32]$_.requiredCoverageMask -eq 0 -or
    [int]$_.declaredViewCount -le 0 -or
    (([uint32]$_.coverageMask -band (-bnot [uint32]$_.requiredCoverageMask)) -ne 0)
})
if ($invalidOcclusionKeys.Count -gt 0) {
    $failures.Add("$($invalidOcclusionKeys.Count) occlusion ledger entries lacked a valid full pipeline/output/POV/coverage identity.")
}
$duplicateOcclusionKeys = @($occlusionViewLedger |
    Group-Object {
        "{0}:{1}:{2}:{3}:{4}:{5}:{6}:{7}:{8}:{9}:{10}" -f
            $_.retainedIndex,
            $_.renderPass,
            $_.scope,
            $_.viewId,
            $_.pipelineInstanceId,
            $_.outputId,
            $_.povId,
            $_.coverageMask,
            $_.requiredCoverageMask,
            $_.declaredViewCount,
            $_.resourceGeneration
    } |
    Where-Object { $_.Count -gt 1 })
if ($duplicateOcclusionKeys.Count -gt 0) {
    $failures.Add("$($duplicateOcclusionKeys.Count) full occlusion keys appeared more than once in a retained frame.")
}
$desktopOcclusion = @($occlusionViewLedger | Where-Object { $desktopScopes -contains [string]$_.scope })
$vrOcclusion = @($occlusionViewLedger | Where-Object { $vrScopes -contains [string]$_.scope })
$desktopPovIds = @($desktopOcclusion | ForEach-Object { [int]$_.povId } | Sort-Object -Unique)
$vrPovIds = @($vrOcclusion | ForEach-Object { [int]$_.povId } | Sort-Object -Unique)
$desktopOutputIds = @($desktopOcclusion | ForEach-Object { [uint64]$_.outputId } | Sort-Object -Unique)
$vrOutputIds = @($vrOcclusion | ForEach-Object { [uint64]$_.outputId } | Sort-Object -Unique)
$desktopViewSubmissions = Get-PropertySum -Items $desktopOcclusion -PropertyName "submissions"
$desktopViewResolutions = Get-PropertySum -Items $desktopOcclusion -PropertyName "resolutions"
$desktopViewCulls = Get-PropertySum -Items $desktopOcclusion -PropertyName "skips"
$desktopRecoveryStarts = Get-PropertySum -Items $desktopOcclusion -PropertyName "recoveryStarts"
$desktopRecoveryCompletions = Get-PropertySum -Items $desktopOcclusion -PropertyName "recoveryCompletions"
$vrViewSubmissions = Get-PropertySum -Items $vrOcclusion -PropertyName "submissions"
$vrViewResolutions = Get-PropertySum -Items $vrOcclusion -PropertyName "resolutions"
$vrViewCulls = Get-PropertySum -Items $vrOcclusion -PropertyName "skips"
$vrRecoveryStarts = Get-PropertySum -Items $vrOcclusion -PropertyName "recoveryStarts"
$vrRecoveryCompletions = Get-PropertySum -Items $vrOcclusion -PropertyName "recoveryCompletions"
if ($desktopOcclusion.Count -eq 0 -or $desktopViewSubmissions -le 0 -or $desktopViewResolutions -le 0 -or $desktopViewCulls -le 0) {
    $failures.Add("Desktop POV occlusion was not independently active: keys=$($desktopOcclusion.Count) submissions=$desktopViewSubmissions resolutions=$desktopViewResolutions culls=$desktopViewCulls.")
}
if ($vrOcclusion.Count -eq 0 -or $vrViewSubmissions -le 0 -or $vrViewResolutions -le 0 -or $vrViewCulls -le 0) {
    $failures.Add("VR POV occlusion was not independently active: keys=$($vrOcclusion.Count) submissions=$vrViewSubmissions resolutions=$vrViewResolutions culls=$vrViewCulls.")
}
foreach ($desktopPovId in $desktopPovIds) {
    if ($vrPovIds -contains $desktopPovId) {
        $failures.Add("Desktop and VR occlusion unexpectedly shared POV identity $desktopPovId.")
    }
}
$expectedVrCoverageMask = if ([int]$summary.locatedViewCount -ge 4) { [uint32]0xF } else { [uint32]0x3 }
$invalidVrCoverage = @($vrOcclusion | Where-Object {
    ([uint32]$_.requiredCoverageMask -band $expectedVrCoverageMask) -ne $expectedVrCoverageMask
})
if ($invalidVrCoverage.Count -gt 0) {
    $failures.Add("$($invalidVrCoverage.Count) VR occlusion ledger entries did not cover the required POV mask 0x$($expectedVrCoverageMask.ToString('X')).")
}
$staleOcclusion = @($occlusionViewLedger | Where-Object {
    [int]$_.currentResultAgeFrames -gt $MaximumOcclusionResultAgeFrames -or
    [int]$_.maxResultAgeFrames -gt $MaximumOcclusionResultAgeFrames -or
    [int]$_.currentRecoveryAgeFrames -gt $MaximumOcclusionRecoveryAgeFrames -or
    [int]$_.maxRecoveryAgeFrames -gt $MaximumOcclusionRecoveryAgeFrames -or
    [int]$_.recoveryLatencyFrames -gt $MaximumOcclusionRecoveryAgeFrames
})
if ($staleOcclusion.Count -gt 0) {
    $failures.Add("$($staleOcclusion.Count) occlusion entries exceeded result/recovery age bounds of $MaximumOcclusionResultAgeFrames/$MaximumOcclusionRecoveryAgeFrames frames.")
}

$visibleOcclusionRoles = @("StableVisibleSentinel", "MovingVisibleSentinel", "TopEdgeVisibleSentinel")
$requiredOcclusionRoles = @("Occluder", "HiddenTarget") + $visibleOcclusionRoles
$invalidNamedOcclusionEvidence = @($occlusionEvidenceLedger | Where-Object {
    [int]$_.retainedIndex -lt 0 -or [int]$_.retainedIndex -ge $RetainedFrames -or
    [uint64]$_.renderFrameId -eq 0 -or [int]$_.pipelineInstanceId -le 0 -or
    [uint64]$_.outputId -eq 0 -or [int]$_.povId -eq 0 -or
    [uint32]$_.stableQueryKey -eq 0 -or
    [string]$_.mode -cne "CpuQueryAsync" -or -not [bool]$_.candidateObserved -or
    -not [bool]$_.hasDecision -or
    ([bool]$_.rendered -eq [bool]$_.culled)
})
if ($invalidNamedOcclusionEvidence.Count -gt 0) {
    $failures.Add("$($invalidNamedOcclusionEvidence.Count) named occlusion evidence entries have invalid identity, mode, candidate, or decision state.")
}
foreach ($role in $requiredOcclusionRoles) {
    if (@($occlusionEvidenceLedger | Where-Object { [string]$_.role -ceq $role }).Count -eq 0) {
        $failures.Add("Named occlusion evidence did not contain role '$role'.")
    }
}
$culledVisibleSentinels = @($occlusionEvidenceLedger | Where-Object {
    $visibleOcclusionRoles -contains [string]$_.role -and [bool]$_.culled
})
if ($culledVisibleSentinels.Count -gt 0) {
    $failures.Add("Known-visible sentinel evidence contains $($culledVisibleSentinels.Count) rejected entries.")
}
$desktopEvidenceScopes = @("MonoDesktop", "EditorDesktopWhileVr", "MirrorOnly")
$spsEvidenceScopes = @("VrSinglePassStereo", "VrFoveatedView")
$desktopHiddenCulls = @($occlusionEvidenceLedger | Where-Object {
    [string]$_.role -ceq "HiddenTarget" -and
    $desktopEvidenceScopes -contains [string]$_.scope -and
    [bool]$_.culled -and
    (([uint32]$_.occlusionProofCoverageMask -band 0x1) -eq 0x1)
})
$spsHiddenCulls = @($occlusionEvidenceLedger | Where-Object {
    [string]$_.role -ceq "HiddenTarget" -and
    $spsEvidenceScopes -contains [string]$_.scope -and
    [bool]$_.culled -and
    (([uint32]$_.occlusionProofCoverageMask -band 0x3) -eq 0x3)
})
if ($desktopHiddenCulls.Count -eq 0 -or $spsHiddenCulls.Count -eq 0) {
    $failures.Add("Named occlusion evidence did not prove independent nonzero desktop/SPS hidden-target culls with masks 0x1/0x3.")
}

$occlusionOffSummary = $null
$occlusionOffEvidence = @()
if ([string]::IsNullOrWhiteSpace($OcclusionOffSummaryPath)) {
    $failures.Add("OcclusionOffSummaryPath is required for enabled/off rendered-set parity.")
}
else {
    if (-not [System.IO.Path]::IsPathRooted($OcclusionOffSummaryPath)) {
        $OcclusionOffSummaryPath = Join-Path $repoRoot $OcclusionOffSummaryPath
    }
    $OcclusionOffSummaryPath = [System.IO.Path]::GetFullPath($OcclusionOffSummaryPath)
    if (-not (Test-Path -LiteralPath $OcclusionOffSummaryPath -PathType Leaf)) {
        $failures.Add("Occlusion-off smoke summary was not found: $OcclusionOffSummaryPath")
    }
    else {
        $occlusionOffSummary = Get-Content -LiteralPath $OcclusionOffSummaryPath -Raw | ConvertFrom-Json
        $occlusionOffEvidence = @(Get-JsonArray $occlusionOffSummary.occlusionEvidenceLedger)
        if ([string]$occlusionOffSummary.occlusionCullingModeEffective -cne "Disabled" -or
            [int]$occlusionOffSummary.schemaVersion -lt 8 -or
            [int]$occlusionOffSummary.retainedFrameCount -ne $RetainedFrames -or
            [int]$occlusionOffSummary.warmupFrameCount -ne $WarmupFrames -or
            [long]$occlusionOffSummary.occlusionEvidenceOverflowCount -ne 0 -or
            @(Get-JsonArray $occlusionOffSummary.failures).Count -ne 0 -or
            [string]$occlusionOffSummary.rendererBackend -cne [string]$summary.rendererBackend -or
            [string]$occlusionOffSummary.viewRenderModeRequested -cne [string]$summary.viewRenderModeRequested -or
            [string]$occlusionOffSummary.viewRenderModeEffective -cne [string]$summary.viewRenderModeEffective -or
            [string]$occlusionOffSummary.viewRenderImplementationPath -cne [string]$summary.viewRenderImplementationPath -or
            [string]$occlusionOffSummary.vulkanRenderTargetModeEffective -cne [string]$summary.vulkanRenderTargetModeEffective -or
            [string]$occlusionOffSummary.vulkanDiagnosticPresetEffective -cne [string]$summary.vulkanDiagnosticPresetEffective -or
            [bool]$occlusionOffSummary.vulkanSynchronizationValidationEffective -ne [bool]$summary.vulkanSynchronizationValidationEffective -or
            [string]$occlusionOffSummary.antiAliasingModeEffective -cne [string]$summary.antiAliasingModeEffective -or
            [string]$occlusionOffSummary.mirrorModeEffective -cne [string]$summary.mirrorModeEffective -or
            [string]$occlusionOffSummary.foveationEffectiveMode -cne [string]$summary.foveationEffectiveMode -or
            [int]$occlusionOffSummary.defaultPipelineCaptureSkipFrames -ne $CaptureSkipFrames -or
            [Math]::Abs([double]$occlusionOffSummary.tsrResolutionScaleEffective - $TsrResolutionScale) -gt 0.0001 -or
            -not (Test-ExactStringArray $occlusionOffSummary.externallyOwnedValidationAllowlist $ExternallyOwnedValidationAllowlist)) {
            $failures.Add("Occlusion-off reference summary is not an exact, overflow-free $RetainedFrames-frame Disabled cohort.")
        }
        $invalidOffEvidence = @($occlusionOffEvidence | Where-Object {
            [string]$_.mode -cne "Disabled" -or -not [bool]$_.candidateObserved -or
            -not [bool]$_.rendered -or [bool]$_.culled
        })
        if ($invalidOffEvidence.Count -gt 0) {
            $failures.Add("Occlusion-off reference contains $($invalidOffEvidence.Count) entries that were not rendered ground truth.")
        }

        function Get-OcclusionCompatibilityKey {
            param([Parameter(Mandatory)][object]$Entry)
            $scopeFamily = if ($desktopEvidenceScopes -contains [string]$Entry.scope) {
                "Desktop"
            }
            elseif ($spsEvidenceScopes -contains [string]$Entry.scope) {
                "Sps"
            }
            else {
                [string]$Entry.scope
            }
            return "{0}:{1}:{2}:{3}:{4}:{5}" -f [int]$Entry.retainedIndex, [string]$Entry.role, [uint32]$Entry.stableQueryKey, $scopeFamily, [int]$Entry.renderPass, [uint32]$Entry.requiredCoverageMask
        }

        $offRenderedKeys = @{}
        foreach ($entry in $occlusionOffEvidence) {
            if ([bool]$entry.rendered) {
                $offRenderedKeys[(Get-OcclusionCompatibilityKey $entry)] = $true
            }
        }
        $missingOffGroundTruth = [System.Collections.Generic.List[string]]::new()
        foreach ($entry in $occlusionEvidenceLedger) {
            $key = Get-OcclusionCompatibilityKey $entry
            if ([bool]$entry.candidateObserved -and -not $offRenderedKeys.ContainsKey($key)) {
                $missingOffGroundTruth.Add($key)
            }
            if ([bool]$entry.culled) {
                $requiredCoverageMask = [uint32]$entry.requiredCoverageMask
                if ($requiredCoverageMask -eq 0 -or
                    (([uint32]$entry.occlusionProofCoverageMask -band $requiredCoverageMask) -ne $requiredCoverageMask) -or
                    [int]$entry.pipelineInstanceId -le 0 -or [uint64]$entry.outputId -eq 0) {
                    $failures.Add("Enabled occlusion removal lacks full owning-view proof for key '$key'.")
                }
            }
            if ([string]$entry.role -in @("StableVisibleSentinel", "MovingVisibleSentinel", "TopEdgeVisibleSentinel") -and
                (-not [bool]$entry.rendered -or [bool]$entry.culled)) {
                $failures.Add("Known-visible sentinel was rejected for key '$key'.")
            }
        }
        if ($missingOffGroundTruth.Count -gt 0) {
            $uniqueMissing = @($missingOffGroundTruth | Sort-Object -Unique)
            $failures.Add("Enabled occlusion evidence has $($uniqueMissing.Count) per-frame candidate keys absent from the off rendered set.")
        }
    }
}

$captureEvidence = [System.Collections.Generic.List[object]]::new()
$retainedCohortStart = if ($null -ne $summary.retainedCohortStartedAtUtc) {
    [DateTimeOffset]$summary.retainedCohortStartedAtUtc
}
else { [DateTimeOffset]::MinValue }
foreach ($stage in $requiredCaptureStages) {
    $stageMipLevel = if ($captureStageMipLevels.ContainsKey($stage)) {
        [int]$captureStageMipLevels[$stage]
    }
    else { 0 }
    $isDisplayResolutionStage = $stage -in @(
        "13b_PreTsrHistoryColor",
        "13c_MonoTsrReference",
        "14_TsrOutput",
        "14b_TsrHistoryColor")
    $stageBaseWidth = if ($isDisplayResolutionStage) { $ExpectedSpsWidth } else { $expectedInternalWidth }
    $stageBaseHeight = if ($isDisplayResolutionStage) { $ExpectedSpsHeight } else { $expectedInternalHeight }
    $expectedStageWidth = [Math]::Max(1, $stageBaseWidth -shr $stageMipLevel)
    $expectedStageHeight = [Math]::Max(1, $stageBaseHeight -shr $stageMipLevel)
    for ($layerIndex = 0; $layerIndex -lt 2; $layerIndex++) {
        $expectedPath = [System.IO.Path]::GetFullPath(
            (Join-Path $captureDirectory "DefaultPipelineSps_${stage}_layer${layerIndex}.png"))
        $ledgerEntries = @($captureLedger | Where-Object {
            [string]$_.pipelineName -ceq "DefaultPipelineSps" -and
            [string]$_.outputRole -ceq "StrictSinglePassStereo" -and
            [string]$_.stage -ceq $stage -and
            [int]$_.layerIndex -eq $layerIndex -and
            [int]$_.expectedLayerCount -eq 2 -and
            [uint32]$_.viewMask -eq 3 -and
            [string]$_.antiAliasingMode -ceq "Tsr"
        })
        if ($ledgerEntries.Count -ne 1) {
            $failures.Add("Capture ledger requires exactly one '$stage' layer $layerIndex entry; observed $($ledgerEntries.Count).")
            continue
        }

        $entryPath = [System.IO.Path]::GetFullPath([string]$ledgerEntries[0].path)
        if (-not [string]::Equals($entryPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add("Capture ledger path mismatch for '$stage' layer ${layerIndex}: $entryPath")
            continue
        }
        if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
            $failures.Add("Required captured stage is absent: $expectedPath")
            continue
        }

        $file = Get-Item -LiteralPath $expectedPath
        if ($retainedCohortStart -ne [DateTimeOffset]::MinValue -and
            [DateTimeOffset]$file.LastWriteTimeUtc -ge $retainedCohortStart) {
            $failures.Add("Captured stage '$stage' layer $layerIndex was written after the retained cohort began.")
        }
        $header = [byte[]]::new(24)
        $stream = [System.IO.File]::OpenRead($expectedPath)
        try {
            $headerLength = $stream.Read($header, 0, $header.Length)
        }
        finally {
            $stream.Dispose()
        }
        $pngSignature = if ($headerLength -ge 8) {
            ConvertTo-HexString $header 0 8
        }
        else {
            ""
        }
        $width = if ($headerLength -ge 24) {
            ([uint32]$header[16] -shl 24) -bor ([uint32]$header[17] -shl 16) -bor ([uint32]$header[18] -shl 8) -bor [uint32]$header[19]
        }
        else { 0 }
        $height = if ($headerLength -ge 24) {
            ([uint32]$header[20] -shl 24) -bor ([uint32]$header[21] -shl 16) -bor ([uint32]$header[22] -shl 8) -bor [uint32]$header[23]
        }
        else { 0 }
        if ($file.Length -le 24 -or $pngSignature -cne "89504E470D0A1A0A" -or
            $width -ne $expectedStageWidth -or $height -ne $expectedStageHeight -or
            [int]$ledgerEntries[0].width -ne $expectedStageWidth -or
            [int]$ledgerEntries[0].height -ne $expectedStageHeight -or
            [long]$ledgerEntries[0].lengthBytes -ne $file.Length) {
            $failures.Add("Captured stage '$stage' layer $layerIndex is empty, malformed, or differs from its ledger metadata.")
            continue
        }
        if ($stage -like "*Velocity*") {
            if ([float]$ledgerEntries[0].velocityMaxMagnitude -lt 0.001 -or
                [int]$ledgerEntries[0].velocityNonZeroSampleCount -le 0) {
                $failures.Add("Captured motion stage '$stage' layer $layerIndex did not contain scripted velocity.")
            }
        }
        elseif ([int]$ledgerEntries[0].nonBlackPixelCount -le 0 -or
            [double]$ledgerEntries[0].maximumLuminance -le 0.0) {
            $failures.Add("Captured stage '$stage' layer $layerIndex did not contain known-nonblack output.")
        }
        if ($stage -like "*BloomMip*") {
            if ([double]$ledgerEntries[0].luminanceEnergy -le 0.0 -or
                [float]$ledgerEntries[0].bloomCentroidX -lt 0.0 -or [float]$ledgerEntries[0].bloomCentroidX -gt 1.0 -or
                [float]$ledgerEntries[0].bloomCentroidY -lt 0.0 -or [float]$ledgerEntries[0].bloomCentroidY -gt 1.0) {
                $failures.Add("Captured bloom stage '$stage' layer $layerIndex has invalid energy or centroid.")
            }
        }
        if ($stage -in @("13_FinalPostProcessOutput", "14_TsrOutput", "14b_TsrHistoryColor") -and
            ([float]$ledgerEntries[0].edgeMaxGradient -le 0.0 -or
             [int]$ledgerEntries[0].topBandNonBlackPixelCount -le 0 -or
             [double]$ledgerEntries[0].topBandMaximumLuminance -le 0.0 -or
             [int]$ledgerEntries[0].topBandMagentaPixelCount -le 0)) {
            $failures.Add("Captured final/TSR stage '$stage' layer $layerIndex lacks sharp edges or the top-band visible sentinel.")
        }

        $captureEvidence.Add([pscustomobject]@{
            outputRole = "StrictSinglePassStereo"
            stage = $stage
            layerIndex = $layerIndex
            path = $expectedPath
            lengthBytes = $file.Length
            width = $width
            height = $height
            nonBlackPixelRatio = [double]$ledgerEntries[0].nonBlackPixelRatio
            maximumLuminance = [double]$ledgerEntries[0].maximumLuminance
            luminanceEnergy = [double]$ledgerEntries[0].luminanceEnergy
            bloomCentroidX = [float]$ledgerEntries[0].bloomCentroidX
            bloomCentroidY = [float]$ledgerEntries[0].bloomCentroidY
            velocityMaxMagnitude = [float]$ledgerEntries[0].velocityMaxMagnitude
            velocityNonZeroSampleCount = [int]$ledgerEntries[0].velocityNonZeroSampleCount
            edgeMaxGradient = [float]$ledgerEntries[0].edgeMaxGradient
            topBandNonBlackPixelCount = [int]$ledgerEntries[0].topBandNonBlackPixelCount
            topBandMagentaPixelCount = [int]$ledgerEntries[0].topBandMagentaPixelCount
            sha256 = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash
            lastWriteTimeUtc = $file.LastWriteTimeUtc
        })
    }
}

$desktopExpectedPath = [System.IO.Path]::GetFullPath(
    (Join-Path $captureDirectory "DefaultPipelineDesktop_${desktopFinalCaptureStage}_layer0.png"))
$desktopLedgerEntries = @($captureLedger | Where-Object {
    [string]$_.pipelineName -ceq "DefaultPipelineDesktop" -and
    [string]$_.outputRole -ceq "DesktopFullIndependent" -and
    [string]$_.stage -ceq $desktopFinalCaptureStage -and
    [int]$_.layerIndex -eq 0 -and
    [int]$_.expectedLayerCount -eq 1 -and
    [uint32]$_.viewMask -eq 0 -and
    [string]$_.antiAliasingMode -ceq "Tsr" -and [string]$_.viewKind -ceq "Motion0"
})
$desktopCaptureShapes = @($outputLedger | Where-Object {
    [string]$_.targetClass -ceq "DesktopSwapchain" -and
    [string]$_.antiAliasingMode -ceq "Tsr" -and [bool]$_.rendered
} | ForEach-Object { "{0}x{1}" -f [uint32]$_.displayWidth, [uint32]$_.displayHeight } | Sort-Object -Unique)
$desktopExpectedWidth = 0
$desktopExpectedHeight = 0
if ($desktopCaptureShapes.Count -ne 1 -or $desktopCaptureShapes[0] -notmatch '^(\d+)x(\d+)$') {
    $failures.Add("Desktop capture did not have one stable display extent in the output ledger: $($desktopCaptureShapes -join ',').")
}
else {
    $desktopExpectedWidth = [int]$Matches[1]
    $desktopExpectedHeight = [int]$Matches[2]
}
if ($desktopLedgerEntries.Count -ne 1) {
    $failures.Add("Capture ledger requires exactly one desktop final-output entry; observed $($desktopLedgerEntries.Count).")
}
elseif (-not [string]::Equals(
    [System.IO.Path]::GetFullPath([string]$desktopLedgerEntries[0].path),
    $desktopExpectedPath,
    [StringComparison]::OrdinalIgnoreCase)) {
    $failures.Add("Desktop final-output capture ledger path mismatch: $($desktopLedgerEntries[0].path)")
}
elseif (-not (Test-Path -LiteralPath $desktopExpectedPath -PathType Leaf)) {
    $failures.Add("Required desktop final-output capture is absent: $desktopExpectedPath")
}
else {
    $desktopFile = Get-Item -LiteralPath $desktopExpectedPath
    if ($retainedCohortStart -ne [DateTimeOffset]::MinValue -and
        [DateTimeOffset]$desktopFile.LastWriteTimeUtc -ge $retainedCohortStart) {
        $failures.Add("Desktop motion capture 0 was written after the retained cohort began.")
    }
    $desktopHeader = [byte[]]::new(24)
    $desktopStream = [System.IO.File]::OpenRead($desktopExpectedPath)
    try {
        $desktopHeaderLength = $desktopStream.Read($desktopHeader, 0, $desktopHeader.Length)
    }
    finally {
        $desktopStream.Dispose()
    }
    $desktopPngSignature = if ($desktopHeaderLength -ge 8) {
        ConvertTo-HexString $desktopHeader 0 8
    }
    else { "" }
    $desktopWidth = if ($desktopHeaderLength -ge 24) {
        ([uint32]$desktopHeader[16] -shl 24) -bor ([uint32]$desktopHeader[17] -shl 16) -bor ([uint32]$desktopHeader[18] -shl 8) -bor [uint32]$desktopHeader[19]
    }
    else { 0 }
    $desktopHeight = if ($desktopHeaderLength -ge 24) {
        ([uint32]$desktopHeader[20] -shl 24) -bor ([uint32]$desktopHeader[21] -shl 16) -bor ([uint32]$desktopHeader[22] -shl 8) -bor [uint32]$desktopHeader[23]
    }
    else { 0 }
    if ($desktopFile.Length -le 24 -or $desktopPngSignature -cne "89504E470D0A1A0A" -or
        $desktopWidth -ne $desktopExpectedWidth -or $desktopHeight -ne $desktopExpectedHeight -or
        [int]$desktopLedgerEntries[0].width -ne $desktopExpectedWidth -or
        [int]$desktopLedgerEntries[0].height -ne $desktopExpectedHeight -or
        [long]$desktopLedgerEntries[0].lengthBytes -ne $desktopFile.Length) {
        $failures.Add("Desktop final-output capture is empty, malformed, or differs from its ledger metadata.")
    }
    else {
        if ([int]$desktopLedgerEntries[0].nonBlackPixelCount -le 0 -or
            [double]$desktopLedgerEntries[0].maximumLuminance -le 0.0 -or
            [float]$desktopLedgerEntries[0].edgeMaxGradient -le 0.0 -or
            [int]$desktopLedgerEntries[0].topBandNonBlackPixelCount -le 0 -or
            [int]$desktopLedgerEntries[0].topBandMagentaPixelCount -le 0) {
            $failures.Add("Desktop final-output capture lacks nonblack, sharp, or top-band sentinel pixels.")
        }
        $captureEvidence.Add([pscustomobject]@{
            outputRole = "DesktopFullIndependent"
            stage = $desktopFinalCaptureStage
            layerIndex = 0
            path = $desktopExpectedPath
            lengthBytes = $desktopFile.Length
            width = $desktopWidth
            height = $desktopHeight
            sha256 = (Get-FileHash -LiteralPath $desktopExpectedPath -Algorithm SHA256).Hash
            lastWriteTimeUtc = $desktopFile.LastWriteTimeUtc
        })
    }
}

$desktopMotionHashes = [System.Collections.Generic.List[string]]::new()
if (Test-Path -LiteralPath $desktopExpectedPath -PathType Leaf) {
    $desktopMotionHashes.Add((Get-FileHash -LiteralPath $desktopExpectedPath -Algorithm SHA256).Hash)
}
for ($motionIndex = 1; $motionIndex -lt $captureMotionSampleCount; $motionIndex++) {
    $motionPath = [System.IO.Path]::GetFullPath(
        (Join-Path $captureDirectory "DefaultPipelineDesktop_${desktopFinalCaptureStage}_motion${motionIndex}_layer0.png"))
    $motionEntries = @($captureLedger | Where-Object {
        [string]$_.pipelineName -ceq "DefaultPipelineDesktop" -and
        [string]$_.outputRole -ceq "DesktopMotionSequence" -and
        [string]$_.stage -ceq $desktopFinalCaptureStage -and
        [string]$_.viewKind -ceq "Motion$motionIndex"
    })
    if ($motionEntries.Count -ne 1 -or -not (Test-Path -LiteralPath $motionPath -PathType Leaf)) {
        $failures.Add("Desktop motion sequence is missing exact sample $motionIndex.")
        continue
    }
    $motionFile = Get-Item -LiteralPath $motionPath
    if ([int]$motionEntries[0].width -ne $desktopExpectedWidth -or
        [int]$motionEntries[0].height -ne $desktopExpectedHeight -or
        [int]$motionEntries[0].nonBlackPixelCount -le 0 -or
        [float]$motionEntries[0].edgeMaxGradient -le 0.0 -or
        [int]$motionEntries[0].topBandNonBlackPixelCount -le 0 -or
        ($retainedCohortStart -ne [DateTimeOffset]::MinValue -and
         [DateTimeOffset]$motionFile.LastWriteTimeUtc -ge $retainedCohortStart)) {
        $failures.Add("Desktop motion sample $motionIndex has invalid dimensions/pixels or was captured inside the retained cohort.")
    }
    $motionHash = (Get-FileHash -LiteralPath $motionPath -Algorithm SHA256).Hash
    $desktopMotionHashes.Add($motionHash)
    $captureEvidence.Add([pscustomobject]@{
        outputRole = "DesktopMotionSequence"
        stage = $desktopFinalCaptureStage
        viewKind = "Motion$motionIndex"
        path = $motionPath
        lengthBytes = $motionFile.Length
        width = [int]$motionEntries[0].width
        height = [int]$motionEntries[0].height
        nonBlackPixelRatio = [double]$motionEntries[0].nonBlackPixelRatio
        edgeMaxGradient = [float]$motionEntries[0].edgeMaxGradient
        sha256 = $motionHash
        lastWriteTimeUtc = $motionFile.LastWriteTimeUtc
    })
}
if (@($desktopMotionHashes | Sort-Object -Unique).Count -ne $captureMotionSampleCount) {
    $failures.Add("Desktop scripted-motion captures did not visibly change across all $captureMotionSampleCount samples; stale presentation cannot be excluded.")
}
$desktopTopMarkerEntries = @($captureLedger | Where-Object {
    [string]$_.pipelineName -ceq "DefaultPipelineDesktop" -and
    [string]$_.stage -ceq $desktopFinalCaptureStage -and
    [int]$_.topBandMagentaPixelCount -gt 0
})
if ($desktopTopMarkerEntries.Count -eq 0) {
    $failures.Add("Desktop scripted-motion capture sequence never rendered the top-band magenta visibility marker.")
}

$boundaryCaptureHashes = @{}
$maxBloomRelativeEnergyDelta = 0.10
$maxBloomCentroidDistance = 0.075
$maxBoundaryFingerprintRmse = 0.005
for ($motionIndex = 0; $motionIndex -lt $captureMotionSampleCount; $motionIndex++) {
    foreach ($viewKind in @("LeftEye", "RightEye")) {
        foreach ($boundary in @(
            [pscustomobject]@{ stage = "PublishStaging"; role = "StrictSpsPublishStaging"; layers = 2; mask = 3 },
            [pscustomobject]@{ stage = "AcquiredImage"; role = "OpenXrAcquiredImage"; layers = 1; mask = 0 }
        )) {
            $boundaryPath = [System.IO.Path]::GetFullPath(
                (Join-Path $captureDirectory "OpenXrSps_$($boundary.stage)_motion${motionIndex}_${viewKind}.png"))
            $boundaryEntries = @($captureLedger | Where-Object {
                [string]$_.pipelineName -ceq "OpenXrSps" -and
                [string]$_.outputRole -ceq [string]$boundary.role -and
                [string]$_.stage -ceq [string]$boundary.stage -and
                [string]$_.viewKind -ceq $viewKind -and
                [int]$_.expectedLayerCount -eq [int]$boundary.layers -and
                [uint32]$_.viewMask -eq [uint32]$boundary.mask -and
                [string]$_.path -like "*motion${motionIndex}_${viewKind}.png"
            })
            if ($boundaryEntries.Count -ne 1 -or -not (Test-Path -LiteralPath $boundaryPath -PathType Leaf)) {
                $failures.Add("Missing exact $($boundary.stage) motion=$motionIndex view=$viewKind boundary capture.")
                continue
            }

            $boundaryEntry = $boundaryEntries[0]
            $boundaryFile = Get-Item -LiteralPath $boundaryPath
            $png = Get-PngHeaderInfo -Path $boundaryPath
            if ($png.signature -cne "89504E470D0A1A0A" -or
                [int]$png.width -ne $ExpectedSpsWidth -or [int]$png.height -ne $ExpectedSpsHeight -or
                [int]$boundaryEntry.width -ne $ExpectedSpsWidth -or [int]$boundaryEntry.height -ne $ExpectedSpsHeight -or
                [uint64]$boundaryEntry.renderFrameId -eq 0 -or [int]$boundaryEntry.externalImageSlot -lt 0 -or
                [long]$boundaryEntry.lengthBytes -ne $boundaryFile.Length -or
                [int]$boundaryEntry.nonBlackPixelCount -le 0 -or [double]$boundaryEntry.maximumLuminance -le 0.0 -or
                [float]$boundaryEntry.edgeMaxGradient -le 0.0 -or
                [int]$boundaryEntry.topBandNonBlackPixelCount -le 0 -or
                ($retainedCohortStart -ne [DateTimeOffset]::MinValue -and
                 [DateTimeOffset]$boundaryFile.LastWriteTimeUtc -ge $retainedCohortStart)) {
                $failures.Add("Boundary capture $($boundary.stage) motion=$motionIndex view=$viewKind has invalid attribution, 896x1007 shape, pixel evidence, or timing.")
            }

            $hash = (Get-FileHash -LiteralPath $boundaryPath -Algorithm SHA256).Hash
            $boundaryCaptureHashes["$($boundary.stage):${viewKind}:${motionIndex}"] = $hash
            $captureEvidence.Add([pscustomobject]@{
                outputRole = [string]$boundary.role
                stage = [string]$boundary.stage
                viewKind = $viewKind
                motionIndex = $motionIndex
                renderFrameId = [uint64]$boundaryEntry.renderFrameId
                externalImageSlot = [int]$boundaryEntry.externalImageSlot
                path = $boundaryPath
                lengthBytes = $boundaryFile.Length
                width = [int]$png.width
                height = [int]$png.height
                nonBlackPixelRatio = [double]$boundaryEntry.nonBlackPixelRatio
                luminanceEnergy = [double]$boundaryEntry.luminanceEnergy
                bloomCentroidX = [float]$boundaryEntry.bloomCentroidX
                bloomCentroidY = [float]$boundaryEntry.bloomCentroidY
                edgeMaxGradient = [float]$boundaryEntry.edgeMaxGradient
                sha256 = $hash
                lastWriteTimeUtc = $boundaryFile.LastWriteTimeUtc
            })
        }

        $staging = @($captureLedger | Where-Object {
            [string]$_.stage -ceq "PublishStaging" -and [string]$_.viewKind -ceq $viewKind -and
            [string]$_.path -like "*motion${motionIndex}_${viewKind}.png"
        })
        $acquired = @($captureLedger | Where-Object {
            [string]$_.stage -ceq "AcquiredImage" -and [string]$_.viewKind -ceq $viewKind -and
            [string]$_.path -like "*motion${motionIndex}_${viewKind}.png"
        })
        if ($staging.Count -eq 1 -and $acquired.Count -eq 1) {
            $energyDenominator = [Math]::Max(
                [Math]::Max([Math]::Abs([double]$staging[0].luminanceEnergy), [Math]::Abs([double]$acquired[0].luminanceEnergy)),
                [double]::Epsilon)
            $energyDelta = [Math]::Abs([double]$staging[0].luminanceEnergy - [double]$acquired[0].luminanceEnergy) / $energyDenominator
            $centroidXDelta = [double]$staging[0].bloomCentroidX - [double]$acquired[0].bloomCentroidX
            $centroidYDelta = [double]$staging[0].bloomCentroidY - [double]$acquired[0].bloomCentroidY
            $centroidDistance = [Math]::Sqrt(($centroidXDelta * $centroidXDelta) + ($centroidYDelta * $centroidYDelta))
            $fingerprintRmse = Get-FingerprintRmse $staging[0].luminanceFingerprint $acquired[0].luminanceFingerprint
            if ([uint64]$staging[0].renderFrameId -ne [uint64]$acquired[0].renderFrameId -or
                [int]$staging[0].externalImageSlot -ne [int]$acquired[0].externalImageSlot -or
                $energyDelta -gt $maxBloomRelativeEnergyDelta -or
                $centroidDistance -gt $maxBloomCentroidDistance -or
                $fingerprintRmse -gt $maxBoundaryFingerprintRmse) {
                $failures.Add("Publish/acquired attribution or pixels diverged for motion=$motionIndex view=${viewKind}: stagingFrame=$($staging[0].renderFrameId) acquiredFrame=$($acquired[0].renderFrameId) stagingSlot=$($staging[0].externalImageSlot) acquiredSlot=$($acquired[0].externalImageSlot) energyDelta=$energyDelta centroidDistance=$centroidDistance fingerprintRmse=$fingerprintRmse.")
            }
        }
    }

    foreach ($stage in @("PublishStaging", "AcquiredImage")) {
        $leftHash = $boundaryCaptureHashes["${stage}:LeftEye:${motionIndex}"]
        $rightHash = $boundaryCaptureHashes["${stage}:RightEye:${motionIndex}"]
        if ([string]::IsNullOrWhiteSpace($leftHash) -or [string]::IsNullOrWhiteSpace($rightHash) -or $leftHash -ceq $rightHash) {
            $failures.Add("$stage motion=$motionIndex did not prove independent left/right pixels.")
        }
    }
}
foreach ($stage in @("PublishStaging", "AcquiredImage")) {
    foreach ($viewKind in @("LeftEye", "RightEye")) {
        $motionHashes = @()
        for ($motionIndex = 0; $motionIndex -lt $captureMotionSampleCount; $motionIndex++) {
            $motionHashes += $boundaryCaptureHashes["${stage}:${viewKind}:${motionIndex}"]
        }
        $motionHashes = @($motionHashes | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
        if ($motionHashes.Count -ne $captureMotionSampleCount) {
            $failures.Add("$stage $viewKind did not visibly change across the scripted motion sequence.")
        }
        $topMarkerEntries = @($captureLedger | Where-Object {
            [string]$_.pipelineName -ceq "OpenXrSps" -and
            [string]$_.stage -ceq $stage -and
            [string]$_.viewKind -ceq $viewKind -and
            [int]$_.topBandMagentaPixelCount -gt 0
        })
        if ($topMarkerEntries.Count -eq 0) {
            $failures.Add("$stage $viewKind never rendered the top-band magenta visibility marker during scripted motion.")
        }
    }
}

$maxFinalImageParityRmse = 0.01
$finalImageParityResults = [System.Collections.Generic.List[object]]::new()
if ($null -ne $occlusionOffSummary) {
    $offCaptureLedger = @(Get-JsonArray $occlusionOffSummary.captureLedger)
    for ($motionIndex = 0; $motionIndex -lt $captureMotionSampleCount; $motionIndex++) {
        $viewKind = "Motion$motionIndex"
        $enabledDesktop = @($captureLedger | Where-Object {
            [string]$_.pipelineName -ceq "DefaultPipelineDesktop" -and [string]$_.viewKind -ceq $viewKind
        })
        $disabledDesktop = @($offCaptureLedger | Where-Object {
            [string]$_.pipelineName -ceq "DefaultPipelineDesktop" -and [string]$_.viewKind -ceq $viewKind
        })
        if ($enabledDesktop.Count -ne 1 -or $disabledDesktop.Count -ne 1) {
            $failures.Add("Occlusion on/off desktop final-image parity is missing motion sample $motionIndex.")
        }
        else {
            $rmse = Get-FingerprintRmse $enabledDesktop[0].luminanceFingerprint $disabledDesktop[0].luminanceFingerprint
            $passed = (Test-FiniteDouble $rmse) -and $rmse -le $maxFinalImageParityRmse
            if (-not $passed) {
                $failures.Add("Occlusion on/off desktop motion $motionIndex fingerprint RMSE=$rmse exceeds $maxFinalImageParityRmse.")
            }
            $finalImageParityResults.Add([pscustomobject]@{
                output = "Desktop"
                motionIndex = $motionIndex
                rmse = $rmse
                maximum = $maxFinalImageParityRmse
                passed = $passed
            })
        }

        foreach ($eye in @("LeftEye", "RightEye")) {
            $enabledAcquired = @($captureLedger | Where-Object {
                [string]$_.stage -ceq "AcquiredImage" -and [string]$_.viewKind -ceq $eye -and
                [string]$_.path -like "*motion${motionIndex}_${eye}.png"
            })
            $disabledAcquired = @($offCaptureLedger | Where-Object {
                [string]$_.stage -ceq "AcquiredImage" -and [string]$_.viewKind -ceq $eye -and
                [string]$_.path -like "*motion${motionIndex}_${eye}.png"
            })
            if ($enabledAcquired.Count -ne 1 -or $disabledAcquired.Count -ne 1) {
                $failures.Add("Occlusion on/off acquired-image parity is missing motion=$motionIndex eye=$eye.")
                continue
            }
            $rmse = Get-FingerprintRmse $enabledAcquired[0].luminanceFingerprint $disabledAcquired[0].luminanceFingerprint
            $passed = (Test-FiniteDouble $rmse) -and $rmse -le $maxFinalImageParityRmse
            if (-not $passed) {
                $failures.Add("Occlusion on/off acquired motion=$motionIndex eye=$eye fingerprint RMSE=$rmse exceeds $maxFinalImageParityRmse.")
            }
            $finalImageParityResults.Add([pscustomobject]@{
                output = "AcquiredImage.$eye"
                motionIndex = $motionIndex
                rmse = $rmse
                maximum = $maxFinalImageParityRmse
                passed = $passed
            })
        }
    }
}

$expectedBoundaryCaptureCount = $captureMotionSampleCount * 4
$expectedCaptureEntryCount = ($requiredCaptureStages.Count * 2) + $captureMotionSampleCount + $expectedBoundaryCaptureCount
$invalidCaptureFingerprints = @($captureLedger | Where-Object {
    $fingerprint = @(Get-JsonArray $_.luminanceFingerprint)
    [int]$_.luminanceFingerprintWidth -ne 16 -or
    [int]$_.luminanceFingerprintHeight -ne 16 -or
    $fingerprint.Count -ne 256
})
if ($invalidCaptureFingerprints.Count -gt 0) {
    $failures.Add("$($invalidCaptureFingerprints.Count) capture entries lack a decoded 16x16 luminance fingerprint.")
}
if ($captureLedger.Count -ne $expectedCaptureEntryCount) {
    $failures.Add("Capture ledger contains $($captureLedger.Count) entries; expected exactly $expectedCaptureEntryCount SPS stage/layer and desktop-final entries.")
}
$captureInventoryPath = Join-Path $reports "vulkan-phase524b-capture-inventory.json"
@($captureEvidence) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $captureInventoryPath -Encoding UTF8

$maxStaticVelocityMagnitude = 0.0001
$minMovingVelocityMagnitude = 0.001
$minDirectionalVelocityComponent = 0.0000001
$minStereoEyeSpecificRmse = 0.0000001
$minStaticEdgeSharpnessRatio = 0.95
$minMovingEdgeSharpnessRatio = 0.80
$maxTemporalConvergenceRmse = 0.01
$minDisocclusionFingerprintRmse = 0.0025
$expectedTemporalCaptureCount = $temporalScenarioDefinitions.Count * $temporalScenarioCaptureStages.Count * 2
$temporalScenarioResults = [System.Collections.Generic.List[object]]::new()
$temporalCaptureEvidence = [System.Collections.Generic.List[object]]::new()

if (-not (Test-ExactStringArray $summary.temporalScenarioCaptureStages $temporalScenarioCaptureStages)) {
    $failures.Add("Temporal scenario capture stages do not match the predeclared velocity/bloom/mono-equivalent/TSR oracle stages.")
}
if ($temporalScenarioMatrix.Count -ne $temporalScenarioDefinitions.Count) {
    $failures.Add("Temporal scenario matrix contains $($temporalScenarioMatrix.Count) definitions; expected exactly $($temporalScenarioDefinitions.Count).")
}
if ($temporalScenarioCaptureLedger.Count -ne $expectedTemporalCaptureCount) {
    $failures.Add("Temporal scenario capture ledger contains $($temporalScenarioCaptureLedger.Count) entries; expected exactly $expectedTemporalCaptureCount.")
}

foreach ($definition in $temporalScenarioDefinitions) {
    $sampleFailuresBefore = $failures.Count
    $sample = [string]$definition.sample
    $matrixEntries = @($temporalScenarioMatrix | Where-Object { [string]$_.sample -ceq $sample })
    if ($matrixEntries.Count -ne 1) {
        $failures.Add("Temporal scenario sample '$sample' has $($matrixEntries.Count) matrix definitions; expected exactly one.")
    }
    else {
        $matrixEntry = $matrixEntries[0]
        if ([string]$matrixEntry.scenario -cne [string]$definition.scenario -or
            [string]$matrixEntry.velocityOracle -cne [string]$definition.velocityOracle -or
            [int]$matrixEntry.captureStartFrame -ne [int]$definition.start -or
            [int]$matrixEntry.captureEndFrame -ne [int]$definition.end -or
            [bool]$matrixEntry.requiresTemporalConvergence -ne [bool]$definition.convergence -or
            [bool]$matrixEntry.isDisocclusionBaseline -ne [bool]$definition.disocclusionBaseline -or
            [bool]$matrixEntry.isDisocclusionResult -ne [bool]$definition.disocclusionResult) {
            $failures.Add("Temporal scenario sample '$sample' changed its predeclared oracle definition.")
        }
    }

    $sampleCaptures = [System.Collections.Generic.List[object]]::new()
    foreach ($stage in $temporalScenarioCaptureStages) {
        $expectedWidth = switch ($stage) {
            "07_Velocity" { $expectedInternalWidth; break }
            "09_BloomMip1" { [Math]::Max(1, $expectedInternalWidth -shr 1); break }
            default { $ExpectedSpsWidth; break }
        }
        $expectedHeight = switch ($stage) {
            "07_Velocity" { $expectedInternalHeight; break }
            "09_BloomMip1" { [Math]::Max(1, $expectedInternalHeight -shr 1); break }
            default { $ExpectedSpsHeight; break }
        }
        for ($layerIndex = 0; $layerIndex -lt 2; $layerIndex++) {
            $expectedPath = [System.IO.Path]::GetFullPath(
                (Join-Path $captureDirectory "DefaultPipelineSps_Temporal_${sample}_${stage}_layer${layerIndex}.png"))
            $entries = @($temporalScenarioCaptureLedger | Where-Object {
                [string]$_.pipelineName -ceq "DefaultPipelineSps" -and
                [string]$_.outputRole -ceq "TemporalScenarioRenderedOutput" -and
                [string]$_.temporalSample -ceq $sample -and
                [string]$_.stage -ceq $stage -and
                [int]$_.layerIndex -eq $layerIndex
            })
            if ($entries.Count -ne 1) {
                $failures.Add("Temporal capture '$sample/$stage/layer$layerIndex' has $($entries.Count) ledger entries; expected exactly one.")
                continue
            }

            $entry = $entries[0]
            $luminanceFingerprint = @(Get-JsonArray $entry.luminanceFingerprint)
            $velocityFingerprint = @(Get-JsonArray $entry.velocityMagnitudeFingerprint)
            $entryPath = [System.IO.Path]::GetFullPath([string]$entry.path)
            $identityValid =
                [string]$entry.temporalScenario -ceq [string]$definition.scenario -and
                [string]$entry.velocityOracle -ceq [string]$definition.velocityOracle -and
                [int]$entry.expectedLayerCount -eq 2 -and [uint32]$entry.viewMask -eq 3 -and
                [string]$entry.antiAliasingMode -ceq "Tsr" -and
                [uint64]$entry.renderFrameId -ne 0 -and
                [int]$entry.temporalSequenceFrame -ge [int]$definition.start -and
                [int]$entry.temporalSequenceFrame -le [int]$definition.end -and
                [int]$entry.width -eq $expectedWidth -and [int]$entry.height -eq $expectedHeight -and
                [long]$entry.lengthBytes -gt 0 -and
                [int]$entry.luminanceFingerprintWidth -eq 16 -and
                [int]$entry.luminanceFingerprintHeight -eq 16 -and $luminanceFingerprint.Count -eq 256 -and
                [int]$entry.velocityMagnitudeFingerprintWidth -eq 16 -and
                [int]$entry.velocityMagnitudeFingerprintHeight -eq 16 -and $velocityFingerprint.Count -eq 256 -and
                [string]::Equals($entryPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase) -and
                (Test-Path -LiteralPath $expectedPath -PathType Leaf)
            if (-not $identityValid) {
                $failures.Add("Temporal capture '$sample/$stage/layer$layerIndex' has invalid identity, extent, frame, fingerprint, or file evidence.")
                continue
            }

            $header = Get-PngHeaderInfo -Path $expectedPath
            if ($header.signature -cne "89504E470D0A1A0A" -or
                [uint32]$header.width -ne [uint32]$expectedWidth -or
                [uint32]$header.height -ne [uint32]$expectedHeight) {
                $failures.Add("Temporal capture '$sample/$stage/layer$layerIndex' PNG header does not match ${expectedWidth}x${expectedHeight}.")
                continue
            }

            $sampleCaptures.Add($entry)
            $temporalCaptureEvidence.Add([ordered]@{
                scenario = [string]$definition.scenario
                sample = $sample
                stage = $stage
                layerIndex = $layerIndex
                renderFrameId = [uint64]$entry.renderFrameId
                sequenceFrame = [int]$entry.temporalSequenceFrame
                width = [int]$entry.width
                height = [int]$entry.height
                path = $expectedPath
                sha256 = (Get-FileHash -LiteralPath $expectedPath -Algorithm SHA256).Hash
            })
        }
    }

    if ($sampleCaptures.Count -eq ($temporalScenarioCaptureStages.Count * 2)) {
        $sampleRenderFrames = @($sampleCaptures | ForEach-Object { [uint64]$_.renderFrameId } | Sort-Object -Unique)
        $sampleSequenceFrames = @($sampleCaptures | ForEach-Object { [int]$_.temporalSequenceFrame } | Sort-Object -Unique)
        if ($sampleRenderFrames.Count -ne 1 -or $sampleSequenceFrames.Count -ne 1) {
            $failures.Add("Temporal sample '$sample' was not captured from one rendered strict-SPS frame.")
        }

        $velocityLeft = @($sampleCaptures | Where-Object { [string]$_.stage -ceq "07_Velocity" -and [int]$_.layerIndex -eq 0 })[0]
        $velocityRight = @($sampleCaptures | Where-Object { [string]$_.stage -ceq "07_Velocity" -and [int]$_.layerIndex -eq 1 })[0]
        if ([string]$definition.velocityOracle -ceq "Zero") {
            if ([double]$velocityLeft.velocityMaxMagnitude -gt $maxStaticVelocityMagnitude -or
                [double]$velocityRight.velocityMaxMagnitude -gt $maxStaticVelocityMagnitude) {
                $failures.Add("Temporal sample '$sample' exceeded the static-zero velocity limit.")
            }
        }
        else {
            if ([double]$velocityLeft.velocityMaxMagnitude -lt $minMovingVelocityMagnitude -or
                [double]$velocityRight.velocityMaxMagnitude -lt $minMovingVelocityMagnitude) {
                $failures.Add("Temporal sample '$sample' did not produce moving velocity in both eyes.")
            }
            $direction = if ([string]$definition.velocityOracle -ceq "PositiveX") { 1.0 } else { -1.0 }
            if (([double]$velocityLeft.velocityMeanX * $direction) -le $minDirectionalVelocityComponent -or
                ([double]$velocityRight.velocityMeanX * $direction) -le $minDirectionalVelocityComponent) {
                $failures.Add("Temporal sample '$sample' velocity direction did not match $($definition.velocityOracle).")
            }
            $velocityEyeRmse = Get-FingerprintRmse $velocityLeft.velocityMagnitudeFingerprint $velocityRight.velocityMagnitudeFingerprint
            if ($velocityEyeRmse -le $minStereoEyeSpecificRmse) {
                $failures.Add("Temporal sample '$sample' velocity evidence was not eye-specific (RMSE=$velocityEyeRmse).")
            }
        }

        $bloomLeft = @($sampleCaptures | Where-Object { [string]$_.stage -ceq "09_BloomMip1" -and [int]$_.layerIndex -eq 0 })[0]
        $bloomRight = @($sampleCaptures | Where-Object { [string]$_.stage -ceq "09_BloomMip1" -and [int]$_.layerIndex -eq 1 })[0]
        $bloomEnergyDenominator = [Math]::Max([Math]::Max([Math]::Abs([double]$bloomLeft.luminanceEnergy), [Math]::Abs([double]$bloomRight.luminanceEnergy)), [double]::Epsilon)
        $bloomEnergyDelta = [Math]::Abs([double]$bloomLeft.luminanceEnergy - [double]$bloomRight.luminanceEnergy) / $bloomEnergyDenominator
        $bloomCentroidX = [double]$bloomLeft.bloomCentroidX - [double]$bloomRight.bloomCentroidX
        $bloomCentroidY = [double]$bloomLeft.bloomCentroidY - [double]$bloomRight.bloomCentroidY
        $bloomCentroidDistance = [Math]::Sqrt(($bloomCentroidX * $bloomCentroidX) + ($bloomCentroidY * $bloomCentroidY))
        $bloomEyeRmse = Get-FingerprintRmse $bloomLeft.luminanceFingerprint $bloomRight.luminanceFingerprint
        if ([double]$bloomLeft.luminanceEnergy -le 0.0 -or [double]$bloomRight.luminanceEnergy -le 0.0 -or
            $bloomEnergyDelta -gt $maxBloomRelativeEnergyDelta -or
            $bloomCentroidDistance -gt $maxBloomCentroidDistance -or
            $bloomEyeRmse -le $minStereoEyeSpecificRmse) {
            $failures.Add("Temporal sample '$sample' failed bloom energy/centroid/independent-eye oracles.")
        }

        for ($layerIndex = 0; $layerIndex -lt 2; $layerIndex++) {
            $monoEquivalent = @($sampleCaptures | Where-Object { [string]$_.stage -ceq "13c_MonoTsrReference" -and [int]$_.layerIndex -eq $layerIndex })[0]
            $temporalOutput = @($sampleCaptures | Where-Object { [string]$_.stage -ceq "14_TsrOutput" -and [int]$_.layerIndex -eq $layerIndex })[0]
            $minimumSharpness = if ([bool]$definition.convergence) { $minStaticEdgeSharpnessRatio } else { $minMovingEdgeSharpnessRatio }
            $sharpnessRatio = if ([double]$monoEquivalent.edgeMaxGradient -gt 0.0) {
                [double]$temporalOutput.edgeMaxGradient / [double]$monoEquivalent.edgeMaxGradient
            }
            else { 0.0 }
            if ($sharpnessRatio -lt $minimumSharpness) {
                $failures.Add("Temporal sample '$sample' layer $layerIndex lost edge sharpness against its mono-equivalent input.")
            }
            if ([bool]$definition.convergence) {
                $convergenceRmse = Get-FingerprintRmse $temporalOutput.luminanceFingerprint $monoEquivalent.luminanceFingerprint
                if ($convergenceRmse -gt $maxTemporalConvergenceRmse) {
                    $failures.Add("Temporal sample '$sample' layer $layerIndex did not converge to its mono-equivalent input (RMSE=$convergenceRmse).")
                }
            }
        }
    }

    $temporalScenarioResults.Add([ordered]@{
        scenario = [string]$definition.scenario
        sample = $sample
        velocityOracle = [string]$definition.velocityOracle
        captureWindow = "$($definition.start)-$($definition.end)"
        captureCount = $sampleCaptures.Count
        passed = $failures.Count -eq $sampleFailuresBefore
    })
}

for ($layerIndex = 0; $layerIndex -lt 2; $layerIndex++) {
    $occluded = @($temporalScenarioCaptureLedger | Where-Object {
        [string]$_.temporalSample -ceq "DisocclusionOccluded" -and
        [string]$_.stage -ceq "13c_MonoTsrReference" -and [int]$_.layerIndex -eq $layerIndex
    })
    $revealed = @($temporalScenarioCaptureLedger | Where-Object {
        [string]$_.temporalSample -ceq "DisocclusionRevealed" -and
        [string]$_.stage -ceq "13c_MonoTsrReference" -and [int]$_.layerIndex -eq $layerIndex
    })
    if ($occluded.Count -eq 1 -and $revealed.Count -eq 1) {
        $disocclusionRmse = Get-FingerprintRmse $occluded[0].luminanceFingerprint $revealed[0].luminanceFingerprint
        if ($disocclusionRmse -lt $minDisocclusionFingerprintRmse) {
            $failures.Add("Disocclusion layer $layerIndex did not reveal a changed rendered input (RMSE=$disocclusionRmse).")
        }
    }
}

$temporalCaptureInventoryPath = Join-Path $reports "vulkan-phase524b-temporal-scenario-capture-inventory.json"
@($temporalCaptureEvidence) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporalCaptureInventoryPath -Encoding UTF8

$provenanceCaptureEntries = @(
    @($captureLedger | Where-Object { [string]$_.pipelineName -like "DefaultPipeline*" }) +
    @($temporalScenarioCaptureLedger)
)
foreach ($entry in $provenanceCaptureEntries) {
    $entryPath = [System.IO.Path]::GetFullPath([string]$entry.path)
    $metricsPath = "$entryPath.metrics.json"
    if (-not (Test-Path -LiteralPath $entryPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $metricsPath -PathType Leaf)) {
        $failures.Add("Capture provenance is incomplete for '$entryPath'.")
        continue
    }

    $actualHash = (Get-FileHash -LiteralPath $entryPath -Algorithm SHA256).Hash
    $metricsCapturePath = if ([string]::IsNullOrWhiteSpace([string]$entry.metricsCapturePath)) {
        [string]::Empty
    }
    else {
        [System.IO.Path]::GetFullPath([string]$entry.metricsCapturePath)
    }
    if ([string]::IsNullOrWhiteSpace([string]$entry.sha256) -or
        $actualHash -cne [string]$entry.sha256 -or
        -not [string]::Equals($entryPath, $metricsCapturePath, [StringComparison]::OrdinalIgnoreCase) -or
        [DateTimeOffset]$entry.metricsCapturedAtUtc -eq [DateTimeOffset]::MinValue) {
        $failures.Add("Capture provenance hash/path/timestamp mismatch for '$entryPath'.")
    }
    if ($summary.retainedCohortStartedAtUtc -and
        [DateTimeOffset]$entry.lastWriteTimeUtc -ge [DateTimeOffset]$summary.retainedCohortStartedAtUtc) {
        $failures.Add("Capture '$entryPath' was not completed before the retained cohort.")
    }
}

$engineLogDirectory = [string]$summary.logDirectory
$copiedLogFiles = [System.Collections.Generic.List[string]]::new()
$engineLogCopyDirectory = Join-Path $logs "engine"
[System.IO.Directory]::CreateDirectory($engineLogCopyDirectory) | Out-Null
if (-not [string]::IsNullOrWhiteSpace($engineLogDirectory) -and (Test-Path -LiteralPath $engineLogDirectory -PathType Container)) {
    foreach ($source in (Get-ChildItem -LiteralPath $engineLogDirectory -File -Filter "*.log" -ErrorAction SilentlyContinue)) {
        $destination = Join-Path $engineLogCopyDirectory $source.Name
        Copy-Item -LiteralPath $source.FullName -Destination $destination -Force
        $copiedLogFiles.Add($destination)
    }
}
foreach ($durableLog in (Get-ChildItem -LiteralPath $engineLogCopyDirectory -File -Filter "*.log" -ErrorAction SilentlyContinue)) {
    if (-not $copiedLogFiles.Contains($durableLog.FullName)) {
        $copiedLogFiles.Add($durableLog.FullName)
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $engineLogCopyDirectory "log_vulkan.log") -PathType Leaf)) {
    $failures.Add("The raw Vulkan engine log was not available for validation.")
}

$forbiddenLogPatterns = @(
    [pscustomobject]@{ text = "VUID-"; description = "Vulkan VUID validation error"; allowExternal = $true },
    [pscustomobject]@{ text = "SYNC-HAZARD"; description = "Vulkan synchronization hazard"; allowExternal = $true },
    [pscustomobject]@{ text = "UNASSIGNED"; description = "unassigned Vulkan validation error"; allowExternal = $true },
    [pscustomobject]@{ text = "ErrorValidationFailed"; description = "engine submission validation rejection"; allowExternal = $false },
    [pscustomobject]@{ text = "Rejected queue submission"; description = "rejected Vulkan queue submission"; allowExternal = $false },
    [pscustomobject]@{ text = "Strict SinglePassStereo sequential fallback attempt blocked"; description = "strict-SPS sequential fallback"; allowExternal = $false },
    [pscustomobject]@{ text = "falling back to sequential"; description = "strict-SPS sequential fallback"; allowExternal = $false },
    [pscustomobject]@{ text = "reason=ResourcePlanReplacement"; description = "blocking resource-plan replacement wait"; allowExternal = $false },
    [pscustomobject]@{ text = "Logical device lost"; description = "Vulkan device loss"; allowExternal = $false },
    [pscustomobject]@{ text = "ErrorDeviceLost"; description = "Vulkan device loss"; allowExternal = $false },
    [pscustomobject]@{ text = "Refusing skipped-frame present for unwritten swapchain image"; description = "unwritten desktop present attempt"; allowExternal = $false },
    [pscustomobject]@{ text = "without a recorded final write or valid prior contents"; description = "invalid desktop present contents"; allowExternal = $false },
    [pscustomobject]@{ text = "dirtied after recording and before submit"; description = "rejected/last-image desktop frame"; allowExternal = $false }
)

$filteredLogPath = Join-Path $logs "phase524b-filtered-log-matches.log"
$filteredLogJsonPath = Join-Path $reports "vulkan-phase524b-filtered-log-matches.json"
Remove-Item -LiteralPath $filteredLogPath -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $filteredLogJsonPath -Force -ErrorAction SilentlyContinue
$rawLogFiles = @(Get-ChildItem -LiteralPath $logs -File -Filter "*.log" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { -not [string]::Equals($_.FullName, $filteredLogPath, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object { $_.FullName } |
    Sort-Object -Unique)
$shutdownMarker = $null
foreach ($path in $rawLogFiles) {
    $fileLines = [System.IO.File]::ReadAllLines($path)
    for ($lineIndex = 0; $lineIndex -lt $fileLines.Length; $lineIndex++) {
        $line = $fileLines[$lineIndex]
        if ($line.IndexOf("[OpenXRSmoke] OpenXR smoke drain complete.", [StringComparison]::Ordinal) -lt 0 -or
            $line.IndexOf("Requesting engine shutdown.", [StringComparison]::Ordinal) -lt 0) {
            continue
        }

        $markerTimestamp = Get-LogLineTimestamp $line
        if ($null -eq $markerTimestamp) {
            continue
        }
        if ($null -eq $shutdownMarker -or $markerTimestamp -lt $shutdownMarker.timestamp) {
            $shutdownMarker = [pscustomobject]@{
                timestamp = $markerTimestamp
                path = $path
                lineNumber = $lineIndex + 1
                text = $line
            }
        }
    }
}
$firstChanceRenderingTokens = @(
    "XREngine.Rendering",
    "Vulkan",
    "OpenXR",
    "ResourceLifetime",
    "DescriptorSet",
    "CommandBuffer",
    "RenderPipeline",
    "XRViewport"
)
$shutdownOnlyDestroyDeviceVuid = "VUID-vkDestroyDevice-device-05137"
$logMatches = [System.Collections.Generic.List[object]]::new()
foreach ($path in $rawLogFiles) {
    $fileLines = [System.IO.File]::ReadAllLines($path)
    for ($lineIndex = 0; $lineIndex -lt $fileLines.Length; $lineIndex++) {
        $line = $fileLines[$lineIndex]
        $lineNumber = $lineIndex + 1
        foreach ($pattern in $forbiddenLogPatterns) {
            if ($line.IndexOf([string]$pattern.text, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
                continue
            }
            $allowed = $false
            $classification = "RetainedFrameOrRuntimeValidationFailure"
            $allowReason = $null
            $matchedAt = $null
            $diagnosticContext = $line
            $isShutdownOnlyDestroyDevice = $false
            if ([string]$pattern.text -ceq "VUID-" -and
                $line.IndexOf($shutdownOnlyDestroyDeviceVuid, [StringComparison]::Ordinal) -ge 0 -and
                $null -ne $shutdownMarker) {
                $contextStart = [Math]::Max(0, $lineIndex - 2)
                $contextEnd = [Math]::Min($fileLines.Length - 1, $lineIndex + 12)
                $diagnosticContext = [string]::Join(
                    [Environment]::NewLine,
                    $fileLines[$contextStart..$contextEnd])
                $timestampSearchStart = [Math]::Max(0, $lineIndex - 32)
                for ($timestampIndex = $lineIndex; $timestampIndex -ge $timestampSearchStart; $timestampIndex--) {
                    $matchedAt = Get-LogLineTimestamp $fileLines[$timestampIndex]
                    if ($null -ne $matchedAt) {
                        break
                    }
                }
                $isShutdownOnlyDestroyDevice = $null -ne $matchedAt -and
                    $matchedAt -ge $shutdownMarker.timestamp -and
                    $diagnosticContext.IndexOf("vkDestroyDevice()", [StringComparison]::Ordinal) -ge 0 -and
                    $diagnosticContext.IndexOf("VulkanRenderer.DestroyLogicalDevice()", [StringComparison]::Ordinal) -ge 0 -and
                    $diagnosticContext.IndexOf("VulkanRenderer.CleanUp()", [StringComparison]::Ordinal) -ge 0
                if ($isShutdownOnlyDestroyDevice) {
                    $allowed = $true
                    $classification = "ShutdownOnlyTeardownNoise"
                    $allowReason = "Exact $shutdownOnlyDestroyDeviceVuid diagnostic occurred after the explicit engine-shutdown marker in the Vulkan device-destruction call path."
                }
            }
            if (-not $allowed -and [bool]$pattern.allowExternal) {
                foreach ($allowToken in $ExternallyOwnedValidationAllowlist) {
                    if (-not [string]::IsNullOrWhiteSpace($allowToken) -and
                        $line.IndexOf($allowToken, [StringComparison]::Ordinal) -ge 0) {
                        $allowed = $true
                        $classification = "ExternallyOwnedAllowlistedValidation"
                        $allowReason = "Matched the explicit externally owned validation allowlist token '$allowToken'."
                        break
                    }
                }
            }
            $logMatches.Add([pscustomobject]@{
                path = $path
                lineNumber = $lineNumber
                pattern = [string]$pattern.text
                description = [string]$pattern.description
                allowed = $allowed
                classification = $classification
                allowReason = $allowReason
                blocksRetainedFrameValidation = -not $allowed
                eventTimestamp = $matchedAt
                text = $line
                diagnosticContext = $diagnosticContext
            })
        }

        if ($line.IndexOf("[FirstChance]", [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $contextEnd = [Math]::Min($fileLines.Length - 1, $lineIndex + 40)
            $context = [string]::Join(
                [Environment]::NewLine,
                $fileLines[$lineIndex..$contextEnd])
            $renderingContext = $false
            foreach ($token in $firstChanceRenderingTokens) {
                if ($context.IndexOf($token, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                    $renderingContext = $true
                    break
                }
            }
            $logMatches.Add([pscustomobject]@{
                path = $path
                lineNumber = $lineNumber
                pattern = "[FirstChance]"
                description = if ($renderingContext) {
                    "rendering/Vulkan/lifetime first-chance exception"
                }
                else {
                    "non-rendering first-chance exception"
                }
                allowed = -not $renderingContext
                classification = if ($renderingContext) {
                    "RetainedFrameOrRuntimeValidationFailure"
                }
                else {
                    "NonRenderingFirstChanceException"
                }
                allowReason = if ($renderingContext) { $null } else { "The exception context contains no rendering/Vulkan/lifetime token." }
                blocksRetainedFrameValidation = $renderingContext
                eventTimestamp = Get-LogLineTimestamp $line
                text = $context
                diagnosticContext = $context
            })
        }
    }
}
$rejectedLogMatches = @($logMatches | Where-Object { -not [bool]$_.allowed })
$shutdownOnlyTeardownMatches = @($logMatches | Where-Object { [string]$_.classification -ceq "ShutdownOnlyTeardownNoise" })
if ($rejectedLogMatches.Count -gt 0) {
    $groupedRejectedMatches = @($rejectedLogMatches | Group-Object description)
    foreach ($group in $groupedRejectedMatches) {
        $failures.Add("Raw logs contain $($group.Count) unapproved $($group.Name) match(es).")
    }
}
$filteredLines = if ($logMatches.Count -eq 0) {
    @("No forbidden or allowlisted Vulkan Phase 5.2.4b log patterns were found.")
}
else {
    @($logMatches | ForEach-Object {
        "[{0}] {1}:{2} pattern='{3}' classification='{4}' {5}" -f $(if ([bool]$_.allowed) { "ALLOWED" } else { "REJECTED" }), $_.path, $_.lineNumber, $_.pattern, $_.classification, $_.text
    })
}
$filteredLines | Set-Content -LiteralPath $filteredLogPath -Encoding UTF8
@($logMatches) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $filteredLogJsonPath -Encoding UTF8

$allLogText = [string]::Join([Environment]::NewLine, @($rawLogFiles | ForEach-Object {
    Get-Content -LiteralPath $_ -Raw
}))
if ($allLogText -notmatch '\[VulkanDiag\].*Preset=SyncValidation.*ValidationLayers=True') {
    $failures.Add("Effective Vulkan SyncValidation preset/layer activation was not recorded.")
}
if ($allLogText -notmatch 'ValidationFeatures=.*SynchronizationValidation') {
    $failures.Add("Synchronization validation was not effectively enabled.")
}
$fullResolutionPostProcessPattern =
    '\[PostProcessDiag\] Fullscreen[^\r\n]*destinationExtent=896x1007[^\r\n]*renderArea=\(0,0,896,1007\)[^\r\n]*viewport=\(0,0,896,1007\)[^\r\n]*scissor=\(0,0,896,1007\)[^\r\n]*(attachmentLayers|layers)=2[^\r\n]*viewMask=0x3[^\r\n]*screenOrigin=\(0,0\)[^\r\n]*uv=(\(fragCoord-screenOrigin\)|localRaster)/destinationExtent->\[0,1\]'
if ($allLogText -notmatch $fullResolutionPostProcessPattern) {
    $failures.Add("Raw logs do not prove the exact SPS fullscreen local-raster contract at 896x1007, two layers, viewMask=0x3, and [0,1] UVs.")
}
if ($allLogText -notmatch '\[PostProcessDiag\] OutputHDR=[^\r\n]*BloomEnabled=True[^\r\n]*BloomMips=1-4') {
    $failures.Add("Raw logs do not prove bloom enabled with the required composition mip range 1-4; mip 0-4 accumulation is validated by the explicit capture inventory.")
}
$expectedBloomExtents = for ($mip = 0; $mip -le 4; $mip++) {
    "{0}x{1}" -f
        [Math]::Max(1, $expectedInternalWidth -shr $mip),
        [Math]::Max(1, $expectedInternalHeight -shr $mip)
}
foreach ($bloomExtent in $expectedBloomExtents) {
    $bloomPattern = "\[PostProcessDiag\] Fullscreen[^\r\n]*Bloom[^\r\n]*destinationExtent=$([regex]::Escape($bloomExtent))[^\r\n]*(attachmentLayers|layers)=2[^\r\n]*viewMask=0x3"
    if ($allLogText -notmatch $bloomPattern) {
        $failures.Add("Raw logs do not prove the stereo bloom destination extent $bloomExtent with layers=2/viewMask=0x3.")
    }
}

$sortedFrameTimes = @($frameTimes | Sort-Object)
$sortedGpuTimes = @($gpuTimes | Sort-Object)
$elapsedRetainedSeconds = if ($frameLedger.Count -gt 1) {
    ([double]$frameLedger[-1].elapsedMilliseconds - [double]$frameLedger[0].elapsedMilliseconds) / 1000.0
}
else {
    0.0
}
$throughput = if ($elapsedRetainedSeconds -gt 0.0) { ($frameLedger.Count - 1) / $elapsedRetainedSeconds } else { 0.0 }
$cpuFrameP95 = Get-Percentile -Sorted $sortedFrameTimes -Percentile 0.95
if ($MinimumObservedFramesPerSecond -gt 0.0 -and $throughput -lt $MinimumObservedFramesPerSecond) {
    $failures.Add("Observed retained throughput was $($throughput.ToString('F2')) frames/s, below the Phase 5.2.4b floor of $($MinimumObservedFramesPerSecond.ToString('F2')) frames/s.")
}
if ($MaximumCpuFrameP95Milliseconds -gt 0.0 -and $cpuFrameP95 -gt $MaximumCpuFrameP95Milliseconds) {
    $failures.Add("Retained CPU frame p95 was $($cpuFrameP95.ToString('F2')) ms, above the Phase 5.2.4b ceiling of $($MaximumCpuFrameP95Milliseconds.ToString('F2')) ms.")
}

$subNativeCompanion = $null
$subNativeOffExitCode = 1
$subNativeExitCode = 1
$subNativeReport = $null
if (-not $SkipSubNativeCompanion -and $failures.Count -eq 0) {
    $subNativeRunRoot = Join-Path $RunRoot "subnative-companion"
    $subNativeReportPath = Join-Path $subNativeRunRoot "reports\vulkan-phase524b-validation.json"
    $subNativeOffRunRoot = Join-Path $RunRoot "subnative-occlusion-off"
    $subNativeOffSummaryPath = Join-Path $subNativeOffRunRoot "reports\openxr-smoke-summary.json"
    $offRunner = Join-Path $repoRoot "Tools\OpenXR\Run-OpenXrPhase524bOcclusionOff.ps1"
    $offArguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $offRunner,
        "-WarmupFrames", [string]$WarmupFrames,
        "-RetainedFrames", [string]$RetainedFrames,
        "-CaptureSkipFrames", [string]$CaptureSkipFrames,
        "-FoveationMode", $FoveationMode,
        "-TsrResolutionScale", $SubNativeTsrResolutionScale.ToString("0.####", [System.Globalization.CultureInfo]::InvariantCulture),
        "-TimeoutSeconds", [string]$TimeoutSeconds,
        "-RunRoot", $subNativeOffRunRoot,
        "-NoBuild"
    )
    if (-not [string]::IsNullOrWhiteSpace($RuntimeJson)) { $offArguments += @("-RuntimeJson", $RuntimeJson) }
    if (-not [string]::IsNullOrWhiteSpace($EditorDll)) { $offArguments += @("-EditorDll", $EditorDll) }
    if ($StartService) { $offArguments += "-StartService" }
    if ($ExternallyOwnedValidationAllowlist.Count -gt 0) {
        $offArguments += "-ExternallyOwnedValidationAllowlist"
        $offArguments += $ExternallyOwnedValidationAllowlist
    }
    try {
        $LASTEXITCODE = 0
        & $currentPowerShellExecutable @offArguments
        $subNativeOffExitCode = [int]$LASTEXITCODE
    }
    catch {
        $failures.Add("The separate sub-native occlusion-off cohort could not be invoked: $($_.Exception.Message)")
    }
    if ($subNativeOffExitCode -ne 0 -or -not (Test-Path -LiteralPath $subNativeOffSummaryPath -PathType Leaf)) {
        $failures.Add("The separate sub-native occlusion-off cohort failed with exit code $subNativeOffExitCode or did not write its summary.")
    }

    $companionArguments = @(
        "-NoProfile",
        "-File", $PSCommandPath,
        "-WarmupFrames", [string]$WarmupFrames,
        "-CaptureSkipFrames", [string]$CaptureSkipFrames,
        "-CaptureSettleFrames", [string]$CaptureSettleFrames,
        "-RetainedFrames", [string]$RetainedFrames,
        "-TimeoutSeconds", [string]$TimeoutSeconds,
        "-MaximumOcclusionRecoveryAgeFrames", [string]$MaximumOcclusionRecoveryAgeFrames,
        "-MaximumOcclusionResultAgeFrames", [string]$MaximumOcclusionResultAgeFrames,
        "-MaximumLiveResourceCount", [string]$MaximumLiveResourceCount,
        "-MaximumTrackedDescriptorSetCount", [string]$MaximumTrackedDescriptorSetCount,
        "-MaximumPlannerStateCount", [string]$MaximumPlannerStateCount,
        "-MaximumCommandVariantCount", [string]$MaximumCommandVariantCount,
        "-SteadyStateWindowFrames", [string]$SteadyStateWindowFrames,
        "-ExpectedSpsWidth", [string]$ExpectedSpsWidth,
        "-ExpectedSpsHeight", [string]$ExpectedSpsHeight,
        "-FoveationMode", $FoveationMode,
        "-TsrResolutionScale", $SubNativeTsrResolutionScale.ToString("0.####", [System.Globalization.CultureInfo]::InvariantCulture),
        "-SubNativeTsrResolutionScale", $SubNativeTsrResolutionScale.ToString("0.####", [System.Globalization.CultureInfo]::InvariantCulture),
        "-RunRoot", $subNativeRunRoot,
        "-StrictFailureReportPath", $StrictFailureReportPath,
        "-OcclusionOffSummaryPath", $subNativeOffSummaryPath,
        "-NoBuild",
        "-SkipSubNativeCompanion"
    )
    if (-not [string]::IsNullOrWhiteSpace($RuntimeJson)) {
        $companionArguments += @("-RuntimeJson", $RuntimeJson)
    }
    if (-not [string]::IsNullOrWhiteSpace($EditorDll)) {
        $companionArguments += @("-EditorDll", $EditorDll)
    }
    if ($StartService) {
        $companionArguments += "-StartService"
    }
    if ($ExternallyOwnedValidationAllowlist.Count -gt 0) {
        $companionArguments += "-ExternallyOwnedValidationAllowlist"
        $companionArguments += $ExternallyOwnedValidationAllowlist
    }

    if ($failures.Count -eq 0) {
        try {
            $LASTEXITCODE = 0
            & $currentPowerShellExecutable @companionArguments
            $subNativeExitCode = [int]$LASTEXITCODE
        }
        catch {
            $failures.Add("The separate sub-native TSR companion cohort could not be invoked with '$currentPowerShellExecutable': $($_.Exception.Message)")
        }
    }
    if ($subNativeExitCode -ne 0 -or -not (Test-Path -LiteralPath $subNativeReportPath -PathType Leaf)) {
        $failures.Add("The separate sub-native TSR companion cohort failed with exit code $subNativeExitCode or did not write its report.")
    }
    else {
        $subNativeReport = Get-Content -LiteralPath $subNativeReportPath -Raw | ConvertFrom-Json
        if (-not [bool]$subNativeReport.passed -or
            [string]$subNativeReport.configuration.cohortKind -cne "SubNative" -or
            [double]$subNativeReport.configuration.tsrResolutionScaleEffective -ge 1.0) {
            $failures.Add("The separate sub-native TSR companion report did not prove an effective sub-native passing cohort.")
        }
        $subNativeCompanion = [ordered]@{
            report = $subNativeReportPath
            passed = [bool]$subNativeReport.passed
            requestedScale = [double]$subNativeReport.configuration.tsrResolutionScaleRequested
            effectiveScale = [double]$subNativeReport.configuration.tsrResolutionScaleEffective
            retainedFrames = [int]$subNativeReport.configuration.retainedFrames
        }
    }
}

$result = [ordered]@{
    schemaVersion = 3
    capturedAtUtc = [DateTimeOffset]::UtcNow
    passed = $failures.Count -eq 0
    runRoot = $RunRoot
    engineLogDirectory = $engineLogDirectory
    configuration = [ordered]@{
        vulkanSdkRoot = $selectedVulkanSdk.Root
        validationLayerApiVersion = $selectedVulkanSdk.ApiVersionText
        validationLayerManifest = $selectedVulkanSdk.ManifestPath
        renderer = "Vulkan"
        renderTargetModeRequested = [string]$summary.vulkanRenderTargetModeRequested
        renderTargetModeEffective = [string]$summary.vulkanRenderTargetModeEffective
        viewRenderMode = "SinglePassStereo"
        implementationPath = [string]$summary.viewRenderImplementationPath
        mirrorMode = "FullIndependentRender"
        foveationMode = $FoveationMode
        antiAliasingEffective = [string]$summary.antiAliasingModeEffective
        cohortKind = if ($TsrResolutionScale -lt 0.9999) { "SubNative" } else { "Native" }
        tsrResolutionScaleRequested = [double]$summary.tsrResolutionScaleRequested
        tsrResolutionScaleEffective = [double]$summary.tsrResolutionScaleEffective
        occlusionRequested = [string]$summary.occlusionCullingModeRequested
        occlusionEffective = [string]$summary.occlusionCullingModeEffective
        diagnosticPresetRequested = [string]$summary.vulkanDiagnosticPresetRequested
        diagnosticPresetEffective = [string]$summary.vulkanDiagnosticPresetEffective
        validationLayersEffective = [bool]$summary.vulkanValidationLayersEffective
        synchronizationValidationEffective = [bool]$summary.vulkanSynchronizationValidationEffective
        validationLayers = @(Get-JsonArray $summary.vulkanValidationLayers)
        validationFeatures = @(Get-JsonArray $summary.vulkanValidationFeatures)
        warmupFrames = $WarmupFrames
        captureSkipFrames = $CaptureSkipFrames
        captureSettleFrames = $CaptureSettleFrames
        captureMotionSampleCount = $captureMotionSampleCount
        captureMotionIntervalFrames = $captureMotionIntervalFrames
        retainedCohortStartedAtUtc = $summary.retainedCohortStartedAtUtc
        retainedFrames = $RetainedFrames
        expectedSpsExtent = "${ExpectedSpsWidth}x${ExpectedSpsHeight}"
        maximumOcclusionRecoveryAgeFrames = $MaximumOcclusionRecoveryAgeFrames
        maximumOcclusionResultAgeFrames = $MaximumOcclusionResultAgeFrames
        externallyOwnedValidationAllowlist = @($ExternallyOwnedValidationAllowlist)
        maximumLiveResourceCount = $MaximumLiveResourceCount
        maximumTrackedDescriptorSetCount = $MaximumTrackedDescriptorSetCount
        maximumPlannerStateCount = $MaximumPlannerStateCount
        maximumCommandVariantCount = $MaximumCommandVariantCount
        steadyStateWindowFrames = $SteadyStateWindowFrames
        powerShellExecutable = $currentPowerShellExecutable
        powerShellVersion = [string]$PSVersionTable.PSVersion
    }
    performance = [ordered]@{
        retainedDurationSeconds = $elapsedRetainedSeconds
        observedFramesPerSecond = $throughput
        minimumObservedFramesPerSecond = $MinimumObservedFramesPerSecond
        cpuFrameP50Milliseconds = Get-Percentile -Sorted $sortedFrameTimes -Percentile 0.50
        cpuFrameP95Milliseconds = $cpuFrameP95
        maximumCpuFrameP95Milliseconds = $MaximumCpuFrameP95Milliseconds
        cpuFrameP99Milliseconds = Get-Percentile -Sorted $sortedFrameTimes -Percentile 0.99
        cpuFrameWorstMilliseconds = if ($sortedFrameTimes.Count -gt 0) { $sortedFrameTimes[-1] } else { 0.0 }
        gpuFrameP50Milliseconds = Get-Percentile -Sorted $sortedGpuTimes -Percentile 0.50
        gpuFrameP95Milliseconds = Get-Percentile -Sorted $sortedGpuTimes -Percentile 0.95
        recordFrames = $recordFrames
        reuseFrames = $reuseFrames
    }
    occlusion = [ordered]@{
        tested = $occlusionTestedTotal
        culled = $occlusionCulledTotal
        submitted = $occlusionSubmittedTotal
        resolved = $occlusionResolvedTotal
        maxResultAgeFrames = $maxOcclusionAge
        maxRecoveryAgeFrames = ($occlusionViewLedger | Measure-Object -Property maxRecoveryAgeFrames -Maximum).Maximum
        desktopPovIds = $desktopPovIds
        desktopOutputIds = $desktopOutputIds
        desktopSubmissions = $desktopViewSubmissions
        desktopResolutions = $desktopViewResolutions
        desktopCulls = $desktopViewCulls
        desktopRecoveryStarts = $desktopRecoveryStarts
        desktopRecoveryCompletions = $desktopRecoveryCompletions
        vrPovIds = $vrPovIds
        vrOutputIds = $vrOutputIds
        vrRequiredCoverageMask = "0x$($expectedVrCoverageMask.ToString('X'))"
        vrSubmissions = $vrViewSubmissions
        vrResolutions = $vrViewResolutions
        vrCulls = $vrViewCulls
        vrRecoveryStarts = $vrRecoveryStarts
        vrRecoveryCompletions = $vrRecoveryCompletions
        namedEvidenceEntryCount = $occlusionEvidenceLedger.Count
        namedEvidenceOverflowCount = [long]$summary.occlusionEvidenceOverflowCount
        visibleSentinelCullCount = $culledVisibleSentinels.Count
        desktopHiddenProofCullCount = $desktopHiddenCulls.Count
        spsHiddenProofCullCount = $spsHiddenCulls.Count
        offSummary = $OcclusionOffSummaryPath
        offEvidenceEntryCount = $occlusionOffEvidence.Count
        finalImageParity = @($finalImageParityResults)
    }
    churn = [ordered]@{
        allocationFrames = $allocationFrames
        descriptorChurnFrames = $descriptorChurnFrames
        retirementFrames = $retirementFrames
        resourcePlanReplacementFrames = $planReplacementFrames
        boundedGauges = @($boundedGaugeResults)
        workloadIdentityHashes = $workloadHashes
        resourcePlanGenerations = $framePlanGenerations
        commandGenerations = $frameCommandGenerations
        unstableOutputGroups = @($unstableOutputGroups)
    }
    captures = [ordered]@{
        outputDirectory = $captureDirectory
        requiredStages = $requiredCaptureStages
        desktopFinalStage = $desktopFinalCaptureStage
        motionSampleCount = $captureMotionSampleCount
        motionIntervalFrames = $captureMotionIntervalFrames
        desktopMotionHashes = @($desktopMotionHashes)
        boundaryEntryCount = $expectedBoundaryCaptureCount
        pixelThresholds = [ordered]@{
            minimumMovingVelocityMagnitude = 0.001
            maximumBloomRelativeEnergyDelta = $maxBloomRelativeEnergyDelta
            maximumBloomCentroidDistance = $maxBloomCentroidDistance
            maximumOcclusionParityFingerprintRmse = $maxFinalImageParityRmse
            topMarkerOracle = "top 111 rows contain red+blue dominant magenta sentinel pixels"
        }
        expectedEntryCount = $expectedCaptureEntryCount
        observedEntryCount = $captureEvidence.Count
        inventory = $captureInventoryPath
        entries = @($captureEvidence)
    }
    temporalScenarios = [ordered]@{
        oracle = "Per-eye 13c_MonoTsrReference is rendered through the mono TSR entry point against an isolated one-layer view of the same live inputs/history, then paired with multiview 14_TsrOutput."
        sequenceCompleteFrame = $temporalScenarioSequenceCompleteFrame
        stages = $temporalScenarioCaptureStages
        definitions = $temporalScenarioDefinitions
        thresholds = [ordered]@{
            maximumStaticVelocityMagnitude = $maxStaticVelocityMagnitude
            minimumMovingVelocityMagnitude = $minMovingVelocityMagnitude
            minimumDirectionalVelocityComponent = $minDirectionalVelocityComponent
            minimumStereoEyeSpecificRmse = $minStereoEyeSpecificRmse
            maximumBloomRelativeEnergyDelta = $maxBloomRelativeEnergyDelta
            maximumBloomCentroidDistance = $maxBloomCentroidDistance
            minimumStaticEdgeSharpnessRatioToMonoEquivalent = $minStaticEdgeSharpnessRatio
            minimumMovingEdgeSharpnessRatioToMonoEquivalent = $minMovingEdgeSharpnessRatio
            maximumTemporalConvergenceRmse = $maxTemporalConvergenceRmse
            minimumDisocclusionFingerprintRmse = $minDisocclusionFingerprintRmse
        }
        expectedCaptureCount = $expectedTemporalCaptureCount
        observedCaptureCount = $temporalScenarioCaptureLedger.Count
        inventory = $temporalCaptureInventoryPath
        results = @($temporalScenarioResults)
    }
    strictSpsFailureMatrix = [ordered]@{
        report = $StrictFailureReportPath
        passed = $null -ne $strictFailureReport -and [bool]$strictFailureReport.passed
        stages = $strictFailureStages
    }
    desktopRejectionEvidence = $desktopRejection
    subNativeCompanion = $subNativeCompanion
    logs = [ordered]@{
        rawFiles = $rawLogFiles
        copiedEngineLogs = @($copiedLogFiles)
        filteredText = $filteredLogPath
        filteredJson = $filteredLogJsonPath
        matchCount = $logMatches.Count
        rejectedMatchCount = $rejectedLogMatches.Count
        shutdownOnlyTeardownVuid = $shutdownOnlyDestroyDeviceVuid
        shutdownOnlyTeardownMatchCount = $shutdownOnlyTeardownMatches.Count
        shutdownOnlyTeardownMatches = @($shutdownOnlyTeardownMatches)
        shutdownMarker = $shutdownMarker
        postProcessFullResolutionContract = "896x1007; layers=2; viewMask=0x3; local render/viewport/scissor; UV [0,1]"
        expectedBloomExtents = $expectedBloomExtents
    }
    smokeSummary = $summaryPath
    failures = @($failures)
}

$result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $resultPath -Encoding UTF8

if ($failures.Count -gt 0) {
    Write-Host "Vulkan Phase 5.2.4b validation failed. Report=$resultPath"
    foreach ($failure in $failures) {
        Write-Host " - $failure"
    }
    exit 1
}

Write-Host "Vulkan Phase 5.2.4b validation passed. Report=$resultPath"
exit 0
