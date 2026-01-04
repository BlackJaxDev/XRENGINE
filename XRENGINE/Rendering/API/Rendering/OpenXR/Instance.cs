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

public unsafe partial class OpenXRAPI
{
    private Instance _instance;

    private void DestroyInstance()
        => Api?.DestroyInstance(_instance);

    private void CreateInstance()
    {
        var appInfo = MakeAppInfo();
        var renderer = Window?.Renderer is VulkanRenderer ? ERenderer.Vulkan : ERenderer.OpenGL;
        var requiredExtensions = GetRequiredExtensions(renderer);

        var availableExtensions = GetAvailableInstanceExtensions();

        // Filter out unsupported optional extensions so instance creation doesn't fail.
        var filtered = requiredExtensions
            .Where(e => availableExtensions.Contains(e))
            .ToArray();

        var dropped = requiredExtensions
            .Except(filtered, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (dropped.Length > 0)
            Console.WriteLine($"OpenXR instance extensions not supported by runtime and will be skipped: {string.Join(", ", dropped)}");

        // Fail fast if the renderer binding extension is missing; without it we cannot create a graphics-bound session.
        var requiredForRenderer = renderer == ERenderer.Vulkan
            ? new[] { "XR_KHR_vulkan_enable", "XR_KHR_vulkan_enable2" }
            : new[] { "XR_KHR_opengl_enable" };

        if (!filtered.Any(e => requiredForRenderer.Contains(e, StringComparer.OrdinalIgnoreCase)))
            throw new Exception($"OpenXR runtime does not support required renderer extension(s): {string.Join(", ", requiredForRenderer)}");

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
        MakeInstance(createInfo);
        Free(createInfo);
    }

    private HashSet<string> GetAvailableInstanceExtensions()
    {
        uint count = 0;
        Api!.EnumerateInstanceExtensionProperties((byte*)null, 0, ref count, null);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (count == 0)
            return set;

        var props = new ExtensionProperties[count];
        for (int i = 0; i < props.Length; i++)
            props[i].Type = StructureType.ExtensionProperties;

        fixed (ExtensionProperties* propsPtr = props)
        {
            Api!.EnumerateInstanceExtensionProperties((byte*)null, count, ref count, propsPtr);
        }

        for (int i = 0; i < count; i++)
        {
            fixed (byte* namePtr = props[i].ExtensionName)
            {
                var name = Marshal.PtrToStringAnsi((nint)namePtr);
                if (!string.IsNullOrWhiteSpace(name))
                    set.Add(name);
            }
        }

        return set;
    }

    private bool IsInstanceExtensionAvailable(string extensionName)
    {
        uint count = 0;
        Api!.EnumerateInstanceExtensionProperties((byte*)null, 0, ref count, null);
        if (count == 0)
            return false;

        var props = new ExtensionProperties[count];
        for (int i = 0; i < props.Length; i++)
            props[i].Type = StructureType.ExtensionProperties;

        fixed (ExtensionProperties* propsPtr = props)
        {
            Api!.EnumerateInstanceExtensionProperties((byte*)null, count, ref count, propsPtr);
        }

        for (int i = 0; i < count; i++)
        {
            fixed (byte* namePtr = props[i].ExtensionName)
            {
                var name = Marshal.PtrToStringAnsi((nint)namePtr);
                if (string.Equals(name, extensionName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    private void MakeInstance(InstanceCreateInfo createInfo)
    {
        Instance i = default;
        Result result = Api!.CreateInstance(&createInfo, &i);
        if (result != Result.Success)
            throw new Exception(BuildCreateInstanceFailureMessage(result, createInfo));
        _instance = i;
    }

    private static string BuildCreateInstanceFailureMessage(Result result, InstanceCreateInfo createInfo)
    {
        var sb = new StringBuilder();
        sb.Append($"Failed to create OpenXR instance. Result={result}");

        var runtimeJsonEnv = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
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
            createInfo.Next = null;
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