using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;
using System.Runtime.InteropServices;
using System.Text;
using XREngine.Rendering.Vulkan;
using System.Linq;
using OxrExtDebugUtils = global::Silk.NET.OpenXR.Extensions.EXT.ExtDebugUtils;
using System.Collections.Generic;

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

        var createInfo = MakeCreateInfo(appInfo, filtered, EnableValidationLayers ? _validationLayers : null);
        MakeInstance(createInfo);
        Free(appInfo, createInfo);
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
            throw new Exception($"Failed to create OpenXR instance. Result={result}");
        _instance = i;
    }

    private void Free(ApplicationInfo appInfo, InstanceCreateInfo createInfo)
    {
        Marshal.FreeHGlobal((IntPtr)appInfo.ApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.EngineName);
        SilkMarshal.Free((nint)createInfo.EnabledExtensionNames);

        if (EnableValidationLayers)
            SilkMarshal.Free((nint)createInfo.EnabledApiLayerNames);
    }

    private static InstanceCreateInfo MakeCreateInfo(ApplicationInfo appInfo, string[] extensions, string[]? validationLayers)
    {
        InstanceCreateInfo createInfo = new()
        {
            Type = StructureType.InstanceCreateInfo,
            ApplicationInfo = appInfo,
            EnabledExtensionCount = (uint)extensions.Length,
            EnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions)
        };
        if (validationLayers != null)
        {
            createInfo.EnabledApiLayerCount = (uint)validationLayers.Length;
            createInfo.EnabledApiLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);

            DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new();
            PopulateDebugMessengerCreateInfo(ref debugCreateInfo);
            createInfo.Next = &debugCreateInfo;
        }
        else
        {
            createInfo.EnabledApiLayerCount = 0;
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