using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    private Instance instance;
    private uint _vulkanInstanceApiVersion;
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
        RuntimeEngine.Rendering.State.VulkanSynchronizationValidationEnabled = false;

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

        uint requestedApiVersion = ResolveRequestedVulkanInstanceApiVersion();
        _vulkanInstanceApiVersion = requestedApiVersion;

        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("XRENGINE"),
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("XRENGINE"),
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = requestedApiVersion
        };

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo
        };

        var extensions = GetRequiredExtensions();
        createInfo.EnabledExtensionCount = (uint)extensions.Length;
        createInfo.PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions); ;

        LogResolvedVulkanDiagnosticOptions(extensions);

        ValidationFeatureEnableEXT* enabledValidationFeatures = stackalloc ValidationFeatureEnableEXT[4];
        uint enabledValidationFeatureCount = EnableValidationLayers
            ? PopulateEnabledValidationFeatures(enabledValidationFeatures)
            : 0u;

        if (EnableValidationLayers)
        {
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);

            DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new();
            PopulateDebugMessengerCreateInfo(ref debugCreateInfo);

            if (enabledValidationFeatureCount > 0)
            {
                ValidationFeaturesEXT validationFeatures = new()
                {
                    SType = StructureType.ValidationFeaturesExt,
                    EnabledValidationFeatureCount = enabledValidationFeatureCount,
                    PEnabledValidationFeatures = enabledValidationFeatures,
                    PNext = &debugCreateInfo,
                };
                createInfo.PNext = &validationFeatures;
            }
            else
            {
                createInfo.PNext = &debugCreateInfo;
            }
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
        RuntimeEngine.Rendering.State.VulkanSynchronizationValidationEnabled =
            EnableValidationLayers && _diagnosticOptions.EnableSynchronizationValidation;
    }

    private static uint ResolveRequestedVulkanInstanceApiVersion()
    {
        uint defaultApiVersion = Vk.Version13;
        OpenXrVulkanRuntimeRequirements openXrRequirements = OpenXRAPI.GetRequestedVulkanRuntimeRequirements();
        if (openXrRequirements.MaxApiVersionSupported == 0)
            return defaultApiVersion;

        uint minApiVersion = ConvertOpenXrVulkanApiVersion(openXrRequirements.MinApiVersionSupported);
        uint maxApiVersion = ConvertOpenXrVulkanApiVersion(openXrRequirements.MaxApiVersionSupported);
        if (maxApiVersion == 0)
            return defaultApiVersion;

        if (minApiVersion != 0 && maxApiVersion < minApiVersion)
        {
            Debug.VulkanWarning(
                "[OpenXR] Ignoring invalid Vulkan API version range from runtime: min={0} max={1}.",
                openXrRequirements.MinApiVersionSupported,
                openXrRequirements.MaxApiVersionSupported);
            return defaultApiVersion;
        }

        uint resolvedApiVersion = defaultApiVersion;
        if (minApiVersion != 0 && resolvedApiVersion < minApiVersion)
            resolvedApiVersion = minApiVersion;
        if (resolvedApiVersion > maxApiVersion)
            resolvedApiVersion = maxApiVersion;

        if (resolvedApiVersion != defaultApiVersion)
        {
            Debug.Vulkan(
                "[OpenXR] Vulkan instance API version resolved to {0} for OpenXR runtime range {1}-{2} (default {3}).",
                FormatVulkanApiVersion(resolvedApiVersion),
                minApiVersion == 0 ? "<unknown>" : FormatVulkanApiVersion(minApiVersion),
                FormatVulkanApiVersion(maxApiVersion),
                FormatVulkanApiVersion(defaultApiVersion));
        }

        return resolvedApiVersion;
    }

    private static uint ConvertOpenXrVulkanApiVersion(ulong openXrApiVersion)
    {
        if (openXrApiVersion == 0)
            return 0;

        ulong major = openXrApiVersion >> 48;
        ulong minor = (openXrApiVersion >> 32) & 0xFFFFUL;
        ulong patch = openXrApiVersion & 0xFFFFFFFFUL;
        if (major > 0x7FUL || minor > 0x3FFUL)
            return 0;

        if (patch > 0xFFFUL)
            patch = 0xFFFUL;

        return ((uint)major << 22) | ((uint)minor << 12) | (uint)patch;
    }
}
