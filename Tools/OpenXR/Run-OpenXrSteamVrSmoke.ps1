[CmdletBinding()]
param(
    [Parameter()]
    [string]$RuntimeJson,

    [Parameter()]
    [switch]$UseActiveRuntime,

    [Parameter()]
    [ValidateSet("OpenGL", "Vulkan")]
    [string]$Renderer = "Vulkan",

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
    [switch]$SkipLoaderPreflight,

    [Parameter()]
    [switch]$SkipAllocationAudit,

    [Parameter()]
    [switch]$NoSteamVrStart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))

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
        $resolved = Join-Path $agentRootFull "$stamp-openxr-steamvr-smoke"
    }

    [System.IO.Directory]::CreateDirectory($resolved) | Out-Null
    foreach ($child in @("logs", "reports", "temp-build", "scratch")) {
        [System.IO.Directory]::CreateDirectory((Join-Path $resolved $child)) | Out-Null
    }

    return [System.IO.Path]::GetFullPath($resolved)
}

function Get-RegistryActiveOpenXrRuntime {
    $candidates = @(
        @{ Hive = "HKCU"; Path = "HKCU:\Software\Khronos\OpenXR\1" },
        @{ Hive = "HKLM"; Path = "HKLM:\SOFTWARE\Khronos\OpenXR\1" }
    )

    foreach ($candidate in $candidates) {
        try {
            $property = Get-ItemProperty -LiteralPath $candidate.Path -Name "ActiveRuntime" -ErrorAction Stop
            if (-not [string]::IsNullOrWhiteSpace([string]$property.ActiveRuntime)) {
                return [pscustomobject]@{
                    hive = $candidate.Hive
                    key  = $candidate.Path
                    path = [string]$property.ActiveRuntime
                }
            }
        }
        catch {
        }
    }

    return $null
}

function Read-OpenXrRuntimeManifest {
    param([AllowNull()][string]$ManifestPath)

    if ([string]::IsNullOrWhiteSpace($ManifestPath) -or -not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) {
        return [pscustomobject]@{
            path        = $ManifestPath
            exists      = $false
            runtimeName = $null
            libraryPath = $null
            kind        = "Unknown"
        }
    }

    $json = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $runtime = $json.runtime
    $runtimeName = if ($null -ne $runtime -and $null -ne $runtime.name) { [string]$runtime.name } else { $null }
    $libraryPath = if ($null -ne $runtime -and $null -ne $runtime.library_path) { [string]$runtime.library_path } else { $null }
    if (-not [string]::IsNullOrWhiteSpace($libraryPath) -and -not [System.IO.Path]::IsPathRooted($libraryPath)) {
        $libraryPath = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $ManifestPath) $libraryPath))
    }

    $probe = "$ManifestPath $runtimeName $libraryPath"
    $kind = if ($probe -match '(?i)steamvr|steam\\steamapps\\common\\steamvr|vrclient') {
        "SteamVR"
    }
    elseif ($probe -match '(?i)monado') {
        "Monado"
    }
    elseif ($probe -match '(?i)oculus|meta') {
        "Oculus"
    }
    elseif ($probe -match '(?i)mixedreality|windows mixed reality|wmr') {
        "WindowsMixedReality"
    }
    else {
        "Other"
    }

    return [pscustomobject]@{
        path        = [System.IO.Path]::GetFullPath($ManifestPath)
        exists      = $true
        runtimeName = $runtimeName
        libraryPath = $libraryPath
        kind        = $kind
    }
}

function Resolve-SelectedRuntime {
    param(
        [string]$RequestedRuntimeJson,
        [switch]$ForceActiveRuntime
    )

    if ($ForceActiveRuntime -and -not [string]::IsNullOrWhiteSpace($RequestedRuntimeJson)) {
        throw "Use either -RuntimeJson or -UseActiveRuntime, not both."
    }

    $registryRuntime = Get-RegistryActiveOpenXrRuntime
    if ($ForceActiveRuntime) {
        return [pscustomobject]@{
            selectionMode       = "WindowsActiveRuntime"
            runtimeJson         = if ($null -ne $registryRuntime) { [string]$registryRuntime.path } else { $null }
            childRuntimeJson    = $null
            registryActive      = $registryRuntime
            inheritedRuntimeJson = $env:XR_RUNTIME_JSON
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($RequestedRuntimeJson)) {
        $resolved = Resolve-FullPath $RequestedRuntimeJson
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Requested OpenXR runtime manifest does not exist: $resolved"
        }

        return [pscustomobject]@{
            selectionMode       = "RuntimeJsonArgument"
            runtimeJson         = $resolved
            childRuntimeJson    = $resolved
            registryActive      = $registryRuntime
            inheritedRuntimeJson = $env:XR_RUNTIME_JSON
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:XR_RUNTIME_JSON)) {
        $resolved = Resolve-FullPath $env:XR_RUNTIME_JSON
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Process XR_RUNTIME_JSON points at a missing manifest: $resolved"
        }

        return [pscustomobject]@{
            selectionMode       = "InheritedXR_RUNTIME_JSON"
            runtimeJson         = $resolved
            childRuntimeJson    = $resolved
            registryActive      = $registryRuntime
            inheritedRuntimeJson = $env:XR_RUNTIME_JSON
        }
    }

    return [pscustomobject]@{
        selectionMode       = "WindowsActiveRuntime"
        runtimeJson         = if ($null -ne $registryRuntime) { [string]$registryRuntime.path } else { $null }
        childRuntimeJson    = $null
        registryActive      = $registryRuntime
        inheritedRuntimeJson = $env:XR_RUNTIME_JSON
    }
}

function Find-OpenXrLoaderPath {
    $editorOutput = Join-Path $repoRoot "Build\Editor\$Configuration\$Platform\$Configuration\net10.0-windows7.0\openxr_loader.dll"
    $candidates = @(
        (Join-Path $repoRoot "Build\Dependencies\vcpkg\installed\x64-windows\bin\openxr_loader.dll"),
        $editorOutput,
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\SteamVR\bin\win64\openxr_loader.dll",
        "$env:ProgramFiles\Steam\steamapps\common\SteamVR\bin\win64\openxr_loader.dll",
        "$env:SystemRoot\System32\openxr_loader.dll"
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
        [AllowNull()][string]$RuntimeManifest,
        [Parameter()][string[]]$RequiredExtensions = @(),
        [Parameter(Mandatory)][string]$ReportPath
    )

    $previousRuntimeJson = $env:XR_RUNTIME_JSON
    $previousPath = $env:PATH
    $setDllDirectory = $false
    $loadedOpenXrLoader = $false
    $loaderDirectory = $null
    try {
        if (-not [string]::IsNullOrWhiteSpace($RuntimeManifest)) {
            $env:XR_RUNTIME_JSON = $RuntimeManifest
        }
        else {
            Remove-Item Env:\XR_RUNTIME_JSON -ErrorAction SilentlyContinue
        }

        $loaderPath = Find-OpenXrLoaderPath
        if (-not [string]::IsNullOrWhiteSpace($loaderPath)) {
            $loaderDirectory = Split-Path -Parent $loaderPath
            $env:PATH = "$loaderDirectory$([System.IO.Path]::PathSeparator)$previousPath"
        }

        Ensure-OpenXrPreflightType
        if (-not [string]::IsNullOrWhiteSpace($loaderDirectory)) {
            [OpenXrLoaderPreflight]::SetDllSearchDirectory($loaderDirectory)
            $setDllDirectory = $true
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

        if ([string]::IsNullOrWhiteSpace($previousRuntimeJson)) {
            Remove-Item Env:\XR_RUNTIME_JSON -ErrorAction SilentlyContinue
        }
        else {
            $env:XR_RUNTIME_JSON = $previousRuntimeJson
        }
        $env:PATH = $previousPath
    }
}

function Get-SteamVrProcessState {
    $vrserver = Get-Process -Name "vrserver" -ErrorAction SilentlyContinue
    $vrmonitor = Get-Process -Name "vrmonitor" -ErrorAction SilentlyContinue

    return [pscustomobject]@{
        vrserverRunning  = $null -ne $vrserver
        vrmonitorRunning = $null -ne $vrmonitor
        vrserverIds      = @($vrserver | ForEach-Object { $_.Id })
        vrmonitorIds     = @($vrmonitor | ForEach-Object { $_.Id })
    }
}

function Find-SteamVrStartupExe {
    param([AllowNull()][object]$RuntimeManifestInfo)

    $candidates = @()
    if ($null -ne $RuntimeManifestInfo -and -not [string]::IsNullOrWhiteSpace([string]$RuntimeManifestInfo.libraryPath)) {
        $libraryDirectory = Split-Path -Parent ([string]$RuntimeManifestInfo.libraryPath)
        $candidates += (Join-Path $libraryDirectory "vrstartup.exe")
        $candidates += (Join-Path $libraryDirectory "..\vrstartup.exe")
        $candidates += (Join-Path $libraryDirectory "..\..\bin\win64\vrstartup.exe")
    }

    $candidates += "${env:ProgramFiles(x86)}\Steam\steamapps\common\SteamVR\bin\win64\vrstartup.exe"
    $candidates += "$env:ProgramFiles\Steam\steamapps\common\SteamVR\bin\win64\vrstartup.exe"

    foreach ($candidate in ($candidates | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)) {
        $full = [System.IO.Path]::GetFullPath($candidate)
        if (Test-Path -LiteralPath $full -PathType Leaf) {
            return $full
        }
    }

    return $null
}

function Start-SteamVrBestEffort {
    param([AllowNull()][object]$RuntimeManifestInfo)

    $before = Get-SteamVrProcessState
    if ($before.vrserverRunning -or $before.vrmonitorRunning) {
        return [pscustomobject]@{
            attempted = $false
            method    = "AlreadyRunning"
            before    = $before
            after     = $before
        }
    }

    $startupExe = Find-SteamVrStartupExe -RuntimeManifestInfo $RuntimeManifestInfo
    $method = $null
    try {
        if (-not [string]::IsNullOrWhiteSpace($startupExe)) {
            Start-Process -FilePath $startupExe -WindowStyle Hidden | Out-Null
            $method = "vrstartup.exe"
        }
        else {
            Start-Process -FilePath "steam://rungameid/250820" | Out-Null
            $method = "steam-uri"
        }
    }
    catch {
        return [pscustomobject]@{
            attempted = $true
            method    = if ($null -ne $method) { $method } else { "none" }
            error     = $_.Exception.Message
            before    = $before
            after     = Get-SteamVrProcessState
        }
    }

    Start-Sleep -Seconds 5
    return [pscustomobject]@{
        attempted = $true
        method    = $method
        before    = $before
        after     = Get-SteamVrProcessState
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
        if ($null -eq $entry.Value) {
            $psi.Environment.Remove($entry.Key) | Out-Null
        }
        else {
            $psi.Environment[$entry.Key] = [string]$entry.Value
        }
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

$selection = Resolve-SelectedRuntime -RequestedRuntimeJson $RuntimeJson -ForceActiveRuntime:$UseActiveRuntime
$runtimeJsonFullPath = if ([string]::IsNullOrWhiteSpace([string]$selection.runtimeJson)) { $null } else { Resolve-FullPath ([string]$selection.runtimeJson) }
$runtimeInfo = Read-OpenXrRuntimeManifest -ManifestPath $runtimeJsonFullPath
$openXrLoaderPath = Find-OpenXrLoaderPath
$processStateBeforeStart = Get-SteamVrProcessState

if ($runtimeInfo.kind -ne "SteamVR") {
    Write-Warning "Selected OpenXR runtime appears to be '$($runtimeInfo.kind)', not SteamVR. Manifest='$runtimeJsonFullPath'. Use -UseActiveRuntime with SteamVR set as the active runtime, or pass SteamVR's openxr_runtime.json with -RuntimeJson."
}

Write-Host "SteamVR OpenXR smoke diagnostics:"
Write-Host "  Runtime selection: $($selection.selectionMode)"
Write-Host "  Selected runtime manifest: $runtimeJsonFullPath"
Write-Host "  Process XR_RUNTIME_JSON: $($selection.inheritedRuntimeJson)"
Write-Host "  Windows active runtime: $(if ($null -ne $selection.registryActive) { $selection.registryActive.path } else { '<none>' })"
Write-Host "  Resolved openxr_loader.dll: $openXrLoaderPath"
Write-Host "  Renderer backend: $Renderer"
Write-Host "  vrserver running: $($processStateBeforeStart.vrserverRunning)"
Write-Host "  vrmonitor running: $($processStateBeforeStart.vrmonitorRunning)"

$steamVrStartResult = if ($NoSteamVrStart) {
    [pscustomobject]@{
        attempted = $false
        method    = "SkippedByNoSteamVrStart"
        before    = $processStateBeforeStart
        after     = $processStateBeforeStart
    }
}
else {
    Start-SteamVrBestEffort -RuntimeManifestInfo $runtimeInfo
}

$diagnostics = [pscustomobject]@{
    schemaVersion                    = 1
    lane                             = "SteamVROpenXR"
    runtimeSelection                 = $selection.selectionMode
    selectedRuntimeManifest          = $runtimeJsonFullPath
    processXrRuntimeJson             = $selection.inheritedRuntimeJson
    childXrRuntimeJson               = $selection.childRuntimeJson
    windowsActiveRuntime             = $selection.registryActive
    runtimeManifest                  = $runtimeInfo
    openXrLoaderPath                 = $openXrLoaderPath
    rendererBackend                  = $Renderer
    steamVrProcessStateBeforeLaunch  = $processStateBeforeStart
    steamVrStartResult               = $steamVrStartResult
    vrMode                           = "OpenXR"
    monadoServiceRecoveryInstalled   = $false
}
$diagnostics | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $reports "steamvr-openxr-startup-diagnostics.json") -Encoding UTF8

$summaryFullPath = if ([string]::IsNullOrWhiteSpace($SummaryPath)) {
    Join-Path $reports "openxr-steamvr-smoke-summary.json"
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
        -RuntimeManifest ([string]$selection.childRuntimeJson) `
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
        -RuntimeManifest ([string]$selection.childRuntimeJson) `
        -RequiredExtensions $requiredExtensions `
        -ReportPath (Join-Path $reports "openxr-loader-preflight.json") | Out-Null
}

$editorDll = Join-Path $repoRoot "Build\Editor\$Configuration\$Platform\$Configuration\net10.0-windows7.0\XREngine.Editor.dll"
if (-not (Test-Path -LiteralPath $editorDll -PathType Leaf)) {
    throw "Editor DLL does not exist: $editorDll"
}

$environment = @{
    XR_RUNTIME_JSON                       = if ([string]::IsNullOrWhiteSpace([string]$selection.childRuntimeJson)) { $null } else { [string]$selection.childRuntimeJson }
    XRE_WORLD_MODE                        = "UnitTesting"
    XRE_UNIT_TEST_WORLD_KIND              = "Default"
    XRE_UNIT_TEST_RENDER_API              = $Renderer
    XRE_UNIT_TEST_VR_MODE                 = "OpenXR"
    XRE_UNIT_TEST_PREVIEW_VR_STEREO_VIEWS = "1"
    XRE_OCCLUSION_CULLING_MODE            = "Disabled"
    XRE_WINDOW_TITLE                      = "XRE OpenXR SteamVR Smoke"
    XRE_SMOKE_FRAMES                      = [string]$SmokeFrames
    XRE_OPENXR_SMOKE_SUMMARY              = $summaryFullPath
    XRE_SMOKE_TIMEOUT_SECONDS             = [string]$TimeoutSeconds
}
if (-not [string]::IsNullOrWhiteSpace($openXrLoaderPath)) {
    $environment["PATH"] = "$(Split-Path -Parent $openXrLoaderPath)$([System.IO.Path]::PathSeparator)$env:PATH"
}

$smokeResult = Invoke-EditorSmoke `
    -EditorDll $editorDll `
    -Summary $summaryFullPath `
    -StdoutPath (Join-Path $logs "editor.stdout.log") `
    -StderrPath (Join-Path $logs "editor.stderr.log") `
    -Environment $environment
$exitCode = [int]$smokeResult.ExitCode

if (-not (Test-Path -LiteralPath $summaryFullPath -PathType Leaf)) {
    throw "OpenXR SteamVR smoke summary was not written: $summaryFullPath. Check SteamVR status, active runtime, HMD availability, and logs in $logs."
}

$summary = Get-Content -LiteralPath $summaryFullPath -Raw | ConvertFrom-Json
$summary | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $reports "openxr-steamvr-smoke-summary.normalized.json") -Encoding UTF8
$failures = @(Get-JsonArrayValues $summary.failures)
$summaryPassed = $failures.Count -eq 0 -and [bool]$summary.teardownCompleted

if ($exitCode -ne 0) {
    if ($summaryPassed -and [bool]$smokeResult.TerminatedAfterSummary) {
        Write-Host "OpenXR SteamVR smoke summary passed; terminated lingering editor process after summary."
    }
    elseif ($summaryPassed -and $exitCode -eq -1073740791) {
        Write-Host "OpenXR SteamVR smoke summary passed; ignoring post-summary editor shutdown exit code $exitCode."
    }
    else {
        Write-Host "OpenXR SteamVR smoke failed with editor exit code $exitCode."
        if ($failures.Count -gt 0) {
            Write-Host ($failures -join "`n")
        }
        exit $exitCode
    }
}
elseif ([bool]$smokeResult.TerminatedAfterSummary) {
    if ($summaryPassed) {
        Write-Host "OpenXR SteamVR smoke summary passed; terminated lingering editor process after summary."
    }
    else {
        Write-Host "OpenXR SteamVR smoke summary failed before lingering editor process was terminated."
        if ($failures.Count -gt 0) {
            Write-Host ($failures -join "`n")
        }
        exit 1
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

Write-Host "OpenXR SteamVR smoke passed. RunRoot=$RunRoot Summary=$summaryFullPath"
exit 0
