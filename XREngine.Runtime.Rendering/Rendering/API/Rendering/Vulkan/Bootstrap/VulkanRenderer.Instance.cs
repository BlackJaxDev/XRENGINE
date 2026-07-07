using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private Instance instance;
    private bool _vulkanInstanceCreatedThroughOpenXr;
    private OpenXrVulkanEnable2BootstrapContext? _openXrVulkanEnable2Context;
    public Instance Instance => instance;
    internal bool UsesOpenXrVulkanEnable2Creation => _vulkanInstanceCreatedThroughOpenXr && _vulkanDeviceCreatedThroughOpenXr;
    internal bool TryGetOpenXrVulkanEnable2BootstrapInstance(
        out Silk.NET.OpenXR.XR api,
        out Silk.NET.OpenXR.Instance xrInstance,
        out string[] enabledExtensions)
    {
        if (_openXrVulkanEnable2Context is not null)
        {
            api = _openXrVulkanEnable2Context.Api;
            xrInstance = _openXrVulkanEnable2Context.XrInstance;
            enabledExtensions = _openXrVulkanEnable2Context.EnabledExtensions;
            return xrInstance.Handle != 0;
        }

        api = null!;
        xrInstance = default;
        enabledExtensions = [];
        return false;
    }

    internal bool InvalidateOpenXrVulkanEnable2BootstrapInstance(string reason)
    {
        if (_openXrVulkanEnable2Context is null)
            return false;

        bool rendererHandlesCreatedThroughOpenXr = UsesOpenXrVulkanEnable2Creation;
        _openXrVulkanEnable2Context.AbandonXrInstanceOnDispose(reason);
        _openXrVulkanEnable2Context.Dispose();
        _openXrVulkanEnable2Context = null;

        Debug.VulkanWarning(
            "[OpenXR] Invalidated renderer-owned XR_KHR_vulkan_enable2 bootstrap instance. Reason={0}",
            string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);

        if (rendererHandlesCreatedThroughOpenXr)
            Debug.VulkanWarning(
                "[OpenXR] Vulkan handles were created through XR_KHR_vulkan_enable2; keeping the logical device live after XR instance teardown. Vulkan device loss will be reported separately if the driver/runtime invalidates the handles.");

        return true;
    }

    private void DestroyInstance()
    {
        RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled = false;

        if (instance.Handle != 0)
        {
            Api!.DestroyInstance(instance, null);
            instance = default;
        }

        if (_deviceLost)
        {
            _openXrVulkanEnable2Context?.AbandonXrInstanceOnDispose(
                string.IsNullOrWhiteSpace(_deviceLostReason)
                    ? "Vulkan logical device lost"
                    : _deviceLostReason);
        }

        _openXrVulkanEnable2Context?.Dispose();
        _openXrVulkanEnable2Context = null;
    }

    private void CreateInstance()
    {
        PrepareObsHookCompatibility();

        if (EnableValidationLayers && !CheckValidationLayerSupport())
        {
            System.Console.WriteLine("Vulkan validation layers requested but not available. Continuing without them.");
            EnableValidationLayers = false;
        }

        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("XRENGINE"),
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("XRENGINE"),
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version13
        };

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo
        };

        var extensions = GetRequiredExtensions();
        createInfo.EnabledExtensionCount = (uint)extensions.Length;
        createInfo.PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions); ;

        if (EnableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);

            DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new();
            PopulateDebugMessengerCreateInfo(ref debugCreateInfo);
            createInfo.PNext = &debugCreateInfo;
        }
        else
        {
            createInfo.EnabledLayerCount = 0;
            createInfo.PNext = null;
        }

        var getInstanceProcAddr = Api!.GetInstanceProcAddr(default, "vkGetInstanceProcAddr");
        if (OpenXRAPI.TryCreateVulkanEnable2BootstrapContext(
            out OpenXrVulkanEnable2BootstrapContext? openXrContext,
            out string? openXrContextFailure))
        {
            if (openXrContext!.TryCreateVulkanInstance(
                &createInfo,
                getInstanceProcAddr,
                out nint openXrCreatedInstanceHandle,
                out _,
                out string? openXrCreateFailure))
            {
                instance = new Instance(openXrCreatedInstanceHandle);
                _openXrVulkanEnable2Context = openXrContext;
                _vulkanInstanceCreatedThroughOpenXr = true;
            }
            else
            {
                openXrContext.Dispose();
                throw new Exception($"Failed to create Vulkan instance through OpenXR: {openXrCreateFailure}");
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(openXrContextFailure))
                throw new Exception($"Failed to create Vulkan OpenXR bootstrap context: {openXrContextFailure}");

            Result createResult = Api.CreateInstance(ref createInfo, null, out instance);
            if (createResult != Result.Success)
                throw new Exception($"Failed to create Vulkan instance. Result={createResult}");

            _openXrVulkanEnable2Context = null;
            _vulkanInstanceCreatedThroughOpenXr = false;
        }
        
        Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
        Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
        SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);

        if (EnableValidationLayers)
            SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);

        RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled = EnableValidationLayers;
    }
}
