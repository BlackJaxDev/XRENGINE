using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;
using System.Runtime.InteropServices;
using System.Text;
using XREngine.Rendering.Vulkan;
using System.Linq;
using OxrExtDebugUtils = global::Silk.NET.OpenXR.Extensions.EXT.ExtDebugUtils;
using System.Collections.Generic;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private Instance _instance;
    private bool _instanceOwnedByRenderer;
    private bool _apiOwnedByRenderer;
    private readonly HashSet<string> _availableInstanceExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enabledInstanceExtensions = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastUnsupportedOptionalExtensionReportKey;

    private readonly record struct OpenXrInstanceCreationAttempt(
        bool Succeeded,
        string Operation,
        Result Result,
        string FailureReason)
    {
        public static OpenXrInstanceCreationAttempt Success()
            => new(true, "xrCreateInstance", Result.Success, string.Empty);

        public static OpenXrInstanceCreationAttempt Failure(string operation, Result result, string failureReason)
            => new(false, operation, result, failureReason);
    }

    private void DestroyInstance()
    {
        if (_instance.Handle == 0)
            return;

        if (!_instanceOwnedByRenderer)
        {
            Api?.DestroyInstance(_instance);
        }
        else if (Window?.Renderer is VulkanRenderer vulkanRenderer &&
            vulkanRenderer.InvalidateOpenXrVulkanEnable2BootstrapInstance("OpenXR runtime instance teardown"))
        {
            Debug.VulkanWarning("[OpenXR] Dropped stale renderer-owned XR instance so runtime recovery can create a fresh session instance.");
        }

        _instanceOwnedByRenderer = false;
        if (_apiOwnedByRenderer)
        {
            Api = XR.GetApi();
            _apiOwnedByRenderer = false;
        }

        ClearInstanceExtensionState();
    }

    private OpenXrInstanceCreationAttempt TryCreateInstance()
    {
        EnsureSteamVrRunningIfActiveRuntime();

        var appInfo = MakeAppInfo();
        var renderer = Window?.Renderer is VulkanRenderer ? ERenderer.Vulkan : ERenderer.OpenGL;

        if (Window?.Renderer is VulkanRenderer vulkanRenderer &&
            vulkanRenderer.TryGetOpenXrVulkanEnable2BootstrapInstance(
                out XR rendererOwnedApi,
                out Instance rendererOwnedInstance,
                out string[] rendererOwnedExtensions))
        {
            if (!ReferenceEquals(Api, rendererOwnedApi))
            {
                if (!_apiOwnedByRenderer)
                    Api.Dispose();

                Api = rendererOwnedApi;
            }

            _instance = rendererOwnedInstance;
            _instanceOwnedByRenderer = true;
            _apiOwnedByRenderer = true;
            if (!TryGetAvailableInstanceExtensions(out HashSet<string> rendererAvailableExtensions, out _))
                rendererAvailableExtensions = new HashSet<string>(rendererOwnedExtensions, StringComparer.OrdinalIgnoreCase);
            SetInstanceExtensionState(rendererAvailableExtensions, rendererOwnedExtensions);
            RecordSmokeInstanceCreated(renderer.ToString(), rendererOwnedExtensions);
            Debug.Vulkan("[OpenXR] Reusing renderer-owned XR_KHR_vulkan_enable2 instance for OpenXR session creation.");
            return OpenXrInstanceCreationAttempt.Success();
        }

        _instanceOwnedByRenderer = false;
        if (_apiOwnedByRenderer)
            Api = XR.GetApi();

        _apiOwnedByRenderer = false;
        string[] requiredRendererAlternatives = GetRequiredRendererExtensionAlternatives(renderer);
        string[] requestedExtensions = GetRequestedExtensions(renderer);

        if (!TryGetAvailableInstanceExtensions(out HashSet<string> availableExtensions, out Result enumerateResult))
        {
            return OpenXrInstanceCreationAttempt.Failure(
                "xrEnumerateInstanceExtensionProperties",
                enumerateResult,
                $"xrEnumerateInstanceExtensionProperties failed: {enumerateResult}");
        }

        // Filter out unsupported optional extensions so instance creation doesn't fail.
        string[] filtered = requestedExtensions
            .Where(e => availableExtensions.Contains(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] droppedOptional = requestedExtensions
            .Except(filtered, StringComparer.OrdinalIgnoreCase)
            .Except(requiredRendererAlternatives, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ReportUnsupportedOptionalExtensionsOnce(renderer, droppedOptional);

        // Fail fast if the renderer binding extension is missing; without it we cannot create a graphics-bound session.
        if (!filtered.Any(e => requiredRendererAlternatives.Contains(e, StringComparer.OrdinalIgnoreCase)))
        {
            return OpenXrInstanceCreationAttempt.Failure(
                "renderer-extension-negotiation",
                Result.ErrorExtensionNotPresent,
                "OpenXR runtime does not support any required renderer binding extension. " +
                $"Renderer={renderer}; RequiredOneOf=[{string.Join(", ", requiredRendererAlternatives)}]");
        }

        Debug.Vulkan("[OpenXR] Enabling OpenXR instance extensions: {0}", string.Join(", ", filtered));

        DebugUtilsMessengerCreateInfoEXT debugCreateInfo = default;
        void* next = null;
        if (EnableValidationLayers && filtered.Contains(OxrExtDebugUtils.ExtensionName, StringComparer.OrdinalIgnoreCase))
        {
            PopulateDebugMessengerCreateInfo(ref debugCreateInfo);
            next = &debugCreateInfo;
        }

        var enabledLayers = (EnableValidationLayers && _validationLayers.Length > 0)
            ? _validationLayers
            : null;

        var createInfo = MakeCreateInfo(appInfo, filtered, enabledLayers, next);
        try
        {
            Result createResult = MakeInstance(createInfo);
            if (createResult != Result.Success)
            {
                return OpenXrInstanceCreationAttempt.Failure(
                    "xrCreateInstance",
                    createResult,
                    BuildCreateInstanceFailureMessage(createResult, createInfo));
            }

            SetInstanceExtensionState(availableExtensions, filtered);
            RecordSmokeInstanceCreated(renderer.ToString(), filtered);
            return OpenXrInstanceCreationAttempt.Success();
        }
        finally
        {
            Free(createInfo);
        }
    }

    private void ReportUnsupportedOptionalExtensionsOnce(ERenderer renderer, string[] droppedOptional)
    {
        if (droppedOptional.Length == 0)
            return;

        Array.Sort(droppedOptional, StringComparer.OrdinalIgnoreCase);
        string reportKey = $"{renderer}:{string.Join('|', droppedOptional)}";
        if (string.Equals(_lastUnsupportedOptionalExtensionReportKey, reportKey, StringComparison.Ordinal))
            return;

        _lastUnsupportedOptionalExtensionReportKey = reportKey;
        Debug.VR(
            "[OpenXR] Runtime capability summary: optional instance extensions unavailable and disabled. " +
            $"Renderer={renderer}; Extensions=[{string.Join(", ", droppedOptional)}]");
    }

    private static void EnsureSteamVrRunningIfActiveRuntime()
    {
        if (!OperatingSystem.IsWindows())
            return;

        // Prefer XR_RUNTIME_JSON if set, otherwise registry ActiveRuntime.
        var runtimeJson = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        var activeRuntime = !string.IsNullOrWhiteSpace(runtimeJson)
            ? runtimeJson
            : GetActiveOpenXRRuntimePathWindows();

        if (string.IsNullOrWhiteSpace(activeRuntime))
            return;

        if (!IsSteamVrRuntimePath(activeRuntime))
            return;

        if (IsSteamVrRunning())
            return;

        if (TryLaunchSteamVrFromRuntimeJson(activeRuntime))
        {
            // Give SteamVR a moment to spin up before instance creation.
            WaitForSteamVrRunning(TimeSpan.FromSeconds(10));
        }
    }

    private static bool IsSteamVrRuntimePath(string runtimePath)
        => runtimePath.Contains("steamvr", StringComparison.OrdinalIgnoreCase) ||
           runtimePath.Contains("steamxr", StringComparison.OrdinalIgnoreCase);

    private static bool IsSteamVrRunning()
        => Process.GetProcessesByName("vrserver").Length > 0 ||
           Process.GetProcessesByName("vrmonitor").Length > 0;

    private static void WaitForSteamVrRunning(TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < timeout)
        {
            if (IsSteamVrRunning())
                return;
            Thread.Sleep(200);
        }
    }

    private static bool TryLaunchSteamVrFromRuntimeJson(string runtimeJsonPath)
    {
        try
        {
            if (!File.Exists(runtimeJsonPath))
                return TryLaunchSteamVrViaSteamUri();

            var root = Path.GetDirectoryName(runtimeJsonPath);
            if (string.IsNullOrWhiteSpace(root))
                return TryLaunchSteamVrViaSteamUri();

            // SteamVR's OpenXR JSON typically sits in the SteamVR root folder.
            // Prefer launching vrmonitor.exe (UI) which will start vrserver as needed.
            var vrmonitor = Path.Combine(root, "bin", "win64", "vrmonitor.exe");
            var vrserver = Path.Combine(root, "bin", "win64", "vrserver.exe");

            string? exe = null;
            if (File.Exists(vrmonitor))
                exe = vrmonitor;
            else if (File.Exists(vrserver))
                exe = vrserver;

            if (exe == null)
                return TryLaunchSteamVrViaSteamUri();

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? root,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return TryLaunchSteamVrViaSteamUri();
        }
    }

    private static bool TryLaunchSteamVrViaSteamUri()
    {
        try
        {
            // SteamVR app id is 250820.
            Process.Start(new ProcessStartInfo
            {
                FileName = "steam://rungameid/250820",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetAvailableInstanceExtensions(out HashSet<string> extensions, out Result failureResult)
    {
        extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint count = 0;
        failureResult = Api!.EnumerateInstanceExtensionProperties((byte*)null, 0, ref count, null);
        if (failureResult != Result.Success)
            return false;

        if (count == 0)
            return true;

        var props = new ExtensionProperties[count];
        for (int i = 0; i < props.Length; i++)
            props[i].Type = StructureType.ExtensionProperties;

        fixed (ExtensionProperties* propsPtr = props)
        {
            failureResult = Api!.EnumerateInstanceExtensionProperties((byte*)null, count, ref count, propsPtr);
        }
        if (failureResult != Result.Success)
            return false;

        for (int i = 0; i < count; i++)
        {
            fixed (byte* namePtr = props[i].ExtensionName)
            {
                var name = Marshal.PtrToStringAnsi((nint)namePtr);
                if (!string.IsNullOrWhiteSpace(name))
                    extensions.Add(name);
            }
        }

        return true;
    }

    private void SetInstanceExtensionState(IEnumerable<string> availableExtensions, IEnumerable<string> enabledExtensions)
    {
        _availableInstanceExtensions.Clear();
        foreach (string extension in availableExtensions)
            if (!string.IsNullOrWhiteSpace(extension))
                _availableInstanceExtensions.Add(extension);

        _enabledInstanceExtensions.Clear();
        foreach (string extension in enabledExtensions)
            if (!string.IsNullOrWhiteSpace(extension))
                _enabledInstanceExtensions.Add(extension);

        ResetOpenXrOptionalExtensionCaches();
    }

    private void ClearInstanceExtensionState()
    {
        _availableInstanceExtensions.Clear();
        _enabledInstanceExtensions.Clear();
        ResetOpenXrOptionalExtensionCaches();
    }

    private bool IsInstanceExtensionEnabled(string extensionName)
        => _enabledInstanceExtensions.Contains(extensionName);

    private string DescribeInstanceExtensionState(string extensionName)
    {
        if (_enabledInstanceExtensions.Contains(extensionName))
            return "enabled";

        return _availableInstanceExtensions.Contains(extensionName)
            ? "runtime advertised the extension, but this OpenXR instance did not enable it"
            : "runtime did not advertise the extension";
    }

    private Result MakeInstance(InstanceCreateInfo createInfo)
    {
        Instance i = default;
        Result result = Api!.CreateInstance(&createInfo, &i);
        if (result == Result.Success)
            _instance = i;
        return result;
    }

    private static string BuildCreateInstanceFailureMessage(Result result, InstanceCreateInfo createInfo)
    {
        var sb = new StringBuilder();
        sb.Append($"Failed to create OpenXR instance. Result={result}");

        var runtimeJsonEnv = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        if (!string.IsNullOrWhiteSpace(runtimeJsonEnv))
            sb.Append($"\nXR_RUNTIME_JSON={runtimeJsonEnv}");

        var activeRuntime = GetActiveOpenXRRuntimePathWindows();
        if (!string.IsNullOrWhiteSpace(activeRuntime))
        {
            sb.Append($"\nActiveRuntime={activeRuntime}");

            // Provide actionable guidance for common runtimes
            if (activeRuntime.Contains("SteamVR", StringComparison.OrdinalIgnoreCase) ||
                activeRuntime.Contains("steamxr", StringComparison.OrdinalIgnoreCase))
            {
                bool steamVRRunning = System.Diagnostics.Process.GetProcessesByName("vrserver").Length > 0 ||
                                      System.Diagnostics.Process.GetProcessesByName("vrmonitor").Length > 0;
                if (!steamVRRunning)
                    sb.Append("\n*** SteamVR is set as the active OpenXR runtime but is not running. Please start SteamVR first. ***");
            }
            else if (activeRuntime.Contains("oculus", StringComparison.OrdinalIgnoreCase) ||
                     activeRuntime.Contains("meta", StringComparison.OrdinalIgnoreCase))
            {
                bool oculusRunning = System.Diagnostics.Process.GetProcessesByName("OVRServer_x64").Length > 0 ||
                                     System.Diagnostics.Process.GetProcessesByName("OculusClient").Length > 0;
                if (!oculusRunning)
                    sb.Append("\n*** Oculus/Meta is set as the active OpenXR runtime but does not appear to be running. Please start the Oculus app first. ***");
            }
        }

        var enabledExtensions = ReadStringArray(createInfo.EnabledExtensionNames, createInfo.EnabledExtensionCount);
        if (enabledExtensions.Length > 0)
            sb.Append($"\nEnabledExtensions=({enabledExtensions.Length}) {string.Join(", ", enabledExtensions)}");
        else
            sb.Append("\nEnabledExtensions=(0)");

        var enabledLayers = ReadStringArray(createInfo.EnabledApiLayerNames, createInfo.EnabledApiLayerCount);
        if (enabledLayers.Length > 0)
            sb.Append($"\nEnabledApiLayers=({enabledLayers.Length}) {string.Join(", ", enabledLayers)}");
        else
            sb.Append("\nEnabledApiLayers=(0)");

        return sb.ToString();
    }

    private static string[] ReadStringArray(byte** strings, uint count)
    {
        if (strings == null || count == 0)
            return [];

        var list = new List<string>((int)count);
        for (nuint i = 0; i < count; i++)
        {
            var s = Marshal.PtrToStringAnsi((nint)strings[i]);
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s);
        }
        return [.. list];
    }

    private static string? GetActiveOpenXRRuntimePathWindows()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        const string subKey = @"SOFTWARE\Khronos\OpenXR\1";

        string? TryRead(RegistryHive hive, RegistryView view)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(subKey);
                return key?.GetValue("ActiveRuntime") as string;
            }
            catch
            {
                return null;
            }
        }

        return
            TryRead(RegistryHive.CurrentUser, RegistryView.Registry64) ??
            TryRead(RegistryHive.LocalMachine, RegistryView.Registry64) ??
            TryRead(RegistryHive.CurrentUser, RegistryView.Registry32) ??
            TryRead(RegistryHive.LocalMachine, RegistryView.Registry32);
    }

    private static void Free(InstanceCreateInfo createInfo)
    {
        if (createInfo.EnabledExtensionNames != null)
            SilkMarshal.Free((nint)createInfo.EnabledExtensionNames);

        if (createInfo.EnabledApiLayerNames != null)
            SilkMarshal.Free((nint)createInfo.EnabledApiLayerNames);
    }

    private static InstanceCreateInfo MakeCreateInfo(ApplicationInfo appInfo, string[] extensions, string[]? validationLayers, void* next)
    {
        InstanceCreateInfo createInfo = new()
        {
            Type = StructureType.InstanceCreateInfo,
            ApplicationInfo = appInfo,
            EnabledExtensionCount = (uint)extensions.Length,
            EnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions),
            Next = next
        };
        if (validationLayers != null && validationLayers.Length > 0)
        {
            createInfo.EnabledApiLayerCount = (uint)validationLayers.Length;
            createInfo.EnabledApiLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);
        }
        else
        {
            createInfo.EnabledApiLayerCount = 0;
            createInfo.EnabledApiLayerNames = null;
        }
        return createInfo;
    }

    private static ApplicationInfo MakeAppInfo()
    {
        ApplicationInfo appInfo = default;
        appInfo.ApiVersion = new Version64(1, 0, 0);
        appInfo.ApplicationVersion = new Version32(1, 0, 0);
        appInfo.EngineVersion = new Version32(1, 0, 0);

        WriteNullTerminatedAscii("XREngine", appInfo.ApplicationName, 128);
        WriteNullTerminatedAscii("XREngine", appInfo.EngineName, 128);
        return appInfo;
    }

    private static void WriteNullTerminatedAscii(string value, byte* destination, int maxBytes)
    {
        if (maxBytes <= 0)
            return;

        Span<byte> span = new(destination, maxBytes);
        span.Clear();
        int written = Encoding.ASCII.GetBytes(value, span);
        if (written >= maxBytes)
            span[^1] = 0;
        else
            span[written] = 0;
    }
}
