using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using System.Runtime.InteropServices;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    public Instance Instance => _deviceContext.Instance;
    internal IReadOnlyList<string> EnabledInstanceExtensions =>
        _deviceContext.EnabledInstanceExtensions;
    internal bool UsesOpenXrVulkanEnable2Creation => _deviceContext.InstanceCreatedThroughOpenXr && _deviceContext.CreatedThroughOpenXr;
    internal bool TryGetOpenXrVulkanEnable2BootstrapInstance(
        out Silk.NET.OpenXR.XR api,
        out Silk.NET.OpenXR.Instance xrInstance,
        out string[] enabledExtensions)
    {
        return _deviceContext.TryGetOpenXrBootstrapInstance(
            out api,
            out xrInstance,
            out enabledExtensions);
    }

    internal bool InvalidateOpenXrVulkanEnable2BootstrapInstance(string reason)
    {
        if (_deviceContext.OpenXrBootstrapContext is null)
            return false;

        bool rendererHandlesCreatedThroughOpenXr = UsesOpenXrVulkanEnable2Creation;
        _deviceContext.InvalidateOpenXrBootstrapInstance(reason);

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

        _deviceContext.DestroyInstance(
            Api!,
            _deviceLost ? _deviceContext.DeviceFaultFacility.DeviceLostReason : null);
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
        try
        {
            ValidationFeatureEnableEXT* enabledValidationFeatures = stackalloc ValidationFeatureEnableEXT[4];
            DebugUtilsMessengerCreateInfoEXT debugCreateInfo = default;
            ValidationFeaturesEXT validationFeatures = default;
            uint enabledValidationFeatureCount = EnableValidationLayers
                ? PopulateEnabledValidationFeatures(enabledValidationFeatures)
                : 0u;

            if (EnableValidationLayers)
            {
                createInfo.EnabledLayerCount = (uint)_deviceContext.MutableCapabilities.validationLayers.Length;
                createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(_deviceContext.MutableCapabilities.validationLayers);

                debugCreateInfo = _deviceContext.PrepareDebugMessengerCreateInfo();

                if (enabledValidationFeatureCount > 0)
                {
                    validationFeatures = new()
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
            Instance createdInstance;
            OpenXrVulkanEnable2BootstrapContext? createdOpenXrContext;
            bool createdThroughOpenXr;
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
                    createdInstance = new Instance(openXrCreatedInstanceHandle);
                    createdOpenXrContext = openXrContext;
                    createdThroughOpenXr = true;
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

                Result createResult = Api.CreateInstance(ref createInfo, null, out createdInstance);
                if (createResult != Result.Success)
                    throw new Exception($"Failed to create Vulkan instance. Result={createResult}");

                createdOpenXrContext = null;
                createdThroughOpenXr = false;
            }

            try
            {
                _deviceContext.AttachInstance(
                    createdInstance,
                    extensions,
                    requestedApiVersion,
                    createdThroughOpenXr,
                    createdOpenXrContext);
            }
            catch
            {
                Api.DestroyInstance(createdInstance, null);
                createdOpenXrContext?.Dispose();
                throw;
            }

            RuntimeEngine.Rendering.State.VulkanValidationLayersEnabled = EnableValidationLayers;
            RuntimeEngine.Rendering.State.VulkanSynchronizationValidationEnabled =
                EnableValidationLayers && _frameTelemetry._diagnosticOptions.EnableSynchronizationValidation;
        }
        finally
        {
            Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
            Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
            SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
            if (createInfo.PpEnabledLayerNames is not null)
                SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
            if (!_deviceContext.HasInstance)
                _deviceContext.DestroyValidation(Api!);
        }
    }

    private uint ResolveRequestedVulkanInstanceApiVersion()
    {
        uint defaultApiVersion = Vk.Version13;
        OpenXrVulkanRuntimeRequirements openXrRequirements = OpenXRAPI.GetRequestedVulkanRuntimeRequirements();
        if (openXrRequirements.MaxApiVersionSupported == 0)
            return Math.Max(defaultApiVersion, _outputRuntime._streamlineMinimumApiVersion);

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

        if (resolvedApiVersion < _outputRuntime._streamlineMinimumApiVersion)
        {
            throw new NotSupportedException(
                $"Streamline requires Vulkan {FormatVulkanApiVersion(_outputRuntime._streamlineMinimumApiVersion)}, but the active OpenXR runtime caps Vulkan at {FormatVulkanApiVersion(maxApiVersion)}.");
        }

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
