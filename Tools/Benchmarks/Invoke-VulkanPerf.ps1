param(
    [ValidateSet('Quick', 'Compare', 'Gate')]
    [string]$Preset = 'Quick',
    [string[]]$Cohorts = @(),
    [string]$BaselinePath = '',
    [switch]$AcceptBaseline,
    [switch]$NoBuild,
    [string]$ContractPath = 'XREngine.Benchmarks\VulkanPerformance\vulkan-performance-cohorts.json',
    [string]$RunRoot = '',
    [string]$GpuClockPolicy = 'Unspecified'
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$contractFullPath = [System.IO.Path]::GetFullPath(
    $(if ([System.IO.Path]::IsPathRooted($ContractPath)) {
        $ContractPath
    } else {
        Join-Path $repoRoot $ContractPath
    }))
if (-not (Test-Path -LiteralPath $contractFullPath)) {
    throw "Vulkan performance contract not found: $contractFullPath"
}

$contract = Get-Content -Raw -LiteralPath $contractFullPath | ConvertFrom-Json
$presetDefinition = $contract.presets.$Preset
if ($null -eq $presetDefinition) {
    throw "Preset '$Preset' is not defined in $contractFullPath"
}

$requestedCohorts = @($Cohorts | ForEach-Object {
    [string]$_ -split ','
} | ForEach-Object {
    $_.Trim()
} | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_)
})
$explicitCohortSelection = $requestedCohorts.Count -gt 0

if ($requestedCohorts.Count -eq 0) {
    $requestedCohorts = switch ($Preset) {
        'Quick' { @('desktop-deferred-static') }
        'Compare' { @($contract.cohorts | Where-Object { $_.lane -eq 'Desktop' } | ForEach-Object { $_.id }) }
        'Gate' { @($contract.cohorts | Where-Object { $_.gate } | ForEach-Object { $_.id }) }
    }
}

$selectedCohorts = [System.Collections.Generic.List[object]]::new()
foreach ($cohortId in $requestedCohorts) {
    $matches = @($contract.cohorts | Where-Object { $_.id -eq $cohortId })
    if ($matches.Count -ne 1) {
        throw "Unknown or duplicate Vulkan performance cohort '$cohortId'."
    }
    $selectedCohorts.Add($matches[0])
}

if ($Preset -eq 'Quick' -and $selectedCohorts.Count -ne 1) {
    throw 'Quick requires exactly one selected cohort.'
}
if ($AcceptBaseline -and [string]::IsNullOrWhiteSpace($BaselinePath)) {
    throw 'AcceptBaseline requires BaselinePath.'
}
if ($Preset -ne 'Quick' -and -not $AcceptBaseline -and [string]::IsNullOrWhiteSpace($BaselinePath)) {
    throw "$Preset requires BaselinePath or AcceptBaseline."
}

function Resolve-RepositoryPath {
    param([string]$Path)

    $resolved = [System.IO.Path]::GetFullPath(
        $(if ([System.IO.Path]::IsPathRooted($Path)) {
            $Path
        } else {
            Join-Path $repoRoot $Path
        }))
    return $resolved
}

function Assert-PathInsideRepository {
    param([string]$Path, [string]$Purpose)

    $repoWithSeparator = $repoRoot.TrimEnd('\') + '\'
    if (-not $Path.StartsWith($repoWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Purpose must remain inside the repository: $Path"
    }
}

function Persist-CohortFrameStreams {
    param(
        [string]$SummaryPath,
        [string]$CohortOutput
    )

    $summary = Get-Content -Raw -LiteralPath $SummaryPath | ConvertFrom-Json
    $summaryIsArray = $summary -is [Array]
    $rows = @($summary)
    $durableLogDirectories = [System.Collections.Generic.List[string]]::new()

    foreach ($row in $rows) {
        $sourceLogDirectory = [System.IO.Path]::GetFullPath([string]$row.LogDir)
        Assert-PathInsideRepository -Path $sourceLogDirectory -Purpose 'Profiler frame-stream source'
        $sourceFrameStream = Join-Path $sourceLogDirectory 'profiler-render-stats.ndjson'
        if (-not (Test-Path -LiteralPath $sourceFrameStream -PathType Leaf)) {
            throw "Profiler frame stream was not available for persistence: $sourceFrameStream"
        }
        $sourceCaptureManifest = Join-Path $sourceLogDirectory 'profiler-capture-manifest.json'
        if (-not (Test-Path -LiteralPath $sourceCaptureManifest -PathType Leaf)) {
            throw "Profiler capture manifest was not available for persistence: $sourceCaptureManifest"
        }

        $repetition = [int]$row.Repetition
        $durableLogDirectory = Join-Path $CohortOutput "frame-streams\repetition-$repetition"
        $durableLogDirectory = [System.IO.Path]::GetFullPath($durableLogDirectory)
        Assert-PathInsideRepository -Path $durableLogDirectory -Purpose 'Durable profiler frame-stream directory'
        New-Item -ItemType Directory -Path $durableLogDirectory -Force | Out-Null
        Copy-Item -LiteralPath $sourceFrameStream -Destination (
            Join-Path $durableLogDirectory 'profiler-render-stats.ndjson') -Force
        Copy-Item -LiteralPath $sourceCaptureManifest -Destination (
            Join-Path $durableLogDirectory 'profiler-capture-manifest.json') -Force

        $row | Add-Member -NotePropertyName SourceLogDir -NotePropertyValue $sourceLogDirectory -Force
        $row.LogDir = $durableLogDirectory
        $durableLogDirectories.Add($durableLogDirectory)
    }

    $persistedSummary = if ($summaryIsArray) { @($rows) } else { $rows[0] }
    $persistedSummary |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $SummaryPath -Encoding UTF8
    $durableLogDirectories |
        Set-Content -LiteralPath (Join-Path $CohortOutput 'run-logdirs.txt') -Encoding UTF8
}

function Limit-AgentValidationRuns {
    $validationRoot = Join-Path $repoRoot 'Build\_AgentValidation'
    New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
    $directories = @(Get-ChildItem -LiteralPath $validationRoot -Directory |
        Sort-Object LastWriteTimeUtc)
    while ($directories.Count -ge 10) {
        $owned = @($directories | Where-Object { $_.Name -match '^\d{8}-\d{6}-vulkan-perf-' })
        if ($owned.Count -eq 0) {
            throw "Build\_AgentValidation already has $($directories.Count) run roots and none are owned by Invoke-VulkanPerf. Remove stale evidence before starting another run."
        }

        $target = [System.IO.Path]::GetFullPath($owned[0].FullName)
        Assert-PathInsideRepository -Path $target -Purpose 'Retention target'
        Remove-Item -LiteralPath $target -Recurse -Force
        $directories = @(Get-ChildItem -LiteralPath $validationRoot -Directory |
            Sort-Object LastWriteTimeUtc)
    }
}

if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    Limit-AgentValidationRuns
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $RunRoot = "Build\_AgentValidation\$stamp-vulkan-perf-$($Preset.ToLowerInvariant())"
}
$runFullPath = Resolve-RepositoryPath -Path $RunRoot
Assert-PathInsideRepository -Path $runFullPath -Purpose 'RunRoot'
$reportsPath = Join-Path $runFullPath 'reports'
$logsPath = Join-Path $runFullPath 'logs'
New-Item -ItemType Directory -Path $reportsPath -Force | Out-Null
New-Item -ItemType Directory -Path $logsPath -Force | Out-Null

$editorProject = Join-Path $repoRoot 'XREngine.Editor\XREngine.Editor.csproj'
$benchmarkProject = Join-Path $repoRoot 'XREngine.Benchmarks\XREngine.Benchmarks.csproj'
$editorExe = Join-Path $repoRoot 'Build\Editor\Release\AnyCPU\Release\net10.0-windows7.0\XREngine.Editor.exe'
if (-not $NoBuild) {
    $editorArtifacts = Join-Path $runFullPath 'temp-build\editor-artifacts'
    & dotnet build $editorProject `
        --configuration Release `
        --artifacts-path $editorArtifacts `
        -p:Platform=AnyCPU `
        -p:RestoreIgnoreFailedSources=true `
        -p:NuGetAudit=false `
        -p:XREngineUseExistingNativeBridges=true `
        -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "Release editor build failed with exit code $LASTEXITCODE."
    }
    $editorCandidates = @(
        Get-ChildItem -LiteralPath (Join-Path $editorArtifacts 'bin\XREngine.Editor') `
            -Recurse `
            -Filter 'XREngine.Editor.exe' `
            -File
    )
    if ($editorCandidates.Count -ne 1) {
        throw "Expected one isolated Release editor executable under $editorArtifacts but found $($editorCandidates.Count)."
    }
    $editorExe = $editorCandidates[0].FullName

    & dotnet build $benchmarkProject -c Release `
        -p:VulkanPerformanceToolOnly=true `
        -p:BuildProjectReferences=false `
        --no-dependencies `
        --no-restore `
        -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "Release benchmark build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $editorExe)) {
    throw "Release editor executable not found: $editorExe"
}

$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Could not resolve the source commit.'
}
$dirtyWorktree = @(& git -C $repoRoot status --porcelain).Count -gt 0
$executableSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $editorExe).Hash
$gpuName = ''
$gpuDriver = ''
$nvidiaSmi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
if ($null -ne $nvidiaSmi) {
    $nvidiaRows = @(& $nvidiaSmi.Source --query-gpu=name,driver_version --format=csv,noheader 2>$null)
    if ($LASTEXITCODE -eq 0 -and $nvidiaRows.Count -gt 0) {
        $primaryNvidia = ([string]$nvidiaRows[0]).Split(',', 2)
        $gpuName = $primaryNvidia[0].Trim()
        $gpuDriver = $(if ($primaryNvidia.Count -gt 1) { $primaryNvidia[1].Trim() } else { '' })
    }
}
if ([string]::IsNullOrWhiteSpace($gpuName)) {
    $videoControllers = @(Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue)
    $gpuName = ($videoControllers | ForEach-Object { $_.Name } | Where-Object { $_ } | Sort-Object -Unique) -join '; '
    $gpuDriver = ($videoControllers | ForEach-Object { $_.DriverVersion } | Where-Object { $_ } | Sort-Object -Unique) -join '; '
}
if ([string]::IsNullOrWhiteSpace($gpuName)) {
    $gpuName = 'Unavailable'
    $gpuDriver = 'Unavailable'
}
$displayMode = 'Windowed 1920x1080, VSync off'

$measureScript = Join-Path $repoRoot 'Tools\Measure-GameLoopRenderPipeline.ps1'
$runCohorts = [System.Collections.Generic.List[object]]::new()
$captureFailures = [System.Collections.Generic.List[object]]::new()
$environmentNames = @(
    'XR_RUNTIME_JSON',
    'XRE_UNIT_TEST_WORLD_SETTINGS_PATH',
    'XRE_UNIT_TEST_RENDER_API',
    'XRE_UNIT_TEST_VR_FOVEATION_MODE',
    'XRE_UNIT_TEST_VR_FOVEATION_QUALITY_PRESET',
    'XRE_UNIT_TEST_VR_FOVEATION_REQUIRE_REQUESTED',
    'XRE_UNIT_TEST_VR_VIEW_RENDER_MODE',
    'XRE_UNIT_TEST_RENDER_WINDOWS_WHILE_IN_VR',
    'XRE_UNIT_TEST_ALLOW_DESKTOP_EDITING_IN_VR',
    'XRE_GPU_DRIVEN_VALIDATION_CAPACITY_MULTIPLIER',
    'XRE_GPU_DRIVEN_VALIDATION_CAPACITY_FLOOR',
    'XRE_GPU_DRIVER'
)
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

$requiresMonado = @($selectedCohorts | Where-Object {
    [string]$_.vrMode -eq 'MonadoOpenXR'
}).Count -gt 0
$monadoServiceOwned = $false
$monadoServiceMarker = Join-Path $runFullPath 'mcp-output\monado-service-marker.json'
$monadoServiceScript = Join-Path $repoRoot 'Tools\OpenXR\Start-MonadoService.ps1'

try {
    if ($requiresMonado) {
        if (-not (Test-Path -LiteralPath $monadoServiceScript -PathType Leaf)) {
            throw "Monado service manager was not found: $monadoServiceScript"
        }

        $monadoStart = & $monadoServiceScript `
            -MarkerPath $monadoServiceMarker `
            -LogDirectory (Join-Path $logsPath 'monado-service') `
            -SimulatedHmdPoseMode stationary
        $monadoStart |
            ConvertTo-Json -Depth 6 |
            Set-Content -LiteralPath (Join-Path $reportsPath 'monado-service-start.json') -Encoding UTF8

        if (-not [bool]$monadoStart.OwnedByRunner) {
            throw "Canonical RVC capture requires a runner-owned stationary Monado service. $($monadoStart.Reason)"
        }

        $monadoServiceOwned = $true
        [Environment]::SetEnvironmentVariable(
            'XR_RUNTIME_JSON',
            [string]$monadoStart.RuntimeJson,
            'Process')
    }

    foreach ($cohort in $selectedCohorts) {
        $settingsPath = Resolve-RepositoryPath -Path ([string]$cohort.settingsPath)
        Assert-PathInsideRepository -Path $settingsPath -Purpose 'Cohort settings path'
        if (-not (Test-Path -LiteralPath $settingsPath)) {
            throw "Cohort settings file not found: $settingsPath"
        }

        $cohortOutput = Join-Path $reportsPath ([string]$cohort.id)
        New-Item -ItemType Directory -Path $cohortOutput -Force | Out-Null

        [Environment]::SetEnvironmentVariable('XRE_UNIT_TEST_WORLD_SETTINGS_PATH', $settingsPath, 'Process')
        [Environment]::SetEnvironmentVariable('XRE_GPU_DRIVER', $gpuDriver, 'Process')
        [Environment]::SetEnvironmentVariable('XRE_UNIT_TEST_RENDER_API', 'Vulkan', 'Process')
        [Environment]::SetEnvironmentVariable('XRE_UNIT_TEST_VR_FOVEATION_MODE', [string]$cohort.foveationMode, 'Process')
        [Environment]::SetEnvironmentVariable('XRE_UNIT_TEST_VR_FOVEATION_QUALITY_PRESET', 'Balanced', 'Process')
        [Environment]::SetEnvironmentVariable(
            'XRE_UNIT_TEST_VR_FOVEATION_REQUIRE_REQUESTED',
            $(if ([bool]$cohort.requireFoveation) { '1' } else { '0' }),
            'Process')
        [Environment]::SetEnvironmentVariable('XRE_UNIT_TEST_VR_VIEW_RENDER_MODE', 'ParallelCommandBufferRecording', 'Process')
        [Environment]::SetEnvironmentVariable('XRE_UNIT_TEST_RENDER_WINDOWS_WHILE_IN_VR', '1', 'Process')
        [Environment]::SetEnvironmentVariable('XRE_UNIT_TEST_ALLOW_DESKTOP_EDITING_IN_VR', '1', 'Process')
        [Environment]::SetEnvironmentVariable(
            'XRE_GPU_DRIVEN_VALIDATION_CAPACITY_MULTIPLIER',
            $(if ($null -ne $cohort.gpuDrivenValidationCapacityMultiplier) {
                [string]$cohort.gpuDrivenValidationCapacityMultiplier
            } else {
                '1'
            }),
            'Process')
        [Environment]::SetEnvironmentVariable(
            'XRE_GPU_DRIVEN_VALIDATION_CAPACITY_FLOOR',
            $(if ($null -ne $cohort.gpuDrivenValidationCapacityFloor) {
                [string]$cohort.gpuDrivenValidationCapacityFloor
            } else {
                '0'
            }),
            'Process')

        $measureArguments = @{
            WarmupSec = [int]$presetDefinition.warmupSeconds
            CaptureSec = [int]$presetDefinition.captureSeconds
            Repetitions = [int]$presetDefinition.repetitions
            Strategies = @([string]$cohort.strategy)
            Configuration = 'Release'
            CacheMode = 'Warm'
            ZeroReadbackMaterialDrawPath = [string]$cohort.zeroReadbackMaterialDrawPath
            UnitTestingWorldSettingsPath = $settingsPath
            ProfileScene = [string]$cohort.scene
            ProfileCamera = [string]$cohort.camera
            ProfileLights = [string]$cohort.lights
            ProfileViewport = [string]$cohort.viewport
            RenderScale = [string]$cohort.renderScale
            GpuClockPolicy = $GpuClockPolicy
            TargetRefreshHz = $(if ($cohort.lane -eq 'VulkanRvc') { 120.0 } else { 200.0 })
            NoClearCachesBetweenVariants = $true
            NoP3Logging = $true
            RetainedRunCount = 20
            RunLabel = "vulkan-perf-$($Preset.ToLowerInvariant())-$($cohort.id)"
            OutputDirectory = $cohortOutput
            EditorExecutablePath = $editorExe
            ProfileMode = [string]$presetDefinition.profileMode
            DisableMcpDiagnostics = $true
            UnitTestVrMode = [string]$cohort.vrMode
            VulkanRenderTargetMode = 'DynamicRendering'
            VulkanPrimaryReuse = $(if ([string]::IsNullOrWhiteSpace(
                    [string]$cohort.vulkanPrimaryReuse)) {
                'Enabled'
            } else {
                [string]$cohort.vulkanPrimaryReuse
            })
            VulkanCommandChains = 'Enabled'
            VulkanParallelCommandChainRecording = $(if ([string]::IsNullOrWhiteSpace(
                    [string]$cohort.vulkanParallelCommandChainRecording)) {
                'Enabled'
            } else {
                [string]$cohort.vulkanParallelCommandChainRecording
            })
            VulkanParallelSecondaryRecording = 'Enabled'
            OcclusionCullingMode = 'Disabled'
            VulkanDiagnosticPreset = 'Off'
        }
        if ([bool]$cohort.forceCommandChainRerecord) {
            $measureArguments.VulkanCommandChainBenchmarkForceRerecord = $true
        }
        if ([int]$cohort.minimumGpuSceneCommandCount -gt 0) {
            $measureArguments.MinSteadyStateGpuSceneCommandCount =
                [int]$cohort.minimumGpuSceneCommandCount
        }
        if ([double]$cohort.minimumPrimaryReuseRatio -gt 0) {
            $measureArguments.FailOnSteadyStateCommandBufferChurn = $true
            $measureArguments.UseEligiblePrimaryReuseRatio = $true
            $measureArguments.MinSteadyStateCommandBufferCleanReuseRatio =
                [double]$cohort.minimumPrimaryReuseRatio
        }
        if ($Preset -eq 'Gate') {
            $measureArguments.FailOnSteadyStateBindingFallback = $true
        }

        try {
            & $measureScript @measureArguments
        } catch {
            $captureFailures.Add([pscustomobject]@{
                Cohort = [string]$cohort.id
                Message = $_.Exception.Message
            })
        }

        $summaryPath = Join-Path $cohortOutput 'summary.json'
        if (Test-Path -LiteralPath $summaryPath) {
            Persist-CohortFrameStreams `
                -SummaryPath $summaryPath `
                -CohortOutput $cohortOutput
            $runCohorts.Add([pscustomobject]@{
                Id = [string]$cohort.id
                SummaryPath = $summaryPath
                SettingsPath = $settingsPath
                SettingsSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $settingsPath).Hash
            })
        } else {
            $captureFailures.Add([pscustomobject]@{
                Cohort = [string]$cohort.id
                Message = "Capture produced no summary at $summaryPath"
            })
        }
    }
} finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
    }

    if ($monadoServiceOwned) {
        & $monadoServiceScript -MarkerPath $monadoServiceMarker -Stop |
            ConvertTo-Json -Depth 6 |
            Set-Content -LiteralPath (Join-Path $reportsPath 'monado-service-stop.json') -Encoding UTF8
    }
}

$captureFailuresPath = Join-Path $reportsPath 'capture-failures.json'
$captureFailures | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $captureFailuresPath -Encoding UTF8
$runManifest = [ordered]@{
    SchemaVersion = 1
    Preset = $Preset
    PromotionEligible = [bool]$presetDefinition.promotionEligible
    ProfileMode = [string]$presetDefinition.profileMode
    GateScope = $(if ($Preset -eq 'Gate' -and $explicitCohortSelection) {
        'Selected'
    } else {
        'Full'
    })
    ContractPath = $contractFullPath
    SourceCommit = $sourceCommit
    DirtyWorktree = $dirtyWorktree
    ExecutableSha256 = $executableSha256
    OperatingSystem = [System.Environment]::OSVersion.VersionString
    MachineName = [System.Environment]::MachineName
    GpuName = $gpuName
    GpuDriver = $gpuDriver
    DisplayMode = $displayMode
    CreatedUtc = [datetime]::UtcNow.ToString('O')
    Cohorts = @($runCohorts)
    CaptureFailures = @($captureFailures)
}
$runManifestPath = Join-Path $reportsPath 'run-manifest.json'
$runManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $runManifestPath -Encoding UTF8
if ($runCohorts.Count -eq 0) {
    Write-Host "Run manifest: $runManifestPath"
    Write-Host "Capture failures: $captureFailuresPath" -ForegroundColor Yellow
    exit 1
}

$evaluationPath = Join-Path $reportsPath 'evaluation.json'
$evaluationArguments = @(
    'run',
    '-c', 'Release',
    '-p:VulkanPerformanceToolOnly=true',
    '-p:BuildProjectReferences=false',
    '--no-build',
    '--project', $benchmarkProject
)
$evaluationArguments += @(
    '--',
    '--vulkan-perf',
    '--contract', $contractFullPath,
    '--run-manifest', $runManifestPath,
    '--out', $evaluationPath
)

$baselineFullPath = ''
if (-not [string]::IsNullOrWhiteSpace($BaselinePath)) {
    $baselineFullPath = Resolve-RepositoryPath -Path $BaselinePath
    Assert-PathInsideRepository -Path $baselineFullPath -Purpose 'BaselinePath'
    $evaluationArguments += @('--baseline', $baselineFullPath)
}
if ($AcceptBaseline) {
    $evaluationArguments += '--accept-baseline'
}

& dotnet @evaluationArguments
$evaluationExitCode = $LASTEXITCODE

Write-Host "Run manifest: $runManifestPath"
Write-Host "Evaluation: $evaluationPath"
if ($captureFailures.Count -gt 0) {
    Write-Host "Capture failures: $captureFailuresPath" -ForegroundColor Yellow
    exit 1
}
exit $evaluationExitCode
