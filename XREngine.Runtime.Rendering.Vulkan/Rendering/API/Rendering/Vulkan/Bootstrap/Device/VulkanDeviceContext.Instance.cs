using Silk.NET.Core;
using Silk.NET.Core.Native;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

internal sealed partial class VulkanDeviceContext
{
    private RendererNativeCallbackBridge.VulkanDebugRegistration? _debugRegistration;
    private DebugUtilsMessengerEXT _debugMessenger;

    public Vk Api { get; private set; } = null!;
    public Instance Instance { get; private set; }
    public VulkanInstanceExtensionSet EnabledInstanceExtensions { get; private set; } =
        VulkanInstanceExtensionSet.Empty;
    public uint InstanceApiVersion { get; private set; }
    public bool InstanceCreatedThroughOpenXr { get; private set; }
    public OpenXrVulkanEnable2BootstrapContext? OpenXrBootstrapContext { get; private set; }
    public ExtDebugUtils? DebugUtils { get; private set; }
    public bool HasInstance => Instance.Handle != 0;
    public bool HasDebugMessenger => _debugMessenger.Handle != 0;
    public bool ValidationLayersEnabled { get; private set; }
    public bool SynchronizationValidationEnabled { get; private set; }
    public bool CanRecordCommandBufferDebugLabels =>
        DebugUtils is not null &&
        _commandBufferDebugLabelsEnabled;

    private bool _commandBufferDebugLabelsEnabled;

    public void AttachInstance(
        Vk api,
        Instance instance,
        IEnumerable<string> enabledExtensions,
        uint apiVersion,
        bool createdThroughOpenXr,
        OpenXrVulkanEnable2BootstrapContext? openXrBootstrapContext)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (instance.Handle == 0)
            throw new ArgumentException("A valid Vulkan instance is required.", nameof(instance));
        ArgumentNullException.ThrowIfNull(enabledExtensions);
        if (HasInstance)
            throw new InvalidOperationException("The Vulkan device context already owns an instance.");
        if (PhysicalDevice.Handle != 0 || HasLogicalDevice)
            throw new InvalidOperationException("Instance identity cannot change after device selection.");
        if (createdThroughOpenXr != (openXrBootstrapContext is not null))
            throw new InvalidOperationException("OpenXR instance creation identity and bootstrap ownership disagree.");

        Api = api;
        Instance = instance;
        EnabledInstanceExtensions = new VulkanInstanceExtensionSet(enabledExtensions);
        InstanceApiVersion = apiVersion;
        InstanceCreatedThroughOpenXr = createdThroughOpenXr;
        OpenXrBootstrapContext = openXrBootstrapContext;
    }

    /// <summary>
    /// Creates and publishes the native Vulkan instance from immutable bootstrap
    /// facts. The context owns all native lifetime and validation diagnostics;
    /// the caller only projects the returned outcome into renderer statistics.
    /// </summary>
    public unsafe VulkanDeviceBootstrapResult CreateInstance(
        Vk api,
        VulkanDeviceBootstrapRequest request)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(request);
        if (HasInstance)
            throw new InvalidOperationException("The Vulkan device context already owns an instance.");

        VulkanDeviceValidationRequest validation = request.Validation;
        bool enableValidationLayers = validation.EnableValidationLayers;
        if (enableValidationLayers && !CheckValidationLayerSupport(api))
        {
            System.Console.WriteLine("Vulkan validation layers requested but not available. Continuing without them.");
            enableValidationLayers = false;
        }

        uint requestedApiVersion = ResolveRequestedApiVersion(request);
        string[] extensions = ResolveRequiredInstanceExtensions(api, request, enableValidationLayers);
        LogResolvedDiagnosticOptions(validation, enableValidationLayers, extensions);

        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("XRENGINE"),
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("XRENGINE"),
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = requestedApiVersion,
        };
        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Length,
            PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(extensions),
        };

        bool attached = false;
        try
        {
            ValidationFeatureEnableEXT* enabledValidationFeatures = stackalloc ValidationFeatureEnableEXT[4];
            DebugUtilsMessengerCreateInfoEXT debugCreateInfo = default;
            ValidationFeaturesEXT validationFeatures = default;
            uint enabledValidationFeatureCount = enableValidationLayers
                ? PopulateEnabledValidationFeatures(validation, enabledValidationFeatures)
                : 0u;

            if (enableValidationLayers)
            {
                string[] validationLayers = ["VK_LAYER_KHRONOS_validation"];
                createInfo.EnabledLayerCount = (uint)validationLayers.Length;
                createInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);
                debugCreateInfo = PrepareDebugMessengerCreateInfo();
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
                    createInfo.PNext = &debugCreateInfo;
            }

            Instance createdInstance;
            OpenXrVulkanEnable2BootstrapContext? createdOpenXrContext;
            bool createdThroughOpenXr;
            var getInstanceProcAddr = api.GetInstanceProcAddr(default, "vkGetInstanceProcAddr");
            if (OpenXRAPI.TryCreateVulkanEnable2BootstrapContext(
                out OpenXrVulkanEnable2BootstrapContext? openXrContext,
                out string? openXrContextFailure))
            {
                OpenXrVulkanEnable2BootstrapContext activeOpenXrContext = openXrContext
                    ?? throw new InvalidOperationException("OpenXR returned a successful bootstrap result without a context.");
                if (!activeOpenXrContext.TryCreateVulkanInstance(
                    &createInfo,
                    getInstanceProcAddr,
                    out nint openXrCreatedInstanceHandle,
                    out _,
                    out string? openXrCreateFailure))
                {
                    activeOpenXrContext.Dispose();
                    throw new InvalidOperationException($"Failed to create Vulkan instance through OpenXR: {openXrCreateFailure}");
                }

                createdInstance = new Instance(openXrCreatedInstanceHandle);
                createdOpenXrContext = activeOpenXrContext;
                createdThroughOpenXr = true;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(openXrContextFailure))
                    throw new InvalidOperationException($"Failed to create Vulkan OpenXR bootstrap context: {openXrContextFailure}");

                Result createResult = api.CreateInstance(ref createInfo, null, out createdInstance);
                if (createResult != Result.Success)
                    throw new InvalidOperationException($"Failed to create Vulkan instance. Result={createResult}");

                createdOpenXrContext = null;
                createdThroughOpenXr = false;
            }

            try
            {
                AttachInstance(
                    api,
                    createdInstance,
                    extensions,
                    requestedApiVersion,
                    createdThroughOpenXr,
                    createdOpenXrContext);
                attached = true;
            }
            catch
            {
                api.DestroyInstance(createdInstance, null);
                createdOpenXrContext?.Dispose();
                throw;
            }

            SetupDebugMessenger(api, enableValidationLayers, validation.EnableDebugUtils);
            ValidationLayersEnabled = enableValidationLayers;
            SynchronizationValidationEnabled =
                enableValidationLayers && validation.EnableSynchronizationValidation;
            _commandBufferDebugLabelsEnabled = validation.EnableCommandBufferLabels;
            return new VulkanDeviceBootstrapResult(
                ValidationLayersEnabled,
                SynchronizationValidationEnabled);
        }
        catch
        {
            if (attached)
                DestroyInstance(api, deviceLostReason: null);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal((nint)appInfo.PApplicationName);
            Marshal.FreeHGlobal((nint)appInfo.PEngineName);
            SilkMarshal.Free((nint)createInfo.PpEnabledExtensionNames);
            if (createInfo.PpEnabledLayerNames is not null)
                SilkMarshal.Free((nint)createInfo.PpEnabledLayerNames);
            if (!HasInstance)
                DestroyValidation(api);
        }
    }

    public unsafe DebugUtilsMessengerCreateInfoEXT PrepareDebugMessengerCreateInfo()
    {
        if (HasInstance)
            throw new InvalidOperationException("The instance-create debug callback must be prepared before native instance creation.");

        _debugRegistration ??=
            RendererNativeCallbackBridge.RegisterVulkanDebugHandler(HandleDebugMessage);
        return new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
                DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt |
                DebugUtilsMessageTypeFlagsEXT.ValidationBitExt,
            PUserData = (void*)_debugRegistration.UserData,
            PfnUserCallback = Marshal.GetDelegateForFunctionPointer<DebugUtilsMessengerCallbackFunctionEXT>(
                RendererNativeCallbackBridge.VulkanDebugCallbackPointer),
        };
    }

    private string[] ResolveRequiredInstanceExtensions(
        Vk api,
        VulkanDeviceBootstrapRequest request,
        bool enableValidationLayers)
    {
        List<string> extensions = [.. request.TargetInstanceExtensions];
        MutableCapabilities._supportsSwapchainColorspace =
            request.RequireSwapchainOutput &&
            IsInstanceExtensionAvailable(api, "VK_EXT_swapchain_colorspace");
        if (MutableCapabilities._supportsSwapchainColorspace)
            extensions.Add("VK_EXT_swapchain_colorspace");

        MutableCapabilities._surfacePresentScalingInstanceExtensionsEnabled =
            request.RequireSwapchainOutput &&
            IsInstanceExtensionAvailable(api, "VK_KHR_get_surface_capabilities2") &&
            IsInstanceExtensionAvailable(api, "VK_EXT_surface_maintenance1");
        if (MutableCapabilities._surfacePresentScalingInstanceExtensionsEnabled)
        {
            extensions.Add("VK_KHR_get_surface_capabilities2");
            extensions.Add("VK_EXT_surface_maintenance1");
        }

        extensions.AddRange(request.OpenXrInstanceExtensions);
        foreach (string extension in request.StreamlineInstanceExtensions)
        {
            if (!IsInstanceExtensionAvailable(api, extension))
            {
                throw new NotSupportedException(
                    $"Streamline requires unavailable Vulkan instance extension '{extension}'.");
            }

            extensions.Add(extension);
        }

        if (enableValidationLayers || request.Validation.EnableDebugUtils)
            extensions.Add(ExtDebugUtils.ExtensionName);

        return [.. extensions
            .Where(static extension => !string.IsNullOrWhiteSpace(extension))
            .Distinct(StringComparer.Ordinal)];
    }

    private static unsafe bool IsInstanceExtensionAvailable(Vk api, string extensionName)
    {
        uint extensionCount = 0;
        if (api.EnumerateInstanceExtensionProperties((byte*)null, ref extensionCount, null) != Result.Success ||
            extensionCount == 0)
        {
            return false;
        }

        ExtensionProperties[] properties = new ExtensionProperties[extensionCount];
        fixed (ExtensionProperties* propertiesPointer = properties)
        {
            if (api.EnumerateInstanceExtensionProperties(
                    (byte*)null,
                    ref extensionCount,
                    propertiesPointer) != Result.Success)
            {
                return false;
            }
        }

        for (int i = 0; i < extensionCount; i++)
        {
            string? availableName = ReadExtensionName(properties[i]);
            if (string.Equals(availableName, extensionName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static unsafe string? ReadExtensionName(ExtensionProperties property)
    {
        byte* extensionNamePointer = property.ExtensionName;
        return SilkMarshal.PtrToString((nint)extensionNamePointer);
    }

    private static uint ResolveRequestedApiVersion(VulkanDeviceBootstrapRequest request)
    {
        uint defaultApiVersion = Vk.Version13;
        if (request.OpenXrMaximumApiVersion == 0)
            return Math.Max(defaultApiVersion, request.StreamlineMinimumApiVersion);

        uint minimumApiVersion = ConvertOpenXrApiVersion(request.OpenXrMinimumApiVersion);
        uint maximumApiVersion = ConvertOpenXrApiVersion(request.OpenXrMaximumApiVersion);
        if (maximumApiVersion == 0)
            return defaultApiVersion;

        if (minimumApiVersion != 0 && maximumApiVersion < minimumApiVersion)
        {
            Debug.VulkanWarning(
                "[OpenXR] Ignoring invalid Vulkan API version range from runtime: min={0} max={1}.",
                request.OpenXrMinimumApiVersion,
                request.OpenXrMaximumApiVersion);
            return defaultApiVersion;
        }

        uint resolvedApiVersion = defaultApiVersion;
        if (minimumApiVersion != 0 && resolvedApiVersion < minimumApiVersion)
            resolvedApiVersion = minimumApiVersion;
        if (resolvedApiVersion > maximumApiVersion)
            resolvedApiVersion = maximumApiVersion;

        if (resolvedApiVersion < request.StreamlineMinimumApiVersion)
        {
            throw new NotSupportedException(
                $"Streamline requires Vulkan {FormatApiVersion(request.StreamlineMinimumApiVersion)}, but the active OpenXR runtime caps Vulkan at {FormatApiVersion(maximumApiVersion)}.");
        }

        if (resolvedApiVersion != defaultApiVersion)
        {
            Debug.Vulkan(
                "[OpenXR] Vulkan instance API version resolved to {0} for OpenXR runtime range {1}-{2} (default {3}).",
                FormatApiVersion(resolvedApiVersion),
                minimumApiVersion == 0 ? "<unknown>" : FormatApiVersion(minimumApiVersion),
                FormatApiVersion(maximumApiVersion),
                FormatApiVersion(defaultApiVersion));
        }

        return resolvedApiVersion;
    }

    private static uint ConvertOpenXrApiVersion(ulong openXrApiVersion)
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

    private static string FormatApiVersion(uint apiVersion)
        => $"{apiVersion >> 22}.{(apiVersion >> 12) & 0x3FFu}.{apiVersion & 0xFFFu}";

    private static unsafe uint PopulateEnabledValidationFeatures(
        VulkanDeviceValidationRequest validation,
        ValidationFeatureEnableEXT* enabledFeatures)
    {
        uint count = 0;
        if (validation.EnableSynchronizationValidation)
            enabledFeatures[count++] = ValidationFeatureEnableEXT.SynchronizationValidationExt;
        if (validation.EnableGpuAssistedValidation)
        {
            enabledFeatures[count++] = ValidationFeatureEnableEXT.GpuAssistedExt;
            enabledFeatures[count++] = ValidationFeatureEnableEXT.GpuAssistedReserveBindingSlotExt;
        }
        if (validation.EnableBestPractices)
            enabledFeatures[count++] = ValidationFeatureEnableEXT.BestPracticesExt;
        return count;
    }

    private static void LogResolvedDiagnosticOptions(
        VulkanDeviceValidationRequest validation,
        bool enableValidationLayers,
        IReadOnlyList<string> instanceExtensions)
    {
        Debug.Vulkan(
            "[VulkanDiag] Preset={0} Flags={1} ValidationLayers={2} DebugUtils={3} Labels={4} Breadcrumbs={5} RenderDocFriendly={6} Source='{7}'",
            validation.Preset,
            validation.Flags,
            enableValidationLayers,
            validation.EnableDebugUtils,
            validation.EnableCommandBufferLabels,
            validation.EnableCrashBreadcrumbs,
            validation.Flags.HasFlag(EVulkanDiagnosticFlags.RenderDocFriendly),
            validation.SourceSummary);
        if (!string.IsNullOrWhiteSpace(validation.OverheadWarnings))
            Debug.VulkanWarning("[VulkanDiag] Overhead warnings: {0}", validation.OverheadWarnings);

        Debug.Vulkan("[VulkanDiag] InstanceExtensions={0}", string.Join(",", instanceExtensions));
        Debug.Vulkan(
            "[VulkanDiag] ValidationLayer VK_LAYER_KHRONOS_validation: {0}",
            enableValidationLayers
                ? "enabled"
                : "disabled: no validation diagnostic flag requested or layer unavailable");
        Debug.Vulkan("[VulkanDiag] ValidationFeatures={0}", DescribeEnabledValidationFeatures(validation));
    }

    private static string DescribeEnabledValidationFeatures(VulkanDeviceValidationRequest validation)
    {
        StringBuilder builder = new();
        AppendValidationFeature(builder, validation.EnableSynchronizationValidation, "SynchronizationValidation");
        AppendValidationFeature(builder, validation.EnableGpuAssistedValidation, "GpuAssisted");
        AppendValidationFeature(builder, validation.EnableGpuAssistedValidation, "GpuAssistedReserveBindingSlot");
        AppendValidationFeature(builder, validation.EnableBestPractices, "BestPractices");
        return builder.Length == 0 ? "<none>" : builder.ToString();
    }

    private static void AppendValidationFeature(StringBuilder builder, bool enabled, string name)
    {
        if (!enabled)
            return;
        if (builder.Length > 0)
            builder.Append(',');
        builder.Append(name);
    }

    private static unsafe bool CheckValidationLayerSupport(Vk api)
    {
        uint layerCount = 0;
        api.EnumerateInstanceLayerProperties(ref layerCount, null);
        LayerProperties[] availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPointer = availableLayers)
            api.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPointer);

        for (int i = 0; i < layerCount; i++)
        {
            string? availableName = ReadLayerName(availableLayers[i]);
            if (string.Equals(availableName, "VK_LAYER_KHRONOS_validation", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static unsafe string? ReadLayerName(LayerProperties property)
    {
        byte* layerNamePointer = property.LayerName;
        return Marshal.PtrToStringAnsi((nint)layerNamePointer);
    }

    public unsafe void SetupDebugMessenger(
        Vk api,
        bool enableValidationLayers,
        bool enableDebugUtils)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (!HasInstance)
            throw new InvalidOperationException("A Vulkan instance is required before loading debug utilities.");
        if (!enableValidationLayers && !enableDebugUtils)
            return;
        if (!api.TryGetInstanceExtension(Instance, out ExtDebugUtils? debugUtils))
            return;

        ExtDebugUtils loadedDebugUtils = debugUtils!;
        DebugUtils = loadedDebugUtils;
        if (!enableValidationLayers)
            return;
        if (HasDebugMessenger)
            throw new InvalidOperationException("The Vulkan debug messenger is already active.");

        DebugUtilsMessengerCreateInfoEXT createInfo =
            PreparePostInstanceDebugMessengerCreateInfo();
        Result result = loadedDebugUtils.CreateDebugUtilsMessenger(
            Instance,
            in createInfo,
            null,
            out DebugUtilsMessengerEXT messenger);
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to create the Vulkan debug messenger. Result={result}.");

        _debugMessenger = messenger;
    }

    public unsafe bool CmdBeginLabel(CommandBuffer commandBuffer, string name)
    {
        if (!CanRecordCommandBufferDebugLabels)
            return false;

        nint namePointer = SilkMarshal.StringToPtr(name);
        try
        {
            DebugUtilsLabelEXT label = new()
            {
                SType = StructureType.DebugUtilsLabelExt,
                PLabelName = (byte*)namePointer,
            };
            DebugUtils!.CmdBeginDebugUtilsLabel(commandBuffer, in label);
            return true;
        }
        finally
        {
            SilkMarshal.Free(namePointer);
        }
    }

    public void CmdEndLabel(CommandBuffer commandBuffer)
    {
        if (CanRecordCommandBufferDebugLabels)
            DebugUtils!.CmdEndDebugUtilsLabel(commandBuffer);
    }

    public unsafe void SetDebugObjectName(ObjectType objectType, ulong objectHandle, string name)
    {
        if (DebugUtils is null ||
            Device.Handle == 0 ||
            objectHandle == 0 ||
            string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        nint namePointer = SilkMarshal.StringToPtr(name);
        try
        {
            DebugUtilsObjectNameInfoEXT nameInfo = new()
            {
                SType = StructureType.DebugUtilsObjectNameInfoExt,
                ObjectType = objectType,
                ObjectHandle = objectHandle,
                PObjectName = (byte*)namePointer,
            };
            _ = DebugUtils.SetDebugUtilsObjectName(Device, in nameInfo);
        }
        finally
        {
            SilkMarshal.Free(namePointer);
        }
    }

    public void SetDebugDescriptorSetName(DescriptorSet descriptorSet, string name)
        => SetDebugObjectName(ObjectType.DescriptorSet, descriptorSet.Handle, name);

    public void SetDebugDescriptorSetNames(DescriptorSet[]? descriptorSets, string prefix)
    {
        if (descriptorSets is null || descriptorSets.Length == 0)
            return;

        for (int i = 0; i < descriptorSets.Length; i++)
            SetDebugDescriptorSetName(descriptorSets[i], $"{prefix}[{i}]");
    }

    public string DescribeValidationSummary(int maxEntries = 6)
        => ValidationDiagnostics.Describe(maxEntries);

    private unsafe DebugUtilsMessengerCreateInfoEXT PreparePostInstanceDebugMessengerCreateInfo()
    {
        _debugRegistration ??=
            RendererNativeCallbackBridge.RegisterVulkanDebugHandler(HandleDebugMessage);
        return new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
                DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt |
                DebugUtilsMessageTypeFlagsEXT.ValidationBitExt,
            PUserData = (void*)_debugRegistration.UserData,
            PfnUserCallback = Marshal.GetDelegateForFunctionPointer<DebugUtilsMessengerCallbackFunctionEXT>(
                RendererNativeCallbackBridge.VulkanDebugCallbackPointer),
        };
    }

    private uint HandleDebugMessage(
        uint messageSeverity,
        uint messageTypes,
        nint callbackData,
        nint userData)
    {
        VulkanSubmissionDiagnosticContext submission = SnapshotSubmissionDiagnostics();
        return ValidationDiagnostics.HandleDebugMessage(
            messageSeverity,
            messageTypes,
            callbackData,
            in submission);
    }

    public unsafe void DestroyValidation(Vk api)
    {
        DestroyDebugMessenger(api);
        ReleaseDebugRegistration();
    }

    private unsafe void DestroyDebugMessenger(Vk api)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (DebugUtils is not null && HasDebugMessenger && HasInstance)
            DebugUtils.DestroyDebugUtilsMessenger(Instance, _debugMessenger, null);

        _debugMessenger = default;
        DebugUtils = null;
    }

    private void ReleaseDebugRegistration()
    {
        Interlocked.Exchange(ref _debugRegistration, null)?.Dispose();
    }

    public bool TryGetOpenXrBootstrapInstance(
        out Silk.NET.OpenXR.XR api,
        out Silk.NET.OpenXR.Instance xrInstance,
        out string[] enabledExtensions)
    {
        if (OpenXrBootstrapContext is not null)
        {
            api = OpenXrBootstrapContext.Api;
            xrInstance = OpenXrBootstrapContext.XrInstance;
            enabledExtensions = OpenXrBootstrapContext.EnabledExtensions;
            return xrInstance.Handle != 0;
        }

        api = null!;
        xrInstance = default;
        enabledExtensions = [];
        return false;
    }

    public bool InvalidateOpenXrBootstrapInstance(string reason)
    {
        OpenXrVulkanEnable2BootstrapContext? context = OpenXrBootstrapContext;
        if (context is null)
            return false;

        bool ownsOpenXrCreatedDevice = InstanceCreatedThroughOpenXr && CreatedThroughOpenXr;
        context.AbandonXrInstanceOnDispose(reason);
        context.Dispose();
        OpenXrBootstrapContext = null;
        Debug.VulkanWarning(
            "[OpenXR] Invalidated XR_KHR_vulkan_enable2 bootstrap instance. Reason={0}",
            string.IsNullOrWhiteSpace(reason) ? "<unspecified>" : reason);
        if (ownsOpenXrCreatedDevice)
        {
            Debug.VulkanWarning(
                "[OpenXR] Vulkan handles were created through XR_KHR_vulkan_enable2; keeping the logical device live after XR instance teardown. Vulkan device loss will be reported separately if the driver/runtime invalidates the handles.");
        }
        return true;
    }

    public unsafe void DestroyInstance(Vk api, string? deviceLostReason)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (HasLogicalDevice || PhysicalDevice.Handle != 0)
            throw new InvalidOperationException("Logical and physical device state must be cleared before destroying the Vulkan instance.");

        // Keep the create-info callback registration alive through instance
        // destruction so the loader can report teardown diagnostics after the
        // explicit messenger has been removed.
        DestroyDebugMessenger(api);
        if (HasInstance)
            api.DestroyInstance(Instance, null);
        ReleaseDebugRegistration();

        if (FirstNativeDeviceFault is not null)
        {
            OpenXrBootstrapContext?.AbandonXrInstanceOnDispose(
                string.IsNullOrWhiteSpace(deviceLostReason)
                    ? "Vulkan logical device lost"
                    : deviceLostReason);
        }

        OpenXrBootstrapContext?.Dispose();
        OpenXrBootstrapContext = null;
        Instance = default;
        EnabledInstanceExtensions = VulkanInstanceExtensionSet.Empty;
        InstanceApiVersion = 0;
        InstanceCreatedThroughOpenXr = false;
        ValidationLayersEnabled = false;
        SynchronizationValidationEnabled = false;
        _commandBufferDebugLabelsEnabled = false;
    }
}
