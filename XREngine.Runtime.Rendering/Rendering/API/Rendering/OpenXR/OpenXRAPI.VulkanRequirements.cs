using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Debug = XREngine.Debug;
using OxrExtDebugUtils = Silk.NET.OpenXR.Extensions.EXT.ExtDebugUtils;

namespace XREngine.Rendering.API.Rendering.OpenXR;

internal sealed record OpenXrVulkanRuntimeRequirements(
    string[] InstanceExtensions,
    string[] DeviceExtensions,
    ulong MinApiVersionSupported,
    ulong MaxApiVersionSupported,
    string? FailureReason)
{
    public static OpenXrVulkanRuntimeRequirements Empty { get; } = new([], [], 0, 0, null);
}

internal enum OpenXrVulkanEnable2BootstrapPolicy
{
    Auto,
    Force,
    Disable
}

internal sealed unsafe class OpenXrVulkanEnable2BootstrapContext(
    XR api,
    Instance xrInstance,
    ulong systemId,
    KhrVulkanEnable2 vulkan2Extension,
    string[] enabledExtensions) : IDisposable
{
    private bool _disposed;
    private bool _destroyXrInstanceOnDispose = true;
    internal XR Api => api;
    internal Instance XrInstance => xrInstance;
    internal ulong SystemId => systemId;
    internal string[] EnabledExtensions { get; } = enabledExtensions.ToArray();

    internal void AbandonXrInstanceOnDispose(string reason)
    {
        if (_disposed || !_destroyXrInstanceOnDispose)
            return;

        _destroyXrInstanceOnDispose = false;
        Debug.VulkanWarning(
            "[OpenXR] Abandoning renderer-owned XR_KHR_vulkan_enable2 instance during teardown. Reason={0}",
            string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);
    }

    internal bool TryCreateVulkanInstance(
        void* vulkanCreateInfo,
        PfnVoidFunction pfnGetInstanceProcAddr,
        out nint vulkanInstanceHandle,
        out uint vulkanResult,
        out string? failureReason)
    {
        vulkanInstanceHandle = 0;
        vulkanResult = 0;
        failureReason = null;

        if (vulkanCreateInfo is null)
        {
            failureReason = "Vulkan instance create-info pointer is null.";
            return false;
        }

        if ((nint)pfnGetInstanceProcAddr == 0)
        {
            failureReason = "vkGetInstanceProcAddr pointer is null.";
            return false;
        }

        VulkanInstanceCreateInfoKHR xrCreateInfo = new()
        {
            Type = StructureType.VulkanInstanceCreateInfoKhr,
            SystemId = systemId,
            PfnGetInstanceProcAddr = pfnGetInstanceProcAddr,
            VulkanCreateInfo = vulkanCreateInfo,
            VulkanAllocator = null
        };

        VkHandle instanceHandle = default;
        Result result = vulkan2Extension.CreateVulkanInstance(
            xrInstance,
            ref xrCreateInfo,
            ref instanceHandle,
            ref vulkanResult);
        if (result != Result.Success)
        {
            failureReason = $"xrCreateVulkanInstanceKHR failed: {result} (VkResult={(int)vulkanResult}).";
            return false;
        }

        if (vulkanResult != 0)
        {
            failureReason = $"xrCreateVulkanInstanceKHR returned Vulkan result {(int)vulkanResult}.";
            return false;
        }

        vulkanInstanceHandle = instanceHandle.Handle;
        if (vulkanInstanceHandle == 0)
        {
            failureReason = "xrCreateVulkanInstanceKHR returned a zero Vulkan instance handle.";
            return false;
        }

        Debug.Vulkan("[OpenXR] Created Vulkan instance through XR_KHR_vulkan_enable2: 0x{0:X}", (nuint)vulkanInstanceHandle);
        return true;
    }

    internal bool TryGetRequestedVulkanPhysicalDevice(
        nint vulkanInstanceHandle,
        out nint physicalDeviceHandle,
        out string? failureReason)
    {
        physicalDeviceHandle = 0;
        failureReason = null;

        if (vulkanInstanceHandle == 0)
        {
            failureReason = "Vulkan instance handle is zero.";
            return false;
        }

        GraphicsRequirementsVulkanKHR requirements = new()
        {
            Type = StructureType.GraphicsRequirementsVulkanKhr
        };
        Result requirementsResult = vulkan2Extension.GetVulkanGraphicsRequirements2(
            xrInstance,
            systemId,
            ref requirements);
        if (requirementsResult != Result.Success)
        {
            failureReason = $"xrGetVulkanGraphicsRequirements2KHR failed: {requirementsResult}";
            return false;
        }

        VulkanGraphicsDeviceGetInfoKHR deviceGetInfo = new()
        {
            Type = StructureType.VulkanGraphicsDeviceGetInfoKhr,
            SystemId = systemId,
            VulkanInstance = new VkHandle(vulkanInstanceHandle)
        };

        VkHandle requestedDevice = default;
        Result deviceResult = vulkan2Extension.GetVulkanGraphicsDevice2(
            xrInstance,
            ref deviceGetInfo,
            ref requestedDevice);
        if (deviceResult != Result.Success)
        {
            failureReason = $"xrGetVulkanGraphicsDevice2KHR failed: {deviceResult}";
            return false;
        }

        physicalDeviceHandle = requestedDevice.Handle;
        if (physicalDeviceHandle == 0)
        {
            failureReason = "OpenXR runtime returned a zero Vulkan physical-device handle.";
            return false;
        }

        Debug.Vulkan("[OpenXR] Runtime-selected Vulkan physical device: 0x{0:X}", (nuint)physicalDeviceHandle);
        return true;
    }

    internal bool TryCreateVulkanDevice(
        nint vulkanPhysicalDeviceHandle,
        void* vulkanCreateInfo,
        PfnVoidFunction pfnGetInstanceProcAddr,
        out nint vulkanDeviceHandle,
        out uint vulkanResult,
        out string? failureReason)
    {
        vulkanDeviceHandle = 0;
        vulkanResult = 0;
        failureReason = null;

        if (vulkanPhysicalDeviceHandle == 0)
        {
            failureReason = "Vulkan physical-device handle is zero.";
            return false;
        }

        if (vulkanCreateInfo is null)
        {
            failureReason = "Vulkan device create-info pointer is null.";
            return false;
        }

        if ((nint)pfnGetInstanceProcAddr == 0)
        {
            failureReason = "vkGetInstanceProcAddr pointer is null.";
            return false;
        }

        VulkanDeviceCreateInfoKHR xrCreateInfo = new()
        {
            Type = StructureType.VulkanDeviceCreateInfoKhr,
            SystemId = systemId,
            PfnGetInstanceProcAddr = pfnGetInstanceProcAddr,
            VulkanPhysicalDevice = new VkHandle(vulkanPhysicalDeviceHandle),
            VulkanCreateInfo = vulkanCreateInfo,
            VulkanAllocator = null
        };

        VkHandle deviceHandle = default;
        Result result = vulkan2Extension.CreateVulkanDevice(
            xrInstance,
            ref xrCreateInfo,
            ref deviceHandle,
            ref vulkanResult);
        if (result != Result.Success)
        {
            failureReason = $"xrCreateVulkanDeviceKHR failed: {result} (VkResult={(int)vulkanResult}).";
            return false;
        }

        if (vulkanResult != 0)
        {
            failureReason = $"xrCreateVulkanDeviceKHR returned Vulkan result {(int)vulkanResult}.";
            return false;
        }

        vulkanDeviceHandle = deviceHandle.Handle;
        if (vulkanDeviceHandle == 0)
        {
            failureReason = "xrCreateVulkanDeviceKHR returned a zero Vulkan device handle.";
            return false;
        }

        Debug.Vulkan("[OpenXR] Created Vulkan device through XR_KHR_vulkan_enable2: 0x{0:X}", (nuint)vulkanDeviceHandle);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_destroyXrInstanceOnDispose && xrInstance.Handle != 0)
        {
            try
            {
                api.DestroyInstance(xrInstance);
            }
            catch (Exception ex)
            {
                Debug.VulkanWarning("[OpenXR] xrDestroyInstance failed during Vulkan bootstrap teardown: {0}", ex.Message);
            }
        }

        api.Dispose();
    }
}

public unsafe partial class OpenXRAPI
{
    private static readonly object VulkanRuntimeRequirementsLock = new();
    private static string? _cachedVulkanRuntimeRequirementsKey;
    private static OpenXrVulkanRuntimeRequirements? _cachedVulkanRuntimeRequirements;

    private delegate Result XrGetVulkanExtensionsKHRDelegate(
        Instance instance,
        ulong systemId,
        uint bufferCapacityInput,
        uint* bufferCountOutput,
        byte* buffer);

    internal static OpenXrVulkanRuntimeRequirements GetRequestedVulkanRuntimeRequirements()
    {
        if (!ShouldQueryVulkanRuntimeRequirements())
            return OpenXrVulkanRuntimeRequirements.Empty;

        string key = CreateVulkanRuntimeRequirementsCacheKey();
        lock (VulkanRuntimeRequirementsLock)
        {
            if (string.Equals(_cachedVulkanRuntimeRequirementsKey, key, StringComparison.Ordinal) &&
                _cachedVulkanRuntimeRequirements is not null)
            {
                return _cachedVulkanRuntimeRequirements;
            }

            OpenXrVulkanRuntimeRequirements requirements = QueryVulkanRuntimeRequirements();
            _cachedVulkanRuntimeRequirementsKey = key;
            _cachedVulkanRuntimeRequirements = requirements;
            return requirements;
        }
    }

    internal static bool TryGetRequestedVulkanPhysicalDevice(
        nint vulkanInstanceHandle,
        out nint physicalDeviceHandle,
        out string? failureReason)
    {
        physicalDeviceHandle = 0;
        failureReason = null;

        if (vulkanInstanceHandle == 0)
        {
            failureReason = "Vulkan instance handle is zero.";
            return false;
        }

        if (!ShouldQueryVulkanRuntimeRequirements())
            return false;

        XR? api = null;
        Instance instance = default;
        try
        {
            api = XR.GetApi();
            HashSet<string> availableExtensions = EnumerateOpenXrInstanceExtensions(api);
            // The renderer currently owns Vulkan instance/device creation. For that flow, prefer the
            // legacy query: xrGetVulkanGraphicsDeviceKHR accepts an app-created VkInstance. The enable2
            // device query is paired with xrCreateVulkanInstanceKHR/xrCreateVulkanDeviceKHR and some
            // runtimes reject app-created instances there with XR_ERROR_VALIDATION_FAILURE.
            string? extensionName = availableExtensions.Contains(KhrVulkanEnable.ExtensionName)
                ? KhrVulkanEnable.ExtensionName
                : availableExtensions.Contains(KhrVulkanEnable2.ExtensionName)
                    ? KhrVulkanEnable2.ExtensionName
                    : null;

            if (extensionName is null)
            {
                failureReason = "OpenXR runtime does not expose XR_KHR_vulkan_enable2 or XR_KHR_vulkan_enable.";
                return false;
            }

            var appInfo = MakeAppInfo();
            var createInfo = MakeCreateInfo(appInfo, [extensionName], null, null);
            Result createResult = api.CreateInstance(&createInfo, &instance);
            Free(createInfo);

            if (createResult != Result.Success)
            {
                failureReason = $"xrCreateInstance for Vulkan physical-device query failed: {createResult}";
                return false;
            }

            SystemGetInfo systemGetInfo = new()
            {
                Type = StructureType.SystemGetInfo,
                FormFactor = FormFactor.HeadMountedDisplay
            };
            ulong systemId = 0;
            Result systemResult = api.GetSystem(instance, in systemGetInfo, ref systemId);
            if (systemResult != Result.Success)
            {
                failureReason = $"xrGetSystem for Vulkan physical-device query failed: {systemResult}";
                return false;
            }

            VkHandle vulkanInstance = new(vulkanInstanceHandle);
            VkHandle requestedDevice = default;

            if (extensionName == KhrVulkanEnable2.ExtensionName)
            {
                if (!api.TryGetInstanceExtension<KhrVulkanEnable2>(null, instance, out var vulkan2Extension))
                {
                    failureReason = "XR_KHR_vulkan_enable2 was not returned by the Silk.NET extension loader.";
                    return false;
                }

                GraphicsRequirementsVulkanKHR requirements = new()
                {
                    Type = StructureType.GraphicsRequirementsVulkanKhr
                };
                Result requirementsResult = vulkan2Extension.GetVulkanGraphicsRequirements2(
                    instance,
                    systemId,
                    ref requirements);
                if (requirementsResult != Result.Success)
                {
                    failureReason = $"xrGetVulkanGraphicsRequirements2KHR failed: {requirementsResult}";
                    return false;
                }

                VulkanGraphicsDeviceGetInfoKHR deviceGetInfo = new()
                {
                    Type = StructureType.VulkanGraphicsDeviceGetInfoKhr,
                    SystemId = systemId,
                    VulkanInstance = vulkanInstance
                };

                Result deviceResult = vulkan2Extension.GetVulkanGraphicsDevice2(
                    instance,
                    ref deviceGetInfo,
                    ref requestedDevice);
                if (deviceResult != Result.Success)
                {
                    failureReason = $"xrGetVulkanGraphicsDevice2KHR failed: {deviceResult}";
                    return false;
                }
            }
            else
            {
                if (!api.TryGetInstanceExtension<KhrVulkanEnable>(null, instance, out var vulkanExtension))
                {
                    failureReason = "XR_KHR_vulkan_enable was not returned by the Silk.NET extension loader.";
                    return false;
                }

                GraphicsRequirementsVulkanKHR requirements = new()
                {
                    Type = StructureType.GraphicsRequirementsVulkanKhr
                };
                Result requirementsResult = vulkanExtension.GetVulkanGraphicsRequirements(
                    instance,
                    systemId,
                    ref requirements);
                if (requirementsResult != Result.Success)
                {
                    failureReason = $"xrGetVulkanGraphicsRequirementsKHR failed: {requirementsResult}";
                    return false;
                }

                Result deviceResult = vulkanExtension.GetVulkanGraphicsDevice(
                    instance,
                    systemId,
                    vulkanInstance,
                    ref requestedDevice);
                if (deviceResult != Result.Success)
                {
                    failureReason = $"xrGetVulkanGraphicsDeviceKHR failed: {deviceResult}";
                    return false;
                }
            }

            physicalDeviceHandle = requestedDevice.Handle;
            if (physicalDeviceHandle == 0)
            {
                failureReason = "OpenXR runtime returned a zero Vulkan physical-device handle.";
                return false;
            }

            Debug.Vulkan("[OpenXR] Runtime-selected Vulkan physical device: 0x{0:X}", (nuint)physicalDeviceHandle);
            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            Debug.VulkanWarning($"[OpenXR] Vulkan physical-device query failed: {failureReason}");
            return false;
        }
        finally
        {
            if (api is not null)
            {
                if (instance.Handle != 0)
                    api.DestroyInstance(instance);
                api.Dispose();
            }
        }
    }

    internal static bool TryCreateVulkanInstanceForOpenXr(
        void* vulkanCreateInfo,
        PfnVoidFunction pfnGetInstanceProcAddr,
        out nint vulkanInstanceHandle,
        out uint vulkanResult,
        out string? failureReason)
    {
        vulkanInstanceHandle = 0;
        vulkanResult = 0;
        failureReason = null;

        if (vulkanCreateInfo is null)
        {
            failureReason = "Vulkan instance create-info pointer is null.";
            return false;
        }

        if ((nint)pfnGetInstanceProcAddr == 0)
        {
            failureReason = "vkGetInstanceProcAddr pointer is null.";
            return false;
        }

        if (!TryCreateVulkanEnable2Context(
            out XR? api,
            out Instance xrInstance,
            out ulong systemId,
            out KhrVulkanEnable2? vulkan2Extension,
            out _,
            out failureReason))
        {
            return false;
        }

        try
        {
            VulkanInstanceCreateInfoKHR xrCreateInfo = new()
            {
                Type = StructureType.VulkanInstanceCreateInfoKhr,
                SystemId = systemId,
                PfnGetInstanceProcAddr = pfnGetInstanceProcAddr,
                VulkanCreateInfo = vulkanCreateInfo,
                VulkanAllocator = null
            };

            VkHandle instanceHandle = default;
            Result result = vulkan2Extension!.CreateVulkanInstance(
                xrInstance,
                ref xrCreateInfo,
                ref instanceHandle,
                ref vulkanResult);
            if (result != Result.Success)
            {
                failureReason = $"xrCreateVulkanInstanceKHR failed: {result} (VkResult={(int)vulkanResult}).";
                return false;
            }

            if (vulkanResult != 0)
            {
                failureReason = $"xrCreateVulkanInstanceKHR returned Vulkan result {(int)vulkanResult}.";
                return false;
            }

            vulkanInstanceHandle = instanceHandle.Handle;
            if (vulkanInstanceHandle == 0)
            {
                failureReason = "xrCreateVulkanInstanceKHR returned a zero Vulkan instance handle.";
                return false;
            }

            Debug.Vulkan("[OpenXR] Created Vulkan instance through XR_KHR_vulkan_enable2: 0x{0:X}", (nuint)vulkanInstanceHandle);
            return true;
        }
        finally
        {
            DestroyTemporaryOpenXrContext(api, xrInstance);
        }
    }

    internal static bool TryCreateVulkanEnable2BootstrapContext(
        out OpenXrVulkanEnable2BootstrapContext? context,
        out string? failureReason)
    {
        context = null;
        if (!TryCreateVulkanEnable2Context(
            out XR? api,
            out Instance xrInstance,
            out ulong systemId,
            out KhrVulkanEnable2? vulkan2Extension,
            out string[] enabledExtensions,
            out failureReason))
        {
            return false;
        }

        context = new OpenXrVulkanEnable2BootstrapContext(api!, xrInstance, systemId, vulkan2Extension!, enabledExtensions);
        return true;
    }

    internal static bool TryCreateVulkanDeviceForOpenXr(
        nint vulkanPhysicalDeviceHandle,
        void* vulkanCreateInfo,
        PfnVoidFunction pfnGetInstanceProcAddr,
        out nint vulkanDeviceHandle,
        out uint vulkanResult,
        out string? failureReason)
    {
        vulkanDeviceHandle = 0;
        vulkanResult = 0;
        failureReason = null;

        if (vulkanPhysicalDeviceHandle == 0)
        {
            failureReason = "Vulkan physical-device handle is zero.";
            return false;
        }

        if (vulkanCreateInfo is null)
        {
            failureReason = "Vulkan device create-info pointer is null.";
            return false;
        }

        if ((nint)pfnGetInstanceProcAddr == 0)
        {
            failureReason = "vkGetInstanceProcAddr pointer is null.";
            return false;
        }

        if (!TryCreateVulkanEnable2Context(
            out XR? api,
            out Instance xrInstance,
            out ulong systemId,
            out KhrVulkanEnable2? vulkan2Extension,
            out _,
            out failureReason))
        {
            return false;
        }

        try
        {
            VulkanDeviceCreateInfoKHR xrCreateInfo = new()
            {
                Type = StructureType.VulkanDeviceCreateInfoKhr,
                SystemId = systemId,
                PfnGetInstanceProcAddr = pfnGetInstanceProcAddr,
                VulkanPhysicalDevice = new VkHandle(vulkanPhysicalDeviceHandle),
                VulkanCreateInfo = vulkanCreateInfo,
                VulkanAllocator = null
            };

            VkHandle deviceHandle = default;
            Result result = vulkan2Extension!.CreateVulkanDevice(
                xrInstance,
                ref xrCreateInfo,
                ref deviceHandle,
                ref vulkanResult);
            if (result != Result.Success)
            {
                failureReason = $"xrCreateVulkanDeviceKHR failed: {result} (VkResult={(int)vulkanResult}).";
                return false;
            }

            if (vulkanResult != 0)
            {
                failureReason = $"xrCreateVulkanDeviceKHR returned Vulkan result {(int)vulkanResult}.";
                return false;
            }

            vulkanDeviceHandle = deviceHandle.Handle;
            if (vulkanDeviceHandle == 0)
            {
                failureReason = "xrCreateVulkanDeviceKHR returned a zero Vulkan device handle.";
                return false;
            }

            Debug.Vulkan("[OpenXR] Created Vulkan device through XR_KHR_vulkan_enable2: 0x{0:X}", (nuint)vulkanDeviceHandle);
            return true;
        }
        finally
        {
            DestroyTemporaryOpenXrContext(api, xrInstance);
        }
    }

    private static bool TryCreateVulkanEnable2Context(
        out XR? api,
        out Instance instance,
        out ulong systemId,
        out KhrVulkanEnable2? vulkan2Extension,
        out string[] enabledExtensions,
        out string? failureReason)
    {
        api = null;
        instance = default;
        systemId = 0;
        vulkan2Extension = null;
        enabledExtensions = [];
        failureReason = null;

        if (!ShouldQueryVulkanRuntimeRequirements())
            return false;

        if (!ShouldUseVulkanEnable2Bootstrap(out string? bootstrapPolicyReason))
        {
            if (!string.IsNullOrWhiteSpace(bootstrapPolicyReason))
                Debug.Vulkan("[OpenXR] Skipping XR_KHR_vulkan_enable2 Vulkan bootstrap. {0}", bootstrapPolicyReason);

            return false;
        }

        try
        {
            api = XR.GetApi();
            HashSet<string> availableExtensions = EnumerateOpenXrInstanceExtensions(api);
            if (!availableExtensions.Contains(KhrVulkanEnable2.ExtensionName))
            {
                Debug.VulkanWarning("[OpenXR] Runtime does not expose XR_KHR_vulkan_enable2; using renderer-created Vulkan handles.");
                DestroyTemporaryOpenXrContext(api, instance);
                api = null;
                return false;
            }

            enabledExtensions = BuildVulkanEnable2BootstrapExtensions(availableExtensions);
            var appInfo = MakeAppInfo();
            var createInfo = MakeCreateInfo(appInfo, enabledExtensions, null, null);
            Instance createdInstance = default;
            Result createResult = api.CreateInstance(&createInfo, &createdInstance);
            Free(createInfo);
            instance = createdInstance;

            if (createResult != Result.Success)
            {
                failureReason = $"xrCreateInstance for Vulkan enable2 creation failed: {createResult}";
                DestroyTemporaryOpenXrContext(api, instance);
                api = null;
                instance = default;
                return false;
            }

            SystemGetInfo systemGetInfo = new()
            {
                Type = StructureType.SystemGetInfo,
                FormFactor = FormFactor.HeadMountedDisplay
            };
            Result systemResult = api.GetSystem(instance, in systemGetInfo, ref systemId);
            if (systemResult != Result.Success)
            {
                failureReason = $"xrGetSystem for Vulkan enable2 creation failed: {systemResult}";
                DestroyTemporaryOpenXrContext(api, instance);
                api = null;
                instance = default;
                return false;
            }

            if (!api.TryGetInstanceExtension<KhrVulkanEnable2>(null, instance, out vulkan2Extension))
            {
                failureReason = "XR_KHR_vulkan_enable2 was not returned by the Silk.NET extension loader.";
                DestroyTemporaryOpenXrContext(api, instance);
                api = null;
                instance = default;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            DestroyTemporaryOpenXrContext(api, instance);
            api = null;
            instance = default;
            enabledExtensions = [];
            Debug.VulkanWarning($"[OpenXR] Vulkan enable2 creation context failed: {failureReason}");
            return false;
        }
    }

    private static bool ShouldUseVulkanEnable2Bootstrap(out string? reason)
    {
        reason = null;
        OpenXrVulkanEnable2BootstrapPolicy policy = ResolveVulkanEnable2BootstrapPolicy(out string? policyDiagnostic);
        if (!string.IsNullOrWhiteSpace(policyDiagnostic))
            Debug.VulkanWarning("[OpenXR] {0}", policyDiagnostic);

        if (policy == OpenXrVulkanEnable2BootstrapPolicy.Disable)
        {
            reason = $"{XREngineEnvironmentVariables.OpenXrVulkanEnable2Bootstrap}=Disable requested app-created Vulkan handles.";
            return false;
        }

        string? runtimePath = ResolveCurrentOpenXrRuntimePath();
        bool steamVrRuntime = !string.IsNullOrWhiteSpace(runtimePath) && IsSteamVrRuntimePath(runtimePath);
        if (steamVrRuntime && policy != OpenXrVulkanEnable2BootstrapPolicy.Force)
        {
            reason =
                $"Active runtime is SteamVR ('{runtimePath}'); using app-created Vulkan handles with XR_KHR_vulkan_enable because SteamVR can raise a native breakpoint inside xrCreateVulkanInstanceKHR. Set {XREngineEnvironmentVariables.OpenXrVulkanEnable2Bootstrap}=Force for diagnostics.";
            return false;
        }

        if (policy == OpenXrVulkanEnable2BootstrapPolicy.Force)
            Debug.Vulkan("[OpenXR] {0}=Force requested XR_KHR_vulkan_enable2 Vulkan bootstrap.", XREngineEnvironmentVariables.OpenXrVulkanEnable2Bootstrap);

        return true;
    }

    private static OpenXrVulkanEnable2BootstrapPolicy ResolveVulkanEnable2BootstrapPolicy(out string? diagnostic)
    {
        diagnostic = null;
        string? value = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanEnable2Bootstrap);
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return OpenXrVulkanEnable2BootstrapPolicy.Auto;
        }

        if (value.Equals("force", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            return OpenXrVulkanEnable2BootstrapPolicy.Force;
        }

        if (value.Equals("disable", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("legacy", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("no", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return OpenXrVulkanEnable2BootstrapPolicy.Disable;
        }

        diagnostic =
            $"Ignoring invalid {XREngineEnvironmentVariables.OpenXrVulkanEnable2Bootstrap}='{value}'. Expected Auto, Force, or Disable.";
        return OpenXrVulkanEnable2BootstrapPolicy.Auto;
    }

    private static string? ResolveCurrentOpenXrRuntimePath()
    {
        string? runtimePath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        if (!string.IsNullOrWhiteSpace(runtimePath))
            return runtimePath;

        runtimePath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson);
        if (!string.IsNullOrWhiteSpace(runtimePath))
            return runtimePath;

        return TryGetOpenXRActiveRuntime();
    }

    private static string[] BuildVulkanEnable2BootstrapExtensions(HashSet<string> availableExtensions)
    {
        List<string> extensions = [KhrVulkanEnable2.ExtensionName];

        AddOptionalExtension(extensions, availableExtensions, KhrWin32ConvertPerformanceCounterTime.ExtensionName);
        AddOptionalExtension(extensions, availableExtensions, OxrExtDebugUtils.ExtensionName);

        return [.. extensions];
    }

    private static void AddOptionalExtension(List<string> extensions, HashSet<string> availableExtensions, string extensionName)
    {
        if (availableExtensions.Contains(extensionName))
            extensions.Add(extensionName);
    }

    private static void DestroyTemporaryOpenXrContext(XR? api, Instance instance)
    {
        if (api is null)
            return;

        if (instance.Handle != 0)
            api.DestroyInstance(instance);

        api.Dispose();
    }

    private static bool ShouldQueryVulkanRuntimeRequirements()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson)))
            return true;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson)))
            return true;

        if (IsUnitTestingOpenXrLaunchMode())
            return true;

        if (TryParseBooleanEnvironment(XREngineEnvironmentVariables.UnitTestUseOpenXr))
            return true;

        return RuntimeEngine.GameSettings?.VRRuntime == EVRRuntime.OpenXR;
    }

    private static bool IsUnitTestingOpenXrLaunchMode()
    {
        string? unitTestVrMode = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrMode);
        return string.Equals(unitTestVrMode, "MonadoOpenXR", StringComparison.OrdinalIgnoreCase)
            || string.Equals(unitTestVrMode, "OpenXR", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseBooleanEnvironment(string variableName)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateVulkanRuntimeRequirementsCacheKey()
        => string.Join("|",
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson) ?? string.Empty,
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestOpenXrRuntimeJson) ?? string.Empty,
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestVrMode) ?? string.Empty,
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.UnitTestUseOpenXr) ?? string.Empty,
            RuntimeEngine.GameSettings?.VRRuntime.ToString() ?? string.Empty);

    private static OpenXrVulkanRuntimeRequirements QueryVulkanRuntimeRequirements()
    {
        XR? api = null;
        Instance instance = default;
        try
        {
            api = XR.GetApi();
            HashSet<string> availableExtensions = EnumerateOpenXrInstanceExtensions(api);
            if (!availableExtensions.Contains(KhrVulkanEnable.ExtensionName))
            {
                string reason = availableExtensions.Contains(KhrVulkanEnable2.ExtensionName)
                    ? "OpenXR runtime exposes XR_KHR_vulkan_enable2 but not XR_KHR_vulkan_enable; renderer-created Vulkan handles cannot query legacy extension strings."
                    : "OpenXR runtime does not expose XR_KHR_vulkan_enable.";

                Debug.VulkanWarning($"[OpenXR] Vulkan runtime extension preflight skipped: {reason}");
                return new OpenXrVulkanRuntimeRequirements([], [], 0, 0, reason);
            }

            var appInfo = MakeAppInfo();
            var createInfo = MakeCreateInfo(appInfo, [KhrVulkanEnable.ExtensionName], null, null);
            Result createResult = api.CreateInstance(&createInfo, &instance);
            Free(createInfo);

            if (createResult != Result.Success)
                return new OpenXrVulkanRuntimeRequirements([], [], 0, 0, $"xrCreateInstance for Vulkan requirement query failed: {createResult}");

            SystemGetInfo systemGetInfo = new()
            {
                Type = StructureType.SystemGetInfo,
                FormFactor = FormFactor.HeadMountedDisplay
            };
            ulong systemId = 0;
            Result systemResult = api.GetSystem(instance, in systemGetInfo, ref systemId);
            if (systemResult != Result.Success)
                return new OpenXrVulkanRuntimeRequirements([], [], 0, 0, $"xrGetSystem for Vulkan requirement query failed: {systemResult}");

            ulong minApiVersionSupported = 0;
            ulong maxApiVersionSupported = 0;
            if (api.TryGetInstanceExtension<KhrVulkanEnable>(null, instance, out var vulkanExtension))
            {
                GraphicsRequirementsVulkanKHR graphicsRequirements = new()
                {
                    Type = StructureType.GraphicsRequirementsVulkanKhr
                };
                Result graphicsRequirementsResult = vulkanExtension.GetVulkanGraphicsRequirements(
                    instance,
                    systemId,
                    ref graphicsRequirements);
                if (graphicsRequirementsResult == Result.Success)
                {
                    minApiVersionSupported = graphicsRequirements.MinApiVersionSupported;
                    maxApiVersionSupported = graphicsRequirements.MaxApiVersionSupported;
                }
                else
                {
                    Debug.VulkanWarning(
                        "[OpenXR] xrGetVulkanGraphicsRequirementsKHR failed during Vulkan runtime preflight: {0}",
                        graphicsRequirementsResult);
                }
            }

            string[] instanceExtensions = QueryVulkanExtensionList(api, instance, systemId, "xrGetVulkanInstanceExtensionsKHR");
            string[] deviceExtensions = QueryVulkanExtensionList(api, instance, systemId, "xrGetVulkanDeviceExtensionsKHR");

            if (instanceExtensions.Length > 0 || deviceExtensions.Length > 0)
            {
                Debug.Vulkan(
                    "[OpenXR] Vulkan runtime requirements: minApi={0} maxApi={1} instance=[{2}] device=[{3}]",
                    minApiVersionSupported,
                    maxApiVersionSupported,
                    string.Join(", ", instanceExtensions),
                    string.Join(", ", deviceExtensions));
            }

            return new OpenXrVulkanRuntimeRequirements(
                instanceExtensions,
                deviceExtensions,
                minApiVersionSupported,
                maxApiVersionSupported,
                null);
        }
        catch (Exception ex)
        {
            string reason = ex.Message;
            Debug.VulkanWarning($"[OpenXR] Vulkan runtime extension preflight failed: {reason}");
            return new OpenXrVulkanRuntimeRequirements([], [], 0, 0, reason);
        }
        finally
        {
            if (api is not null)
            {
                if (instance.Handle != 0)
                    api.DestroyInstance(instance);
                api.Dispose();
            }
        }
    }

    private static HashSet<string> EnumerateOpenXrInstanceExtensions(XR api)
    {
        uint count = 0;
        api.EnumerateInstanceExtensionProperties((byte*)null, 0, ref count, null);
        HashSet<string> extensions = new(StringComparer.OrdinalIgnoreCase);
        if (count == 0)
            return extensions;

        ExtensionProperties[] props = new ExtensionProperties[count];
        for (int i = 0; i < props.Length; i++)
            props[i].Type = StructureType.ExtensionProperties;

        fixed (ExtensionProperties* propsPtr = props)
        {
            api.EnumerateInstanceExtensionProperties((byte*)null, count, ref count, propsPtr);
        }

        for (int i = 0; i < count; i++)
        {
            fixed (byte* namePtr = props[i].ExtensionName)
            {
                string? name = Marshal.PtrToStringAnsi((nint)namePtr);
                if (!string.IsNullOrWhiteSpace(name))
                    extensions.Add(name);
            }
        }

        return extensions;
    }

    private static string[] QueryVulkanExtensionList(XR api, Instance instance, ulong systemId, string functionName)
    {
        PfnVoidFunction function = default;
        Result procResult = api.GetInstanceProcAddr(instance, functionName, ref function);
        nint functionPointer = (nint)function;
        if (procResult != Result.Success || functionPointer == 0)
            throw new InvalidOperationException($"{functionName} lookup failed: {procResult}");

        XrGetVulkanExtensionsKHRDelegate query =
            Marshal.GetDelegateForFunctionPointer<XrGetVulkanExtensionsKHRDelegate>(functionPointer);

        uint byteCount = 0;
        Result countResult = query(instance, systemId, 0, &byteCount, null);
        if (countResult != Result.Success)
            throw new InvalidOperationException($"{functionName} count query failed: {countResult}");

        if (byteCount == 0)
            return [];

        byte[] buffer = new byte[byteCount];
        fixed (byte* bufferPtr = buffer)
        {
            Result valuesResult = query(instance, systemId, byteCount, &byteCount, bufferPtr);
            if (valuesResult != Result.Success)
                throw new InvalidOperationException($"{functionName} value query failed: {valuesResult}");
        }

        string extensionString = Encoding.ASCII.GetString(buffer, 0, (int)byteCount).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(extensionString))
            return [];

        return extensionString
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
