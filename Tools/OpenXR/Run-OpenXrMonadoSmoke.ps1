[CmdletBinding()]
param(
    [Parameter()]
    [string]$RuntimeJson,

    [Parameter()]
    [ValidateSet("OpenGL", "Vulkan")]
    [string]$Renderer = "OpenGL",

    [Parameter()]
    [int]$SmokeFrames = 120,

    [Parameter()]
    [int]$TimeoutSeconds = 120,

    [Parameter()]
    [string]$Configuration = "Debug",

    [Parameter()]
    [string]$Platform = "AnyCPU",

    [Parameter()]
    [string]$RunRoot,

    [Parameter()]
    [string]$SummaryPath,

    [Parameter()]
    [switch]$NoBuild,

    [Parameter()]
    [switch]$StartService,

    [Parameter()]
    [string]$ServiceExe,

    [Parameter()]
    [switch]$SkipAllocationAudit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
$openXrToolRoot = Join-Path $repoRoot "Tools\OpenXR"
$findRuntimeScript = Join-Path $openXrToolRoot "Find-MonadoRuntime.ps1"
$serviceScript = Join-Path $openXrToolRoot "Start-MonadoService.ps1"

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
        $resolved = Join-Path $agentRootFull "$stamp-openxr-monado-smoke"
    }

    [System.IO.Directory]::CreateDirectory($resolved) | Out-Null
    foreach ($child in @("logs", "reports", "temp-build", "scratch")) {
        [System.IO.Directory]::CreateDirectory((Join-Path $resolved $child)) | Out-Null
    }

    return [System.IO.Path]::GetFullPath($resolved)
}

function Find-OpenXrLoaderPath {
    $editorOutput = Join-Path $repoRoot "Build\Editor\$Configuration\$Platform\$Configuration\net10.0-windows7.0\openxr_loader.dll"
    $candidates = @(
        $editorOutput,
        "$env:ProgramFiles\Monado\bin\openxr_loader.dll",
        "$env:ProgramFiles\Monado\openxr_loader.dll",
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\SteamVR\bin\win64\openxr_loader.dll"
    )

    if (-not [string]::IsNullOrWhiteSpace($env:PATH)) {
        foreach ($pathEntry in ($env:PATH -split [System.IO.Path]::PathSeparator)) {
            if (-not [string]::IsNullOrWhiteSpace($pathEntry)) {
                $candidates += (Join-Path $pathEntry "openxr_loader.dll")
            }
        }
    }

    foreach ($candidate in ($candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

function Ensure-OpenXrPreflightType {
    if ("OpenXrLoaderPreflight" -as [type]) {
        return
    }

    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class OpenXrLoaderPreflight
{
    private const int XR_TYPE_API_LAYER_PROPERTIES = 1;
    private const int XR_TYPE_EXTENSION_PROPERTIES = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct XrApiLayerProperties
    {
        public int type;
        public IntPtr next;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string layerName;
        public uint specVersion;
        public uint layerVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct XrExtensionProperties
    {
        public int type;
        public IntPtr next;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string extensionName;
        public uint extensionVersion;
    }

    [DllImport("openxr_loader.dll", EntryPoint = "xrEnumerateApiLayerProperties", CallingConvention = CallingConvention.Winapi)]
    private static extern int EnumerateApiLayerPropertiesCount(uint propertyCapacityInput, out uint propertyCountOutput, IntPtr properties);

    [DllImport("openxr_loader.dll", EntryPoint = "xrEnumerateApiLayerProperties", CallingConvention = CallingConvention.Winapi)]
    private static extern int EnumerateApiLayerProperties(uint propertyCapacityInput, out uint propertyCountOutput, [In, Out] XrApiLayerProperties[] properties);

    [DllImport("openxr_loader.dll", EntryPoint = "xrEnumerateInstanceExtensionProperties", CallingConvention = CallingConvention.Winapi)]
    private static extern int EnumerateInstanceExtensionPropertiesCount(IntPtr layerName, uint propertyCapacityInput, out uint propertyCountOutput, IntPtr properties);

    [DllImport("openxr_loader.dll", EntryPoint = "xrEnumerateInstanceExtensionProperties", CallingConvention = CallingConvention.Winapi)]
    private static extern int EnumerateInstanceExtensionProperties(IntPtr layerName, uint propertyCapacityInput, out uint propertyCountOutput, [In, Out] XrExtensionProperties[] properties);

    public static string[] EnumerateApiLayers()
    {
        uint count;
        int result = EnumerateApiLayerPropertiesCount(0, out count, IntPtr.Zero);
        if (result != 0)
            throw new InvalidOperationException("xrEnumerateApiLayerProperties(count) returned " + result);

        if (count == 0)
            return Array.Empty<string>();

        var properties = new XrApiLayerProperties[count];
        for (int i = 0; i < properties.Length; i++)
            properties[i].type = XR_TYPE_API_LAYER_PROPERTIES;

        result = EnumerateApiLayerProperties(count, out count, properties);
        if (result != 0)
            throw new InvalidOperationException("xrEnumerateApiLayerProperties(list) returned " + result);

        var names = new List<string>();
        for (int i = 0; i < count; i++)
            if (!string.IsNullOrWhiteSpace(properties[i].layerName))
                names.Add(properties[i].layerName);
        return names.ToArray();
    }

    public static string[] EnumerateInstanceExtensions()
    {
        uint count;
        int result = EnumerateInstanceExtensionPropertiesCount(IntPtr.Zero, 0, out count, IntPtr.Zero);
        if (result != 0)
            throw new InvalidOperationException("xrEnumerateInstanceExtensionProperties(count) returned " + result);

        if (count == 0)
            return Array.Empty<string>();

        var properties = new XrExtensionProperties[count];
        for (int i = 0; i < properties.Length; i++)
            properties[i].type = XR_TYPE_EXTENSION_PROPERTIES;

        result = EnumerateInstanceExtensionProperties(IntPtr.Zero, count, out count, properties);
        if (result != 0)
            throw new InvalidOperationException("xrEnumerateInstanceExtensionProperties(list) returned " + result);

        var names = new List<string>();
        for (int i = 0; i < count; i++)
            if (!string.IsNullOrWhiteSpace(properties[i].extensionName))
                names.Add(properties[i].extensionName);
        return names.ToArray();
    }
}
"@
}

function Invoke-OpenXrLoaderPreflight {
    param(
        [Parameter(Mandatory)][string]$RuntimeManifest,
        [Parameter(Mandatory)][string[]]$RequiredExtensions,
        [Parameter(Mandatory)][string]$ReportPath
    )

    $previousRuntimeJson = $env:XR_RUNTIME_JSON
    $previousPath = $env:PATH
    try {
        $env:XR_RUNTIME_JSON = $RuntimeManifest
        $loaderPath = Find-OpenXrLoaderPath
        if (-not [string]::IsNullOrWhiteSpace($loaderPath)) {
            $env:PATH = "$(Split-Path -Parent $loaderPath)$([System.IO.Path]::PathSeparator)$previousPath"
        }

        Ensure-OpenXrPreflightType
        $layers = [OpenXrLoaderPreflight]::EnumerateApiLayers()
        $extensions = [OpenXrLoaderPreflight]::EnumerateInstanceExtensions()
        $missing = @($RequiredExtensions | Where-Object { $extensions -notcontains $_ })

        $report = [pscustomobject]@{
            runtimeJson        = $RuntimeManifest
            loaderPath         = $loaderPath
            apiLayers          = $layers
            instanceExtensions = $extensions
            requiredExtensions = $RequiredExtensions
            missingExtensions  = $missing
        }
        $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding UTF8

        if ($missing.Count -gt 0) {
            throw "OpenXR runtime is missing required extension(s): $($missing -join ', ')"
        }

        return $report
    }
    finally {
        $env:XR_RUNTIME_JSON = $previousRuntimeJson
        $env:PATH = $previousPath
    }
}

function Invoke-EditorSmoke {
    param(
        [Parameter(Mandatory)][string]$EditorDll,
        [Parameter(Mandatory)][string]$Summary,
        [Parameter(Mandatory)][string]$StdoutPath,
        [Parameter(Mandatory)][string]$StderrPath,
        [Parameter(Mandatory)][hashtable]$Environment
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
        "--unit-testing",
        "--smoke-frames",
        [string]$SmokeFrames,
        "--smoke-timeout-seconds",
        [string]$TimeoutSeconds,
        "--openxr-smoke-summary",
        $Summary
    )

    foreach ($entry in $Environment.GetEnumerator()) {
        $psi.Environment[$entry.Key] = [string]$entry.Value
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $psi
    if (-not $process.Start()) {
        throw "Failed to start editor smoke process."
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $waitMilliseconds = [Math]::Max(1, $TimeoutSeconds + 30) * 1000
    if (-not $process.WaitForExit($waitMilliseconds)) {
        try {
            $process.Kill($true)
        }
        catch {
            $process.Kill()
        }
        throw "Editor smoke process timed out after $($TimeoutSeconds + 30)s."
    }

    $stdoutTask.Wait()
    $stderrTask.Wait()
    $stdoutTask.Result | Set-Content -LiteralPath $StdoutPath -Encoding UTF8
    $stderrTask.Result | Set-Content -LiteralPath $StderrPath -Encoding UTF8
    return $process.ExitCode
}

$RunRoot = New-AgentRunRoot -RequestedRunRoot $RunRoot
$logs = Join-Path $RunRoot "logs"
$reports = Join-Path $RunRoot "reports"
$serviceMarker = Join-Path $RunRoot "mcp-output\monado-service-marker.json"
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $serviceMarker)) | Out-Null

$runtimeInfo = & $findRuntimeScript -RuntimeJson $RuntimeJson
$runtimeJsonFullPath = [string]$runtimeInfo.RuntimeJson
$summaryFullPath = if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    Join-Path $reports "openxr-smoke-summary.json"
}
else {
    Resolve-FullPath $SummaryPath
}

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

$requiredExtensions = if ($Renderer -eq "OpenGL") {
    @("XR_KHR_opengl_enable")
}
else {
    @("XR_KHR_vulkan_enable", "XR_KHR_vulkan_enable2")
}

if ($Renderer -eq "Vulkan") {
    $preflightReport = Invoke-OpenXrLoaderPreflight `
        -RuntimeManifest $runtimeJsonFullPath `
        -RequiredExtensions @() `
        -ReportPath (Join-Path $reports "openxr-loader-preflight.json")
    $hasVulkanExtension = $preflightReport.instanceExtensions -contains "XR_KHR_vulkan_enable" -or
        $preflightReport.instanceExtensions -contains "XR_KHR_vulkan_enable2"
    if (-not $hasVulkanExtension) {
        throw "OpenXR runtime is missing required Vulkan extension XR_KHR_vulkan_enable or XR_KHR_vulkan_enable2."
    }
}
else {
    Invoke-OpenXrLoaderPreflight `
        -RuntimeManifest $runtimeJsonFullPath `
        -RequiredExtensions $requiredExtensions `
        -ReportPath (Join-Path $reports "openxr-loader-preflight.json") | Out-Null
}

$serviceStarted = $false
try {
    if ($StartService) {
        $serviceResult = & $serviceScript `
            -RuntimeJson $runtimeJsonFullPath `
            -ServiceExe $ServiceExe `
            -MarkerPath $serviceMarker `
            -LogDirectory (Join-Path $logs "monado-service")
        $serviceResult | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reports "monado-service-start.json") -Encoding UTF8
        $serviceStarted = $true
    }

    $editorDll = Join-Path $repoRoot "Build\Editor\$Configuration\$Platform\$Configuration\net10.0-windows7.0\XREngine.Editor.dll"
    if (-not (Test-Path -LiteralPath $editorDll -PathType Leaf)) {
        throw "Editor DLL does not exist: $editorDll"
    }

    $environment = @{
        XR_RUNTIME_JSON                         = $runtimeJsonFullPath
        XRE_WORLD_MODE                          = "UnitTesting"
        XRE_UNIT_TEST_WORLD_KIND                = "Default"
        XRE_UNIT_TEST_RENDER_API                = $Renderer
        XRE_UNIT_TEST_VR_MODE                   = "MonadoOpenXR"
        XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS   = "1"
        XRE_WINDOW_TITLE                        = "XRE OpenXR Monado Smoke"
        XRE_SMOKE_FRAMES                        = [string]$SmokeFrames
        XRE_OPENXR_SMOKE_SUMMARY                = $summaryFullPath
        XRE_SMOKE_TIMEOUT_SECONDS               = [string]$TimeoutSeconds
    }

    $exitCode = Invoke-EditorSmoke `
        -EditorDll $editorDll `
        -Summary $summaryFullPath `
        -StdoutPath (Join-Path $logs "editor.stdout.log") `
        -StderrPath (Join-Path $logs "editor.stderr.log") `
        -Environment $environment

    if (-not (Test-Path -LiteralPath $summaryFullPath -PathType Leaf)) {
        throw "OpenXR smoke summary was not written: $summaryFullPath"
    }

    $summary = Get-Content -LiteralPath $summaryFullPath -Raw | ConvertFrom-Json
    $summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $reports "openxr-smoke-summary.normalized.json") -Encoding UTF8

    if ($exitCode -ne 0) {
        Write-Host "OpenXR smoke failed with editor exit code $exitCode."
        if ($summary.failures) {
            Write-Host ($summary.failures -join "`n")
        }
        exit $exitCode
    }

    if (-not $SkipAllocationAudit) {
        $allocationReport = Join-Path $reports "openxr-new-allocations.md"
        $allocationLog = Join-Path $logs "openxr-new-allocations.log"
        & (Join-Path $repoRoot "Tools\Reports\Find-NewAllocations.ps1") `
            -Root $repoRoot `
            -OutFile $allocationReport `
            -FailOnOpenXrHotPathAllocations *> $allocationLog
        if ($LASTEXITCODE -ne 0) {
            throw "OpenXR allocation audit failed. See $allocationLog and $allocationReport"
        }
    }

    Write-Host "OpenXR Monado smoke passed. RunRoot=$RunRoot Summary=$summaryFullPath"
    exit 0
}
finally {
    if ($serviceStarted) {
        try {
            & $serviceScript -MarkerPath $serviceMarker -Stop |
                ConvertTo-Json -Depth 6 |
                Set-Content -LiteralPath (Join-Path $reports "monado-service-stop.json") -Encoding UTF8
        }
        catch {
            Write-Warning "Failed to stop owned Monado service: $($_.Exception.Message)"
        }
    }
}
