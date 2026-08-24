[CmdletBinding()]
param(
    [string] $OutputRoot = '',
    [switch] $SkipTests,
    [switch] $SkipLiveValidation,
    [switch] $NoBuild,
    [string] $SessionSuffix = "$PID"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($SessionSuffix -notmatch '^[A-Za-z0-9_-]+$') {
    throw 'SessionSuffix may contain only letters, digits, underscores, and hyphens.'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    & (Join-Path $repoRoot 'Tools\Limit-AgentValidation.ps1') -ReserveTaskRun | Out-Null
    $OutputRoot = Join-Path $repoRoot (
        'Build\_AgentValidation\{0}-poiyomi-parity' -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
else {
    if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
        $OutputRoot = Join-Path $repoRoot $OutputRoot
    }
    $OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
}

$testOutput = Join-Path $OutputRoot 'test-results'
$captureOutput = Join-Path $OutputRoot 'mcp-captures'
$mcpOutput = Join-Path $OutputRoot 'mcp-output'
$logOutput = Join-Path $OutputRoot 'logs'
$reportOutput = Join-Path $OutputRoot 'reports'
foreach ($path in @($testOutput, $captureOutput, $mcpOutput, $logOutput, $reportOutput)) {
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
}

$report = [ordered]@{
    formatVersion = 1
    startedUtc = [DateTime]::UtcNow.ToString('O')
    repository = $repoRoot
    outputRoot = $OutputRoot
    tests = [ordered]@{ skipped = [bool]$SkipTests; passed = $false }
    backends = @()
    passed = $false
}

function Write-JsonFile {
    param([string] $Path, [object] $Value)
    $json = $Value | ConvertTo-Json -Depth 30
    [System.IO.File]::WriteAllText($Path, $json, [System.Text.UTF8Encoding]::new($false))
}

function Invoke-EditorTool {
    param(
        [string] $Session,
        [string] $Name,
        [hashtable] $Arguments,
        [string] $OutputName,
        [switch] $AllowError
    )

    $json = & (Join-Path $repoRoot 'Tools\Invoke-Mcp.ps1') `
        -Session $Session `
        -Method 'tools/call' `
        -Params @{ name = $Name; arguments = $Arguments } `
        -TimeoutSec 90
    if ($LASTEXITCODE -ne 0) {
        throw "MCP tool '$Name' failed for session '$Session'."
    }
    [System.IO.File]::WriteAllLines(
        (Join-Path $mcpOutput $OutputName),
        [string[]]$json,
        [System.Text.UTF8Encoding]::new($false))
    $response = $json -join [Environment]::NewLine | ConvertFrom-Json
    if ($response.PSObject.Properties['error'] -and -not $AllowError) {
        throw "MCP tool '$Name' failed for session '$Session': $($response.error.message)"
    }
    return $response
}

try {
    if (-not $SkipTests) {
        $testArgs = @(
            'test',
            (Join-Path $repoRoot 'XREngine.UnitTests\XREngine.UnitTests.csproj'),
            '--filter', 'FullyQualifiedName~Poiyomi',
            '--logger', 'trx;LogFileName=poiyomi-parity.trx',
            '--results-directory', $testOutput,
            '--verbosity', 'minimal'
        )
        if ($NoBuild) {
            $testArgs += @('--no-build', '--no-restore')
        }
        & dotnet @testArgs
        if ($LASTEXITCODE -ne 0) {
            throw 'The Poiyomi automated validation suite failed.'
        }
        $report.tests.passed = $true
    }
    else {
        $report.tests.passed = $true
    }

    if (-not $SkipLiveValidation) {
        foreach ($backend in @('OpenGL', 'Vulkan')) {
            $session = 'poiyomi-parity-{0}-{1}' -f $backend.ToLowerInvariant(), $SessionSuffix
            $backendCaptures = Join-Path $captureOutput $backend
            [System.IO.Directory]::CreateDirectory($backendCaptures) | Out-Null
            $backendReport = [ordered]@{
                backend = $backend
                session = $session
                screenshots = @()
                pipelineTextures = @()
                validationErrors = @()
                textureStreaming = $null
                warmup = $null
                world = $null
                passed = $false
            }

            try {
                $startArgs = @{
                    Action = 'Start'
                    Name = $session
                    PermissionPolicy = 'AllowAll'
                    SessionEnvironment = @{
                        XRE_UNIT_TEST_RENDER_API = $backend
                        XRE_UNIT_TEST_WORLD_KIND = 'UberShader'
                    }
                }
                if ($NoBuild) {
                    $startArgs.NoBuild = $true
                }
                & (Join-Path $repoRoot 'Tools\Manage-McpEditorSession.ps1') @startArgs
                if ($LASTEXITCODE -ne 0) {
                    throw "Failed to start the $backend editor session."
                }

                $worldReady = $false
                for ($attempt = 0; $attempt -lt 60; $attempt++) {
                    $worldResponse = Invoke-EditorTool `
                        -Session $session `
                        -Name 'list_worlds' `
                        -Arguments @{} `
                        -OutputName ('{0}-world-{1}.json' -f $backend, $attempt) `
                        -AllowError
                    $worlds = @()
                    if ($worldResponse.PSObject.Properties['result']) {
                        $worlds = @($worldResponse.result.structuredContent.worlds)
                    }
                    if ($worlds.Count -eq 1 -and $worlds[0].name -eq 'Uber Shader World') {
                        $backendReport.world = $worlds[0]
                        $worldReady = $true
                        break
                    }
                    Start-Sleep -Milliseconds 500
                }
                if (-not $worldReady) {
                    throw "$backend did not activate the Uber Shader World."
                }

                $warmupReady = $false
                $warmupCaptures = Join-Path $backendCaptures 'warmup'
                for ($attempt = 0; $attempt -lt 180; $attempt++) {
                    $warmup = Invoke-EditorTool `
                        -Session $session `
                        -Name 'capture_render_pipeline_texture' `
                        -Arguments @{
                            texture_name = 'FinalPostProcessOutputTexture'
                            output_dir = $warmupCaptures
                        } `
                        -OutputName ('{0}-warmup-{1}.json' -f $backend, $attempt)
                    $warmupContent = $warmup.result.structuredContent
                    if ($warmupContent.stats.nonFiniteSamples -eq 0 -and
                        $warmupContent.stats.maxRgb -gt 0.01 -and
                        $warmupContent.stats.averageRgb -gt 0.001) {
                        $backendReport.warmup = $warmupContent
                        $warmupReady = $true
                        break
                    }
                    Start-Sleep -Seconds 1
                }
                if (-not $warmupReady) {
                    throw "$backend did not produce a finite, non-empty final target during warmup."
                }

                Invoke-EditorTool `
                    -Session $session `
                    -Name 'capture_viewport_screenshot' `
                    -Arguments @{ output_dir = (Join-Path $backendCaptures 'prime') } `
                    -OutputName "$backend-prime.json" | Out-Null

                $cameraPositions = @(
                    @(0.0, 2.0, 0.0),
                    @(4.0, 2.0, -1.0),
                    @(-4.0, 3.0, -1.0)
                )
                for ($index = 0; $index -lt $cameraPositions.Count; $index++) {
                    $position = $cameraPositions[$index]
                    Invoke-EditorTool `
                        -Session $session `
                        -Name 'set_editor_camera_view' `
                        -Arguments @{
                            position_x = $position[0]
                            position_y = $position[1]
                            position_z = $position[2]
                            look_at_x = 0.0
                            look_at_y = 1.25
                            look_at_z = -6.0
                            duration = 0.0
                        } `
                        -OutputName ('{0}-camera-{1}.json' -f $backend, $index) | Out-Null
                    $capture = Invoke-EditorTool `
                        -Session $session `
                        -Name 'capture_viewport_screenshot' `
                        -Arguments @{ output_dir = $backendCaptures } `
                        -OutputName ('{0}-capture-{1}.json' -f $backend, $index)
                    $backendReport.screenshots += $capture.result.structuredContent.path
                    $pipelineCapture = Invoke-EditorTool `
                        -Session $session `
                        -Name 'capture_render_pipeline_texture' `
                        -Arguments @{
                            texture_name = 'FinalPostProcessOutputTexture'
                            output_dir = (Join-Path $backendCaptures 'final-pipeline')
                        } `
                        -OutputName ('{0}-pipeline-{1}.json' -f $backend, $index)
                    $backendReport.pipelineTextures += $pipelineCapture.result.structuredContent
                }

                Invoke-EditorTool `
                    -Session $session `
                    -Name 'dump_cpu_frame_profile' `
                    -Arguments @{} `
                    -OutputName "$backend-cpu-profile.json" | Out-Null
                Invoke-EditorTool `
                    -Session $session `
                    -Name 'dump_gpu_render_pipeline_profile' `
                    -Arguments @{ all_pipelines = $true } `
                    -OutputName "$backend-gpu-profile.json" | Out-Null
                Invoke-EditorTool `
                    -Session $session `
                    -Name 'get_render_profiler_stats' `
                    -Arguments @{} `
                    -OutputName "$backend-render-stats.json" | Out-Null
                Invoke-EditorTool `
                    -Session $session `
                    -Name 'list_render_pipeline_resources' `
                    -Arguments @{} `
                    -OutputName "$backend-pipeline-resources.json" | Out-Null
                $textureStreaming = Invoke-EditorTool `
                    -Session $session `
                    -Name 'list_texture_streaming_textures' `
                    -Arguments @{ max_results = 128 } `
                    -OutputName "$backend-texture-streaming.json"
                $backendReport.textureStreaming = $textureStreaming.result.structuredContent
            }
            finally {
                & (Join-Path $repoRoot 'Tools\Manage-McpEditorSession.ps1') `
                    Stop -Name $session -ErrorAction SilentlyContinue
            }

            $sessionsRoot = Join-Path $repoRoot 'Build\_AgentValidation\00000000-000000-shared\mcp-sessions'
            $sessionManifest = Get-ChildItem -LiteralPath $sessionsRoot -Filter session.json -File -Recurse |
                Where-Object {
                    try { [string](Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json).name -ceq $session }
                    catch { $false }
                } |
                Select-Object -First 1
            $sessionLogs = if ($null -ne $sessionManifest) {
                Join-Path $sessionManifest.DirectoryName 'logs'
            }
            else {
                $null
            }
            if ($null -ne $sessionLogs -and (Test-Path -LiteralPath $sessionLogs)) {
                Copy-Item -LiteralPath $sessionLogs -Destination (Join-Path $logOutput $backend) -Recurse -Force
                $backendReport.validationErrors = @(
                    Get-ChildItem -LiteralPath $sessionLogs -File -Recurse |
                        Select-String -Pattern 'VUID-|validation error|GL_INVALID_|shader.*failed|WORKER_COMPILE_FAILED|resource-lifetime error' |
                        Where-Object { $_.Line -notmatch 'vkDestroyDevice-device-05137' } |
                        ForEach-Object { '{0}:{1}: {2}' -f $_.Path, $_.LineNumber, $_.Line.Trim() }
                )
            }

            if ($backendReport.screenshots.Count -ne 3) {
                throw "$backend did not produce all three required camera captures."
            }
            if ($backendReport.pipelineTextures.Count -ne 3) {
                throw "$backend did not produce all three final-pipeline captures."
            }
            $invalidPipelineTextures = @(
                $backendReport.pipelineTextures |
                    Where-Object {
                        $_.stats.nonFiniteSamples -ne 0 -or
                        $_.stats.maxRgb -le 0.01 -or
                        $_.stats.averageRgb -le 0.001
                    }
            )
            if ($invalidPipelineTextures.Count -ne 0) {
                throw "$backend produced an empty or non-finite final-pipeline capture."
            }
            $invalidTextures = @(
                $backendReport.textureStreaming.textures |
                    Where-Object { -not $_.preview_ready -or $_.has_validation_failure }
            )
            if ($invalidTextures.Count -ne 0) {
                throw "$backend imported texture streaming did not publish clean previews."
            }
            if ($backendReport.validationErrors.Count -ne 0) {
                throw "$backend emitted validation, resource-lifetime, or shader errors."
            }
            $backendReport.passed = $true
            $report.backends += $backendReport
        }
    }

    $report.passed = $report.tests.passed -and (
        $SkipLiveValidation -or
        ($report.backends.Count -eq 2 -and
         @($report.backends | Where-Object { -not $_.passed }).Count -eq 0))
}
finally {
    $report.completedUtc = [DateTime]::UtcNow.ToString('O')
    Write-JsonFile (Join-Path $reportOutput 'poiyomi-parity-validation.json') $report
}

if (-not $report.passed) {
    throw "Poiyomi parity validation failed. See '$OutputRoot'."
}

Write-Host "Poiyomi parity validation passed. Evidence: $OutputRoot"
