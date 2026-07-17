[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateRange(0, 300)]
    [int]$WarmupSec = 15,

    [ValidateRange(1, 300)]
    [int]$CaptureSec = 15,

    [ValidateRange(1, 300)]
    [int]$ScreenshotWarmupSec = 15,

    [ValidateRange(10, 600)]
    [int]$StartupTimeoutSec = 120,

    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$editorExe = Join-Path $repoRoot "Build\Editor\$Configuration\AnyCPU\$Configuration\net10.0-windows7.0\XREngine.Editor.exe"
$measureScript = Join-Path $PSScriptRoot 'Measure-GameLoopRenderPipeline.ps1'
$captureScript = Join-Path $PSScriptRoot 'Capture-EditorWindow.ps1'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputRoot = Join-Path $repoRoot "Build\_AgentValidation\$stamp-deploy-gpu-validation"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot $OutputRoot
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$screenshots = Join-Path $OutputRoot 'screenshots'
$reports = Join-Path $OutputRoot 'reports'
[System.IO.Directory]::CreateDirectory($screenshots) | Out-Null
[System.IO.Directory]::CreateDirectory($reports) | Out-Null

foreach ($requiredPath in @($editorExe, $measureScript, $captureScript)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required GPU-validation input was not found: $requiredPath"
    }
}

$environmentNames = @(
    'XRE_UNIT_TEST_RENDER_API',
    'XRE_WORLD_MODE',
    'XRE_WINDOW_TITLE',
    'XRE_VULKAN_VALIDATION',
    'XRE_VULKAN_DIAGNOSTIC_PRESET',
    'XRE_VULKAN_COMMAND_BUFFER_LABELS'
)
$previousEnvironment = @{}
foreach ($name in $environmentNames) {
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Set-ProcessEnvironmentValue {
    param(
        [Parameter(Mandatory)][string]$Name,
        [AllowNull()][string]$Value
    )

    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

function Assert-ScreenshotHasRenderedContent {
    param([Parameter(Mandatory)][string]$Path)

    Add-Type -AssemblyName System.Drawing
    $bitmap = [System.Drawing.Bitmap]::FromFile($Path)
    try {
        if ($bitmap.Width -lt 64 -or $bitmap.Height -lt 64) {
            throw "Screenshot is unexpectedly small: $($bitmap.Width)x$($bitmap.Height)."
        }

        $stepX = [Math]::Max(1, [int]($bitmap.Width / 64))
        $stepY = [Math]::Max(1, [int]($bitmap.Height / 64))
        $maximumLuminance = 0
        $colors = [System.Collections.Generic.HashSet[int]]::new()
        for ($y = 0; $y -lt $bitmap.Height; $y += $stepY) {
            for ($x = 0; $x -lt $bitmap.Width; $x += $stepX) {
                $pixel = $bitmap.GetPixel($x, $y)
                $luminance = [int]$pixel.R + [int]$pixel.G + [int]$pixel.B
                $maximumLuminance = [Math]::Max($maximumLuminance, $luminance)
                $colors.Add($pixel.ToArgb()) | Out-Null
            }
        }

        if ($maximumLuminance -le 8 -or $colors.Count -lt 8) {
            throw "Screenshot appears blank or unrendered (max luminance=$maximumLuminance, sampled colors=$($colors.Count))."
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

function Capture-BackendScreenshot {
    param([Parameter(Mandatory)][ValidateSet('OpenGL', 'Vulkan')][string]$Backend)

    $title = "XRE Deploy GPU Smoke $Backend"
    $path = Join-Path $screenshots "$($Backend.ToLowerInvariant())-smoke.png"
    Set-ProcessEnvironmentValue -Name 'XRE_UNIT_TEST_RENDER_API' -Value $Backend
    Set-ProcessEnvironmentValue -Name 'XRE_WORLD_MODE' -Value 'UnitTesting'
    Set-ProcessEnvironmentValue -Name 'XRE_WINDOW_TITLE' -Value $title
    Set-ProcessEnvironmentValue -Name 'XRE_VULKAN_VALIDATION' -Value $(if ($Backend -eq 'Vulkan') { '1' } else { $null })
    Set-ProcessEnvironmentValue -Name 'XRE_VULKAN_DIAGNOSTIC_PRESET' -Value $(if ($Backend -eq 'Vulkan') { 'StandardValidation' } else { $null })
    Set-ProcessEnvironmentValue -Name 'XRE_VULKAN_COMMAND_BUFFER_LABELS' -Value $(if ($Backend -eq 'Vulkan') { '1' } else { $null })

    $process = Start-Process -FilePath $editorExe -WorkingDirectory $repoRoot -ArgumentList '--unit-testing' -PassThru
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSec)
        do {
            Start-Sleep -Seconds 1
            $process.Refresh()
            if ($process.HasExited) {
                throw "$Backend editor smoke process exited during startup with code $($process.ExitCode)."
            }
        } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)

        if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
            throw "$Backend editor did not create a visible window within $StartupTimeoutSec seconds. Ensure the self-hosted runner is running in an interactive desktop session."
        }

        Start-Sleep -Seconds $ScreenshotWarmupSec
        $global:LASTEXITCODE = 0
        & $captureScript -OutPath $path -ProcessName 'XREngine.Editor' -WindowTitlePattern $title
        if ($LASTEXITCODE -ne 0) {
            throw "$Backend window capture failed with exit code $LASTEXITCODE."
        }

        Assert-ScreenshotHasRenderedContent -Path $path
        return $path
    }
    finally {
        if (-not $process.HasExited) {
            $process.CloseMainWindow() | Out-Null
            if (-not $process.WaitForExit(10000)) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                $process.WaitForExit(5000) | Out-Null
            }
        }
    }
}

$result = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTimeOffset]::UtcNow
    configuration = $Configuration
    outputRoot = $OutputRoot
    passed = $false
    screenshots = @()
    failure = $null
}
$resultPath = Join-Path $reports 'deploy-gpu-validation.json'

Push-Location $repoRoot
try {
    $result.screenshots += Capture-BackendScreenshot -Backend 'Vulkan'
    $result.screenshots += Capture-BackendScreenshot -Backend 'OpenGL'

    Set-ProcessEnvironmentValue -Name 'XRE_UNIT_TEST_RENDER_API' -Value 'Vulkan'
    & $measureScript `
        -WarmupSec $WarmupSec `
        -CaptureSec $CaptureSec `
        -Repetitions 1 `
        -Strategies 'CpuDirect,GpuIndirectZeroReadback' `
        -Configuration $Configuration `
        -CacheMode Warm `
        -UnitTestVrMode Desktop `
        -VulkanDiagnosticPreset StandardValidation `
        -VulkanCommandBufferLabels `
        -VulkanValidation `
        -FailOnSteadyStateResourceChurn `
        -RetainedRunCount 4 `
        -RunLabel 'deploy-vulkan'

    Set-ProcessEnvironmentValue -Name 'XRE_UNIT_TEST_RENDER_API' -Value 'OpenGL'
    & $measureScript `
        -WarmupSec $WarmupSec `
        -CaptureSec $CaptureSec `
        -Repetitions 1 `
        -Strategies 'CpuDirect' `
        -Configuration $Configuration `
        -CacheMode Warm `
        -UnitTestVrMode Desktop `
        -RetainedRunCount 4 `
        -RunLabel 'deploy-opengl'

    $result.passed = $true
}
catch {
    $result.failure = [ordered]@{
        message = $_.Exception.Message
        exceptionType = $_.Exception.GetType().FullName
        scriptStackTrace = $_.ScriptStackTrace
    }
    throw
}
finally {
    $result.capturedAtUtc = [DateTimeOffset]::UtcNow
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8

    foreach ($name in $environmentNames) {
        Set-ProcessEnvironmentValue -Name $name -Value $previousEnvironment[$name]
    }
    Pop-Location
}

Write-Host "Deploy GPU validation passed. Evidence: $OutputRoot" -ForegroundColor Green
