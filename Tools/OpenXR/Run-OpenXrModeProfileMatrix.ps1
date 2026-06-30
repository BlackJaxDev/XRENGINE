[CmdletBinding()]
param(
    [Parameter()]
    [string]$RuntimeJson,

    [Parameter()]
    [int]$SmokeFrames = 180,

    [Parameter()]
    [int]$TimeoutSeconds = 180,

    [Parameter()]
    [int]$InterCaseDelaySeconds = 2,

    [Parameter()]
    [string]$Configuration = "Debug",

    [Parameter()]
    [string]$Platform = "AnyCPU",

    [Parameter()]
    [string]$RunRoot,

    [Parameter()]
    [switch]$NoBuild,

    [Parameter()]
    [switch]$StartService,

    [Parameter()]
    [switch]$SkipLoaderPreflight,

    [Parameter()]
    [switch]$SkipAllocationAudit,

    [Parameter()]
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$smokeScript = Join-Path $PSScriptRoot "Run-OpenXrMonadoSmoke.ps1"

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    $expanded = [Environment]::ExpandEnvironmentVariables($Path)
    if ([System.IO.Path]::IsPathRooted($expanded)) {
        return [System.IO.Path]::GetFullPath($expanded)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $expanded))
}

function ConvertTo-ProcessArgument {
    param([string]$Argument)

    if ($null -eq $Argument -or $Argument.Length -eq 0) {
        return '""'
    }

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    $escaped = $Argument -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Join-ProcessArguments {
    param([Parameter(Mandatory)][string[]]$Arguments)

    return ($Arguments | ForEach-Object { ConvertTo-ProcessArgument $_ }) -join " "
}

function New-AgentRunRoot {
    param([string]$RequestedRunRoot)

    $agentRoot = Join-Path $repoRoot "Build\_AgentValidation"
    [System.IO.Directory]::CreateDirectory($agentRoot) | Out-Null
    $agentRootFull = [System.IO.Path]::GetFullPath($agentRoot)

    $existingRuns = Get-ChildItem -LiteralPath $agentRootFull -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc
    $removeCount = [Math]::Max(0, ($existingRuns.Count + 1) - 10)
    foreach ($oldRun in ($existingRuns | Select-Object -First $removeCount)) {
        $oldRunFull = [System.IO.Path]::GetFullPath($oldRun.FullName)
        if (-not $oldRunFull.StartsWith($agentRootFull, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to delete path outside Build/_AgentValidation: $oldRunFull"
        }

        Remove-Item -LiteralPath $oldRunFull -Recurse -Force
    }

    if (-not [string]::IsNullOrWhiteSpace($RequestedRunRoot)) {
        $resolved = Resolve-FullPath $RequestedRunRoot
    }
    else {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $resolved = Join-Path $agentRootFull "$stamp-openxr-mode-profile-matrix"
    }

    [System.IO.Directory]::CreateDirectory($resolved) | Out-Null
    foreach ($child in @("logs", "reports", "temp-build", "scratch")) {
        [System.IO.Directory]::CreateDirectory((Join-Path $resolved $child)) | Out-Null
    }

    return [System.IO.Path]::GetFullPath($resolved)
}

function Invoke-WithEnvironment {
    param(
        [Parameter(Mandatory)][hashtable]$Variables,
        [Parameter(Mandatory)][scriptblock]$ScriptBlock
    )

    $previous = @{}
    foreach ($key in $Variables.Keys) {
        $previous[$key] = [Environment]::GetEnvironmentVariable($key, "Process")
        [Environment]::SetEnvironmentVariable($key, [string]$Variables[$key], "Process")
    }

    try {
        & $ScriptBlock
    }
    finally {
        foreach ($key in $Variables.Keys) {
            [Environment]::SetEnvironmentVariable($key, $previous[$key], "Process")
        }
    }
}

$resolvedRunRoot = New-AgentRunRoot $RunRoot
$reportsDir = Join-Path $resolvedRunRoot "reports"
$logsDir = Join-Path $resolvedRunRoot "logs"

$matrix = @(
    [pscustomobject]@{
        Name = "sequential-dedicated"
        ViewRenderMode = "SequentialViews"
        PacingMode = "DedicatedThread"
        SerialEyeSubmit = "0"
    },
    [pscustomobject]@{
        Name = "single-pass-dedicated"
        ViewRenderMode = "SinglePassStereo"
        PacingMode = "DedicatedThread"
        SerialEyeSubmit = "0"
    },
    [pscustomobject]@{
        Name = "parallel-dedicated"
        ViewRenderMode = "ParallelCommandBufferRecording"
        PacingMode = "DedicatedThread"
        SerialEyeSubmit = "0"
    },
    [pscustomobject]@{
        Name = "parallel-collect-visible"
        ViewRenderMode = "ParallelCommandBufferRecording"
        PacingMode = "CollectVisibleThread"
        SerialEyeSubmit = "0"
    },
    [pscustomobject]@{
        Name = "parallel-serial-submit"
        ViewRenderMode = "ParallelCommandBufferRecording"
        PacingMode = "DedicatedThread"
        SerialEyeSubmit = "1"
    }
)

$results = New-Object System.Collections.Generic.List[object]

foreach ($case in $matrix) {
    $summaryPath = Join-Path $reportsDir "$($case.Name)-smoke-summary.json"
    $caseLogPath = Join-Path $logsDir "$($case.Name).log"
    $caseErrorLogPath = Join-Path $logsDir "$($case.Name).stderr.log"
    $envVars = @{
        "XRE_PROFILE_CAPTURE" = "1"
        "XRE_PROFILE_AUTO_DUMP" = "1"
        "XRE_OPENXR_VULKAN_TRACE" = "1"
        "XRE_UNIT_TEST_VR_VIEW_RENDER_MODE" = $case.ViewRenderMode
        "XRE_OPENXR_RENDER_PACING_MODE" = $case.PacingMode
        "XRE_OPENXR_VULKAN_SERIAL_EYE_SUBMIT" = $case.SerialEyeSubmit
    }

    $exitCode = 0
    $startedUtc = [DateTime]::UtcNow
    $elapsedMs = 0.0

    if (-not $DryRun) {
        $smokeProcessArguments = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $smokeScript,
            "-Renderer", "Vulkan",
            "-SmokeFrames", [string]$SmokeFrames,
            "-TimeoutSeconds", [string]$TimeoutSeconds,
            "-Configuration", $Configuration,
            "-Platform", $Platform,
            "-RunRoot", (Join-Path $resolvedRunRoot $case.Name),
            "-SummaryPath", $summaryPath
        )

        if (-not [string]::IsNullOrWhiteSpace($RuntimeJson)) {
            $smokeProcessArguments += @("-RuntimeJson", $RuntimeJson)
        }
        if ($NoBuild) {
            $smokeProcessArguments += "-NoBuild"
        }
        if ($StartService) {
            $smokeProcessArguments += "-StartService"
        }
        if ($SkipLoaderPreflight) {
            $smokeProcessArguments += "-SkipLoaderPreflight"
        }
        if ($SkipAllocationAudit) {
            $smokeProcessArguments += "-SkipAllocationAudit"
        }

        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        $exitCode = Invoke-WithEnvironment $envVars {
            $psi = [System.Diagnostics.ProcessStartInfo]::new()
            $psi.FileName = "powershell.exe"
            $psi.WorkingDirectory = $repoRoot
            $psi.UseShellExecute = $false
            $psi.RedirectStandardOutput = $true
            $psi.RedirectStandardError = $true
            $psi.CreateNoWindow = $true
            $psi.Arguments = Join-ProcessArguments -Arguments $smokeProcessArguments
            $process = [System.Diagnostics.Process]::new()
            $process.StartInfo = $psi
            if (-not $process.Start()) {
                throw "Failed to start OpenXR smoke process for matrix case '$($case.Name)'."
            }

            $stdoutTask = $process.StandardOutput.ReadToEndAsync()
            $stderrTask = $process.StandardError.ReadToEndAsync()
            $caseTimeoutMs = ([Math]::Max(1, $TimeoutSeconds + 90)) * 1000
            if (-not $process.WaitForExit($caseTimeoutMs)) {
                try {
                    $process.Kill($true)
                }
                catch {
                    $process.Kill()
                }
                try {
                    $process.WaitForExit(5000) | Out-Null
                }
                catch {
                }
                "case timed out after $caseTimeoutMs ms." | Set-Content -LiteralPath $caseErrorLogPath -Encoding UTF8
                return 124
            }
            else {
                $stdout = if ($stdoutTask.Wait(5000)) { $stdoutTask.Result } else { "stdout capture did not complete after process shutdown." }
                $stderr = if ($stderrTask.Wait(5000)) { $stderrTask.Result } else { "stderr capture did not complete after process shutdown." }
                $stdout | Set-Content -LiteralPath $caseLogPath -Encoding UTF8
                $stderr | Set-Content -LiteralPath $caseErrorLogPath -Encoding UTF8
                return $process.ExitCode
            }
        }
        $timer.Stop()
        $elapsedMs = $timer.Elapsed.TotalMilliseconds
    }

    $results.Add([pscustomobject]@{
        Name = $case.Name
        ViewRenderMode = $case.ViewRenderMode
        PacingMode = $case.PacingMode
        SerialEyeSubmit = $case.SerialEyeSubmit
        SmokeFrames = $SmokeFrames
        TimeoutSeconds = $TimeoutSeconds
        DryRun = [bool]$DryRun
        ExitCode = $exitCode
        StartedUtc = $startedUtc.ToString("O")
        ElapsedMs = [Math]::Round($elapsedMs, 3)
        SummaryPath = $summaryPath
        LogPath = $caseLogPath
        ErrorLogPath = $caseErrorLogPath
    }) | Out-Null

    if (-not $DryRun -and $InterCaseDelaySeconds -gt 0) {
        Start-Sleep -Seconds $InterCaseDelaySeconds
    }
}

$csvPath = Join-Path $reportsDir "openxr-mode-profile-matrix.csv"
$jsonPath = Join-Path $reportsDir "openxr-mode-profile-matrix.json"
$results | Export-Csv -LiteralPath $csvPath -NoTypeInformation
$results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

Write-Host "OpenXR mode profile matrix written to:"
Write-Host "  $csvPath"
Write-Host "  $jsonPath"
Write-Host "Run root: $resolvedRunRoot"
