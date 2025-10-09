using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;
using System.Runtime.InteropServices;
using System.Text;

public unsafe partial class OpenXRAPI
{
    private Instance _instance;

    private void DestroyInstance()
        => Api!.DestroyInstance(_instance);

    private void CreateInstance()
    {
        var appInfo = MakeAppInfo();
        var createInfo = MakeCreateInfo(appInfo, GetRequiredExtensions(ERenderer.OpenGL), EnableValidationLayers ? validationLayers : null);
        MakeInstance(createInfo);
        Free(appInfo, createInfo);
    }

    private void MakeInstance(InstanceCreateInfo createInfo)
    {
        Instance i = default;
        if (Api!.CreateInstance(&createInfo, &i) != Result.Success)
            throw new Exception("Failed to create OpenXR instance.");
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
        ApplicationInfo appInfo = new(
                    new Version32(1, 0, 0),
                    new Version32(1, 0, 0),
                    new Version32(1, 0, 0));
        var encoding = Encoding.GetEncoding("Windows-1252", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        encoding.GetBytes("XREngine", new Span<byte>(appInfo.ApplicationName, 8));
        encoding.GetBytes("XREngine", new Span<byte>(appInfo.EngineName, 8));
        return appInfo;
    }
}