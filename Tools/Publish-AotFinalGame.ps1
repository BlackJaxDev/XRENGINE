param(
    [string]$ProjectPath = ".\Samples\MonkeyBallVR\MonkeyBallVR.xrproj",
    [ValidateSet("Development", "Release")]
    [string]$BuildConfiguration = "Release",
    [ValidateSet("AnyCPU", "Windows64")]
    [string]$BuildPlatform = "Windows64",
    [string]$OutputSubfolder = "Publish",
    [string]$LauncherName = "Game.exe",
    [switch]$NoClean,
    [switch]$NoSmoke,
    [int]$SmokeTimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

function Invoke-ProcessCapture {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,
        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,
        [int]$TimeoutSeconds = 0
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments = ($Arguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join " "

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start '$FilePath'."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    if ($TimeoutSeconds -gt 0) {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try {
                $process.Kill($true)
            } catch {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }

            throw "Process '$FilePath' timed out after $TimeoutSeconds seconds."
        }
    } else {
        $process.WaitForExit()
    }

    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()

    [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = if ([string]::IsNullOrWhiteSpace($stderr)) { $stdout } else { $stdout + [Environment]::NewLine + $stderr }
    }
}

function ConvertTo-CommandLineArgument {
    param([Parameter(Mandatory = $true)][string]$Argument)

    if ($Argument -notmatch '[\s"]') {
        return $Argument
    }

    '"' + ($Argument -replace '"', '\"') + '"'
}

function Remove-GeneratedProjectPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectDirectory,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $projectRoot = [System.IO.Path]::GetFullPath($ProjectDirectory).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $resolvedPath = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path))

    if (-not $resolvedPath.StartsWith($projectRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove generated path outside project directory: $resolvedPath"
    }

    Write-Host "  Cleaning: $resolvedPath"
    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Get-LatestLauncherPublishLog {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectDirectory
    )

    $intermediateDir = Join-Path $ProjectDirectory "Intermediate"
    if (-not (Test-Path -LiteralPath $intermediateDir)) {
        return $null
    }

    Get-ChildItem -LiteralPath $intermediateDir -Recurse -Filter "aot-publish.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Get-AotWarningLines {
    param([string]$Text)

    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $warnings = [System.Collections.Generic.List[string]]::new()

    foreach ($line in ($Text -split '\r?\n')) {
        if ($line -notmatch '\bIL[23][0-9]{3}\b') {
            continue
        }

        if ($seen.Add($line)) {
            $warnings.Add($line)
        }
    }

    $warnings
}

function Get-AotWarningClassification {
    param([Parameter(Mandatory = $true)][string]$Warning)

    if ($Warning -match '^ILC :' -or
        $Warning -match '^/_/src/runtime/' -or
        $Warning -match '\\Microsoft\.CSharp\\' -or
        $Warning -match '\\System\.Linq\.Expressions\\') {
        return "third-party/runtime library internals"
    }

    if ($Warning -match 'CookedBinary|RuntimeCookedBinarySerializer|SerializedAssetSupport|XRMesh\.MemoryPack|XRTexture2D\.StreamingPayload|XRAsset\.MemoryPack|PublishedCookedAssetRegistryRegistration|AnimationPropertySerialization') {
        return "cooked-binary runtime/fallback surface"
    }

    if ($Warning -match 'Yaml|YamlDotNet|AssetManager\.Serialization|AssetManager\.ThirdPartyImport|AssetManager\.Loading\.ThirdParty|XRAssetGraphUtility|Gltf|ProfileCapture|ShaderArtifactCache|PipelinePrewarm|BinaryCache|UpscaleBridgeSidecar|InterfaceCollectionYamlNodeDeserializer|PolymorphicYamlNodeDeserializer|ViewportRenderCommandContainerYamlNodeDeserializer') {
        return "editor/dev authoring, import, or cache surface"
    }

    if ($Warning -match 'AotRuntimeMetadataStore|RuntimeWorldObjectBase|RuntimePlayerControllerServices|TransformBase|AssetManager\.Loading\.Core|RenderPipelinePostProcessSchema|CameraPostProcessStateCollection|OpenXRAPI\.State|Engine\.RuntimeShaderServices') {
        return "first-party runtime follow-up"
    }

    return "general first-party reflection/dynamic-code follow-up"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectCandidate = if ([System.IO.Path]::IsPathRooted($ProjectPath)) {
    $ProjectPath
} else {
    Join-Path $repoRoot $ProjectPath
}
$projectFullPath = Resolve-Path $projectCandidate
$editorProject = Join-Path $repoRoot "XREngine.Editor\XREngine.Editor.csproj"
$reportsDir = Join-Path $repoRoot "Build\Reports"
$publishLog = Join-Path $reportsDir "aot-final-game-publish.log"
$launcherPublishLogCopy = Join-Path $reportsDir "aot-final-game-launcher-publish.log"
$warningReport = Join-Path $reportsDir "aot-final-game-publish-warnings.md"
$smokeLog = Join-Path $reportsDir "aot-final-game-smoke.log"

New-Item -ItemType Directory -Force -Path $reportsDir | Out-Null

Write-Host "Publishing NativeAOT final game launcher..."
Write-Host "  Project: $projectFullPath"
Write-Host "  Output:  $OutputSubfolder\$LauncherName"

$projectDir = Split-Path -Parent $projectFullPath
if (-not $NoClean) {
    Write-Host "Cleaning generated project publish artifacts..."
    Remove-GeneratedProjectPath -ProjectDirectory $projectDir -Path (Join-Path $projectDir "Build\$OutputSubfolder")
    Remove-GeneratedProjectPath -ProjectDirectory $projectDir -Path (Join-Path $projectDir "Intermediate\Build")

    $intermediateDir = Join-Path $projectDir "Intermediate"
    if (Test-Path -LiteralPath $intermediateDir) {
        Get-ChildItem -LiteralPath $intermediateDir -Directory -ErrorAction SilentlyContinue |
            ForEach-Object {
                Remove-GeneratedProjectPath -ProjectDirectory $projectDir -Path (Join-Path $_.FullName "Launcher")
            }
    }
}

$buildArgs = @(
    "run",
    "--project", $editorProject,
    "-c", "Debug",
    "-p:Platform=AnyCPU",
    "--",
    "--build-project", $projectFullPath,
    "--build-configuration", $BuildConfiguration,
    "--build-platform", $BuildPlatform,
    "--output-subfolder", $OutputSubfolder,
    "--launcher-name", $LauncherName,
    "--publish-native-aot", "true",
    "--validate-aot", "true"
)

$buildResult = Invoke-ProcessCapture -FilePath "dotnet" -Arguments $buildArgs -WorkingDirectory $repoRoot
$buildExit = $buildResult.ExitCode
$buildText = $buildResult.Output
Set-Content -Path $publishLog -Value $buildText -Encoding UTF8

$launcherPublishLog = Get-LatestLauncherPublishLog -ProjectDirectory $projectDir
$warningSourceLog = $publishLog
$warningSourceText = $buildText
if ($null -ne $launcherPublishLog -and (Test-Path -LiteralPath $launcherPublishLog.FullName)) {
    Copy-Item -LiteralPath $launcherPublishLog.FullName -Destination $launcherPublishLogCopy -Force
    $warningSourceLog = $launcherPublishLogCopy
    $warningSourceText = Get-Content -Raw -Path $launcherPublishLogCopy
}

$aotWarnings = @(Get-AotWarningLines -Text $warningSourceText)

$report = [System.Collections.Generic.List[string]]::new()
$report.Add("# AOT Final Game Publish Warnings")
$report.Add("")
$report.Add("Project: ``$projectFullPath``")
$report.Add("Outer build log: ``$publishLog``")
$report.Add("Warning source log: ``$warningSourceLog``")
$report.Add("")
if ($aotWarnings.Count -eq 0) {
    $report.Add("No IL2xxx/IL3xxx warnings were emitted by the validation publish warning source.")
} else {
    $report.Add("IL2xxx/IL3xxx warnings: $($aotWarnings.Count)")
    $report.Add("")
    $report.Add("Classification summary:")
    foreach ($classificationGroup in ($aotWarnings | Group-Object { Get-AotWarningClassification $_ } | Sort-Object Count -Descending)) {
        $report.Add("- $($classificationGroup.Name): $($classificationGroup.Count)")
    }
    $report.Add("")
    foreach ($warning in $aotWarnings) {
        $classification = Get-AotWarningClassification $warning
        $report.Add("- [$classification] ``$warning``")
    }
}
Set-Content -Path $warningReport -Value $report -Encoding UTF8

if ($buildExit -ne 0) {
    throw "NativeAOT publish failed. See $publishLog"
}

$launcherExe = $LauncherName
if ([System.IO.Path]::GetExtension($launcherExe) -eq "") {
    $launcherExe = "$launcherExe.exe"
}

$exePath = Join-Path $projectDir "Build\$OutputSubfolder\Binaries\$launcherExe"
if (-not (Test-Path $exePath)) {
    throw "Published launcher executable was not found at '$exePath'."
}

$configArchive = Join-Path $projectDir "Build\$OutputSubfolder\Config\GameConfig.pak"
$contentArchive = Join-Path $projectDir "Build\$OutputSubfolder\Content\GameContent.pak"
if (-not (Test-Path $configArchive)) {
    throw "Published config archive was not found at '$configArchive'."
}
if (-not (Test-Path $contentArchive)) {
    throw "Published content archive was not found at '$contentArchive'."
}

if (-not $NoSmoke) {
    Write-Host "Running published launcher AOT smoke..."
    Remove-Item -Force -ErrorAction SilentlyContinue $smokeLog

    try {
        $smokeResult = Invoke-ProcessCapture `
            -FilePath $exePath `
            -Arguments @("--aot-smoke") `
            -WorkingDirectory (Split-Path -Parent $exePath) `
            -TimeoutSeconds $SmokeTimeoutSeconds
    } catch {
        throw "AOT smoke timed out after $SmokeTimeoutSeconds seconds. $($_.Exception.Message)"
    }

    Set-Content -Path $smokeLog -Value $smokeResult.Output -Encoding UTF8

    if ($smokeResult.ExitCode -ne 0) {
        throw "AOT smoke failed with exit code $($smokeResult.ExitCode). See $smokeLog"
    }
}

Write-Host "NativeAOT final game validation completed."
Write-Host "  Launcher: $exePath"
Write-Host "  Publish log: $publishLog"
Write-Host "  Warning report: $warningReport"
if (-not $NoSmoke) {
    Write-Host "  Smoke log: $smokeLog"
}
