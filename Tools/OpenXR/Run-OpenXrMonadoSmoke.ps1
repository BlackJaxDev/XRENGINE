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
    [int]$WarmupFrames = 0,

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
    [string]$EditorDll,

    [Parameter()]
    [switch]$NoBuild,

    [Parameter()]
    [switch]$StartService,

    [Parameter()]
    [ValidateSet("wobble", "rotate", "stationary", "user_input")]
    [string]$SimulatedHmdPoseMode = "stationary",

    [Parameter()]
    [switch]$RequireOwnedService,

    [Parameter()]
    [string]$ServiceExe,

    [Parameter()]
    [switch]$SkipLoaderPreflight,

    [Parameter()]
    [switch]$SkipAllocationAudit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$LASTEXITCODE = 0

# First-chance tracing is an editor diagnostic. Do not expose it to this
# PowerShell host while the native OpenXR loader is loaded; pass it explicitly
# to the editor child process instead.
$editorFirstChanceExceptions = $env:XRE_FIRST_CHANCE_EXCEPTIONS
Remove-Item Env:XRE_FIRST_CHANCE_EXCEPTIONS -ErrorAction SilentlyContinue

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

function Get-JsonArrayValues {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Array]) {
        return @($Value)
    }

    $valuesProperty = $Value.PSObject.Properties['$values']
    if ($null -ne $valuesProperty) {
        return @($valuesProperty.Value)
    }

    return @($Value)
}

function New-AgentRunRoot {
    param([string]$RequestedRunRoot)

    $agentRoot = Join-Path $repoRoot "Build\_AgentValidation"
    [System.IO.Directory]::CreateDirectory($agentRoot) | Out-Null
    $agentRootFull = [System.IO.Path]::GetFullPath($agentRoot)

    if (-not [string]::IsNullOrWhiteSpace($RequestedRunRoot)) {
        $resolved = Resolve-FullPath $RequestedRunRoot
    }
    else {
        $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $resolved = Join-Path $agentRootFull "$stamp-openxr-monado-smoke"
    }

    $resolvedFull = [System.IO.Path]::GetFullPath($resolved)
    $agentRootPrefix = $agentRootFull + [System.IO.Path]::DirectorySeparatorChar
    $relativeRunPath = if ($resolvedFull.StartsWith($agentRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        $resolvedFull.Substring($agentRootPrefix.Length)
    }
    else {
        $null
    }
    $firstRunSegment = if (-not [string]::IsNullOrWhiteSpace($relativeRunPath)) {
        ($relativeRunPath -split '[\\/]', 2)[0]
    }
    else {
        $null
    }
    $createsImmediateRun = -not [string]::IsNullOrWhiteSpace($firstRunSegment) -and
        -not (Test-Path -LiteralPath (Join-Path $agentRootFull $firstRunSegment) -PathType Container)

    if ($createsImmediateRun) {
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
    }

    [System.IO.Directory]::CreateDirectory($resolvedFull) | Out-Null
    foreach ($child in @("logs", "reports", "temp-build", "scratch")) {
        [System.IO.Directory]::CreateDirectory((Join-Path $resolvedFull $child)) | Out-Null
    }

    return $resolvedFull
}

function Find-OpenXrLoaderPath {
    $editorOutput = Join-Path $repoRoot "Build\Editor\$Configuration\$Platform\$Configuration\net10.0-windows7.0\openxr_loader.dll"
    $candidates = @(
        (Join-Path $repoRoot "Build\Dependencies\vcpkg\installed\x64-windows\bin\openxr_loader.dll"),
        (Join-Path $repoRoot "Build\Submodules\monado\build\vcpkg_installed\x64-windows\bin\openxr_loader.dll"),
        (Join-Path $repoRoot "Build\Deps\Monado\bin\openxr_loader.dll"),
        $editorOutput,
        "$env:MONADO_INSTALL_DIR\bin\openxr_loader.dll",
        "$env:MONADO_INSTALL_DIR\openxr_loader.dll",
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
    private static IntPtr loaderHandle = IntPtr.Zero;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string pathName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryW")]
    private static extern IntPtr LoadLibrary(string fileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);

    public static void SetDllSearchDirectory(string directory)
    {
        if (!SetDllDirectory(directory))
            throw new InvalidOperationException("SetDllDirectory failed with Win32 error " + Marshal.GetLastWin32Error());
    }

    public static void LoadOpenXrLoader(string loaderPath)
    {
        if (loaderHandle != IntPtr.Zero)
            return;

        loaderHandle = LoadLibrary(loaderPath);
        if (loaderHandle == IntPtr.Zero)
            throw new InvalidOperationException("LoadLibrary failed for " + loaderPath + " with Win32 error " + Marshal.GetLastWin32Error());
    }

    public static void ReleaseOpenXrLoader()
    {
        if (loaderHandle == IntPtr.Zero)
            return;

        FreeLibrary(loaderHandle);
        loaderHandle = IntPtr.Zero;
    }

    public static void ClearDllSearchDirectory()
    {
        SetDllDirectory(null);
    }

    // OpenXR property arrays are native in/out buffers. Avoid the CLR array/string
    // marshaler here: it has corrupted the hosting PowerShell heap with some loader
    // builds during teardown. The smoke tooling is Windows-x64 only, so use the
    // ABI-defined offsets and copy the fixed UTF-8 name fields explicitly.
    private const int XR_PROPERTY_NAME_OFFSET_X64 = 16;
    // type (4) + padding (4) + next (8) + layerName (256) +
    // specVersion (8) + layerVersion (4) + description (256) + tail padding (4).
    // Using 536 under-allocates this buffer and lets the loader overwrite the
    // PowerShell host heap when it writes XrApiLayerProperties on Windows x64.
    private const int XR_API_LAYER_PROPERTIES_SIZE_X64 = 544;
    private const int XR_EXTENSION_PROPERTIES_SIZE_X64 = 152;

    [DllImport("openxr_loader.dll", EntryPoint = "xrEnumerateApiLayerProperties", CallingConvention = CallingConvention.Winapi)]
    private static extern int EnumerateApiLayerPropertiesCount(uint propertyCapacityInput, out uint propertyCountOutput, IntPtr properties);

    [DllImport("openxr_loader.dll", EntryPoint = "xrEnumerateApiLayerProperties", CallingConvention = CallingConvention.Winapi)]
    private static extern int EnumerateApiLayerProperties(uint propertyCapacityInput, out uint propertyCountOutput, IntPtr properties);

    [DllImport("openxr_loader.dll", EntryPoint = "xrEnumerateInstanceExtensionProperties", CallingConvention = CallingConvention.Winapi)]
    private static extern int EnumerateInstanceExtensionPropertiesCount(IntPtr layerName, uint propertyCapacityInput, out uint propertyCountOutput, IntPtr properties);

    [DllImport("openxr_loader.dll", EntryPoint = "xrEnumerateInstanceExtensionProperties", CallingConvention = CallingConvention.Winapi)]
    private static extern int EnumerateInstanceExtensionProperties(IntPtr layerName, uint propertyCapacityInput, out uint propertyCountOutput, IntPtr properties);

    private static IntPtr AllocatePropertyArray(uint count, int elementSize, int structureType)
    {
        if (IntPtr.Size != 8)
            throw new PlatformNotSupportedException("OpenXR smoke loader preflight requires a 64-bit process.");

        IntPtr memory = Marshal.AllocHGlobal(checked((int)count * elementSize));
        for (int i = 0; i < count; i++)
        {
            IntPtr element = IntPtr.Add(memory, checked(i * elementSize));
            for (int offset = 0; offset < elementSize; offset += sizeof(int))
                Marshal.WriteInt32(element, offset, 0);
            Marshal.WriteInt32(element, 0, structureType);
        }
        return memory;
    }

    private static string ReadFixedUtf8(IntPtr element, int offset, int capacity)
    {
        byte[] bytes = new byte[capacity];
        Marshal.Copy(IntPtr.Add(element, offset), bytes, 0, capacity);
        int length = Array.IndexOf(bytes, (byte)0);
        if (length < 0)
            length = capacity;
        return System.Text.Encoding.UTF8.GetString(bytes, 0, length);
    }

    public static string[] EnumerateApiLayers()
    {
        uint count;
        int result = EnumerateApiLayerPropertiesCount(0, out count, IntPtr.Zero);
        if (result != 0)
            throw new InvalidOperationException("xrEnumerateApiLayerProperties(count) returned " + result);

        if (count == 0)
            return Array.Empty<string>();

        IntPtr properties = AllocatePropertyArray(count, XR_API_LAYER_PROPERTIES_SIZE_X64, XR_TYPE_API_LAYER_PROPERTIES);
        try
        {
            result = EnumerateApiLayerProperties(count, out count, properties);
            if (result != 0)
                throw new InvalidOperationException("xrEnumerateApiLayerProperties(list) returned " + result);

            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string name = ReadFixedUtf8(IntPtr.Add(properties, i * XR_API_LAYER_PROPERTIES_SIZE_X64), XR_PROPERTY_NAME_OFFSET_X64, 256);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
            return names.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }
    }

    public static string[] EnumerateInstanceExtensions()
    {
        uint count;
        int result = EnumerateInstanceExtensionPropertiesCount(IntPtr.Zero, 0, out count, IntPtr.Zero);
        if (result != 0)
            throw new InvalidOperationException("xrEnumerateInstanceExtensionProperties(count) returned " + result);

        if (count == 0)
            return Array.Empty<string>();

        IntPtr properties = AllocatePropertyArray(count, XR_EXTENSION_PROPERTIES_SIZE_X64, XR_TYPE_EXTENSION_PROPERTIES);
        try
        {
            result = EnumerateInstanceExtensionProperties(IntPtr.Zero, count, out count, properties);
            if (result != 0)
                throw new InvalidOperationException("xrEnumerateInstanceExtensionProperties(list) returned " + result);

            var names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string name = ReadFixedUtf8(IntPtr.Add(properties, i * XR_EXTENSION_PROPERTIES_SIZE_X64), XR_PROPERTY_NAME_OFFSET_X64, 128);
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
            return names.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(properties);
        }
    }
}
"@
}

function Invoke-OpenXrLoaderPreflight {
    param(
        [Parameter(Mandatory)][string]$RuntimeManifest,
        [Parameter()][string[]]$RequiredExtensions = @(),
        [Parameter(Mandatory)][string]$ReportPath
    )

    $previousRuntimeJson = $env:XR_RUNTIME_JSON
    $previousPath = $env:PATH
    $setDllDirectory = $false
    $loadedOpenXrLoader = $false
    $loaderDirectory = $null
    try {
        $env:XR_RUNTIME_JSON = $RuntimeManifest
        $loaderPath = Find-OpenXrLoaderPath
        if (-not [string]::IsNullOrWhiteSpace($loaderPath)) {
            $loaderDirectory = Split-Path -Parent $loaderPath
            $env:PATH = "$loaderDirectory$([System.IO.Path]::PathSeparator)$previousPath"
        }

        Ensure-OpenXrPreflightType
        if (-not [string]::IsNullOrWhiteSpace($loaderDirectory)) {
            [OpenXrLoaderPreflight]::SetDllSearchDirectory($loaderDirectory)
            $setDllDirectory = $true
        }
        if (-not [string]::IsNullOrWhiteSpace($loaderPath)) {
            [OpenXrLoaderPreflight]::LoadOpenXrLoader($loaderPath)
            $loadedOpenXrLoader = $true
        }

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
        if ($loadedOpenXrLoader -and ("OpenXrLoaderPreflight" -as [type])) {
            [OpenXrLoaderPreflight]::ReleaseOpenXrLoader()
        }
        if ($setDllDirectory -and ("OpenXrLoaderPreflight" -as [type])) {
            [OpenXrLoaderPreflight]::ClearDllSearchDirectory()
        }

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
        "--smoke-warmup-frames",
        [string]$WarmupFrames,
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
    $deadlineUtc = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $TimeoutSeconds + 30))
    $summarySeenUtc = $null
    $terminatedAfterSummary = $false
    while (-not $process.HasExited) {
        if ((Test-Path -LiteralPath $Summary -PathType Leaf)) {
            if ($null -eq $summarySeenUtc) {
                $summarySeenUtc = [DateTime]::UtcNow
            }
            elseif (([DateTime]::UtcNow - $summarySeenUtc).TotalSeconds -ge 5) {
                try {
                    $process.Kill($true)
                }
                catch {
                    $process.Kill()
                }
                $terminatedAfterSummary = $true
                break
            }
        }

        if ([DateTime]::UtcNow -ge $deadlineUtc) {
            try {
                $process.Kill($true)
            }
            catch {
                $process.Kill()
            }
            throw "Editor smoke process timed out after $($TimeoutSeconds + 30)s."
        }

        Start-Sleep -Milliseconds 200
    }

    if ($terminatedAfterSummary) {
        try {
            $process.WaitForExit(5000) | Out-Null
        }
        catch {
        }
        if (-not $process.HasExited) {
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
        }
    }
    elseif (-not $process.HasExited) {
        try {
            $process.Kill($true)
        }
        catch {
            $process.Kill()
        }
        throw "Editor smoke process timed out after $($TimeoutSeconds + 30)s."
    }

    if ($stdoutTask.Wait(5000)) {
        $stdoutTask.Result | Set-Content -LiteralPath $StdoutPath -Encoding UTF8
    }
    else {
        "stdout capture did not complete after process shutdown." | Set-Content -LiteralPath $StdoutPath -Encoding UTF8
    }
    if ($stderrTask.Wait(5000)) {
        $stderrTask.Result | Set-Content -LiteralPath $StderrPath -Encoding UTF8
    }
    else {
        "stderr capture did not complete after process shutdown." | Set-Content -LiteralPath $StderrPath -Encoding UTF8
    }
    $exitCode = if ($process.HasExited) { $process.ExitCode } else { 0 }
    return [pscustomobject]@{
        ExitCode               = $exitCode
        TerminatedAfterSummary = $terminatedAfterSummary
    }
}

$RunRoot = New-AgentRunRoot -RequestedRunRoot $RunRoot
$logs = Join-Path $RunRoot "logs"
$reports = Join-Path $RunRoot "reports"
$serviceMarker = Join-Path $RunRoot "mcp-output\monado-service-marker.json"
[System.IO.Directory]::CreateDirectory((Split-Path -Parent $serviceMarker)) | Out-Null

$runtimeInfo = & $findRuntimeScript -RuntimeJson $RuntimeJson
$runtimeJsonFullPath = [string]$runtimeInfo.RuntimeJson
$openXrLoaderPath = Find-OpenXrLoaderPath
$summaryFullPath = if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    Join-Path $reports "openxr-smoke-summary.json"
}
else {
    Resolve-FullPath $SummaryPath
}
$summaryDirectory = Split-Path -Parent $summaryFullPath
if (-not [string]::IsNullOrWhiteSpace($summaryDirectory)) {
    [System.IO.Directory]::CreateDirectory($summaryDirectory) | Out-Null
}
if (Test-Path -LiteralPath $summaryFullPath -PathType Leaf) {
    Remove-Item -LiteralPath $summaryFullPath -Force
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

if ($SkipLoaderPreflight) {
    [pscustomobject]@{
        skipped            = $true
        reason             = "Skipped by -SkipLoaderPreflight."
        runtimeJson        = $runtimeJsonFullPath
        requiredExtensions = $requiredExtensions
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reports "openxr-loader-preflight.json") -Encoding UTF8
}
elseif ($Renderer -eq "Vulkan") {
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
            -SimulatedHmdPoseMode $SimulatedHmdPoseMode `
            -MarkerPath $serviceMarker `
            -LogDirectory (Join-Path $logs "monado-service")
        $serviceResult | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reports "monado-service-start.json") -Encoding UTF8
        if ($RequireOwnedService -and
            (-not $serviceResult.OwnedByRunner -or
             [string]$serviceResult.SimulatedHmdPoseMode -cne $SimulatedHmdPoseMode)) {
            throw "Deterministic Monado validation requires a runner-owned service with simulated HMD pose mode '$SimulatedHmdPoseMode'. Stop the existing monado-service and retry."
        }
        $serviceStarted = $true
    }

    $editorDll = if ([string]::IsNullOrWhiteSpace($EditorDll)) {
        Join-Path $repoRoot "Build\Editor\$Configuration\$Platform\$Configuration\net10.0-windows7.0\XREngine.Editor.dll"
    }
    else {
        Resolve-FullPath $EditorDll
    }
    if (-not (Test-Path -LiteralPath $editorDll -PathType Leaf)) {
        throw "Editor DLL does not exist: $editorDll"
    }

    $previewStereoViews = if ([string]::IsNullOrWhiteSpace($env:XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS)) {
        "1"
    }
    else {
        $env:XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS
    }

    $environment = @{
        XR_RUNTIME_JSON                         = $runtimeJsonFullPath
        XRE_WORLD_MODE                          = "UnitTesting"
        XRE_UNIT_TEST_WORLD_KIND                = "Default"
        XRE_UNIT_TEST_RENDER_API                = $Renderer
        XRE_UNIT_TEST_VR_MODE                   = "MonadoOpenXR"
        XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS   = $previewStereoViews
        XRE_WINDOW_TITLE                        = "XRE OpenXR Monado Smoke"
        XRE_SMOKE_FRAMES                        = [string]$SmokeFrames
        XRE_SMOKE_WARMUP_FRAMES                 = [string]$WarmupFrames
        XRE_OPENXR_SMOKE_SUMMARY                = $summaryFullPath
        XRE_SMOKE_TIMEOUT_SECONDS               = [string]$TimeoutSeconds
        XRE_AGENT_VALIDATION_RUN_ROOT            = $RunRoot
    }
    if (-not [string]::IsNullOrWhiteSpace($openXrLoaderPath)) {
        $environment["PATH"] = "$(Split-Path -Parent $openXrLoaderPath)$([System.IO.Path]::PathSeparator)$env:PATH"
    }
    if (-not [string]::IsNullOrWhiteSpace($editorFirstChanceExceptions)) {
        $environment["XRE_FIRST_CHANCE_EXCEPTIONS"] = $editorFirstChanceExceptions
    }

    $smokeResult = Invoke-EditorSmoke `
        -EditorDll $editorDll `
        -Summary $summaryFullPath `
        -StdoutPath (Join-Path $logs "editor.stdout.log") `
        -StderrPath (Join-Path $logs "editor.stderr.log") `
        -Environment $environment
    $exitCode = [int]$smokeResult.ExitCode

    if (-not (Test-Path -LiteralPath $summaryFullPath -PathType Leaf)) {
        throw "OpenXR smoke summary was not written before editor exit code $exitCode`: $summaryFullPath"
    }

    $summary = Get-Content -LiteralPath $summaryFullPath -Raw | ConvertFrom-Json
    $durableEngineLogDirectory = Join-Path $logs "engine"
    [System.IO.Directory]::CreateDirectory($durableEngineLogDirectory) | Out-Null
    $sourceEngineLogDirectory = [string]$summary.logDirectory
    $copiedEngineLogs = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($sourceEngineLogDirectory) -and
        (Test-Path -LiteralPath $sourceEngineLogDirectory -PathType Container)) {
        foreach ($sourceLog in (Get-ChildItem -LiteralPath $sourceEngineLogDirectory -File -Filter "*.log" -ErrorAction SilentlyContinue)) {
            $destination = Join-Path $durableEngineLogDirectory $sourceLog.Name
            Copy-Item -LiteralPath $sourceLog.FullName -Destination $destination -Force
            $copiedEngineLogs.Add($destination)
        }
    }
    [pscustomobject]@{
        capturedAtUtc = [DateTimeOffset]::UtcNow
        sourceDirectory = $sourceEngineLogDirectory
        durableDirectory = $durableEngineLogDirectory
        copiedFiles = @($copiedEngineLogs)
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $reports "openxr-engine-log-copy.json") -Encoding UTF8

    $summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $reports "openxr-smoke-summary.normalized.json") -Encoding UTF8
    $failures = @(Get-JsonArrayValues $summary.failures)
    $summaryPassed = $failures.Count -eq 0 -and [bool]$summary.teardownCompleted

    if ($exitCode -ne 0) {
        if ($summaryPassed -and [bool]$smokeResult.TerminatedAfterSummary) {
            Write-Host "OpenXR smoke summary passed; terminated lingering editor process after summary."
        }
        elseif ($summaryPassed -and $exitCode -eq -1073740791) {
            Write-Host "OpenXR smoke summary passed; ignoring post-summary editor shutdown exit code $exitCode."
        }
        else {
            Write-Host "OpenXR smoke failed with editor exit code $exitCode."
            if ($failures.Count -gt 0) {
                Write-Host ($failures -join "`n")
            }
            throw "OpenXR smoke editor process failed with exit code $exitCode."
        }
    }
    elseif ([bool]$smokeResult.TerminatedAfterSummary) {
        if ($summaryPassed) {
            Write-Host "OpenXR smoke summary passed; terminated lingering editor process after summary."
        }
        else {
            Write-Host "OpenXR smoke summary failed before lingering editor process was terminated."
            if ($failures.Count -gt 0) {
                Write-Host ($failures -join "`n")
            }
            throw "OpenXR smoke summary failed before the lingering editor process was terminated."
        }
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
    return
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
