[CmdletBinding()]
param(
    [Parameter()]
    [int]$SmokeSeconds = 10,

    [Parameter()]
    [int]$TimeoutSeconds = 45,

    [Parameter()]
    [string]$Configuration = "Debug",

    [Parameter()]
    [string]$Platform = "AnyCPU",

    [Parameter()]
    [string]$RunRoot,

    [Parameter()]
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))

function Resolve-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
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
        $resolved = Join-Path $agentRootFull "$stamp-openxr-scene-only-vr-smoke"
    }

    [System.IO.Directory]::CreateDirectory($resolved) | Out-Null
    foreach ($child in @("logs", "reports")) {
        [System.IO.Directory]::CreateDirectory((Join-Path $resolved $child)) | Out-Null
    }

    return [System.IO.Path]::GetFullPath($resolved)
}

function Invoke-EditorSceneSmoke {
    param(
        [Parameter(Mandatory)][string]$EditorDll,
        [Parameter(Mandatory)][string]$StdoutPath,
        [Parameter(Mandatory)][string]$StderrPath
    )

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "dotnet"
    $psi.WorkingDirectory = $repoRoot
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.Arguments = Join-ProcessArguments -Arguments @(
        $EditorDll,
        "--unit-testing"
    )
    $psi.Environment["XRE_WORLD_MODE"] = "UnitTesting"
    $psi.Environment["XRE_UNIT_TEST_WORLD_KIND"] = "Default"
    $psi.Environment["XRE_UNIT_TEST_RENDER_API"] = "OpenGL"
    $psi.Environment["XRE_UNIT_TEST_VR_MODE"] = "Emulated"
    $psi.Environment["XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS"] = "1"
    $psi.Environment["XRE_WINDOW_TITLE"] = "XRE Scene-only VR Smoke"

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) {
        throw "Failed to start editor scene-only VR smoke process."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Max($TimeoutSeconds, $SmokeSeconds + 5))
    $survivedUntil = [DateTimeOffset]::UtcNow.AddSeconds($SmokeSeconds)

    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) {
            $stdoutTask.Wait()
            $stderrTask.Wait()
            $stdoutTask.Result | Set-Content -LiteralPath $StdoutPath -Encoding UTF8
            $stderrTask.Result | Set-Content -LiteralPath $StderrPath -Encoding UTF8
            throw "Scene-only VR smoke process exited early with code $($process.ExitCode)."
        }

        if ([DateTimeOffset]::UtcNow -ge $survivedUntil) {
            try {
                $process.Kill($true)
            }
            catch {
                $process.Kill()
            }
            $stdoutTask.Wait()
            $stderrTask.Wait()
            $stdoutTask.Result | Set-Content -LiteralPath $StdoutPath -Encoding UTF8
            $stderrTask.Result | Set-Content -LiteralPath $StderrPath -Encoding UTF8
            return
        }

        Start-Sleep -Milliseconds 250
    }

    try {
        $process.Kill($true)
    }
    catch {
        $process.Kill()
    }
    throw "Scene-only VR smoke process exceeded timeout."
}

$RunRoot = New-AgentRunRoot -RequestedRunRoot $RunRoot
$logs = Join-Path $RunRoot "logs"
$reports = Join-Path $RunRoot "reports"

if (-not $NoBuild) {
    $buildLog = Join-Path $logs "build-editor.log"
    & dotnet build (Join-Path $repoRoot "XREngine.Editor\XREngine.Editor.csproj") `
        -c $Configuration `
        -p:Platform=$Platform `
        /property:GenerateFullPaths=true `
        /consoleloggerparameters:NoSummary *> $buildLog
    if ($LASTEXITCODE -ne 0) {
        throw "Editor build failed. See $buildLog"
    }
}

$editorDll = Join-Path $repoRoot "Build\Editor\$Configuration\$Platform\$Configuration\net10.0-windows7.0\XREngine.Editor.dll"
if (-not (Test-Path -LiteralPath $editorDll -PathType Leaf)) {
    throw "Editor DLL does not exist: $editorDll"
}

Invoke-EditorSceneSmoke `
    -EditorDll $editorDll `
    -StdoutPath (Join-Path $logs "editor.stdout.log") `
    -StderrPath (Join-Path $logs "editor.stderr.log")

[pscustomobject]@{
    schemaVersion        = 1
    lane                 = "SceneOnlyVR"
    smokeSeconds         = $SmokeSeconds
    renderApi            = "OpenGL"
    vrMode               = "Emulated"
    previewVRStereoViews = $true
    runRoot              = $RunRoot
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $reports "openxr-scene-only-vr-smoke-summary.json") -Encoding UTF8

Write-Host "Scene-only VR smoke passed. RunRoot=$RunRoot"
