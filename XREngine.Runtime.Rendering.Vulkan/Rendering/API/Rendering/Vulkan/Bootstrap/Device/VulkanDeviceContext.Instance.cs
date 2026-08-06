using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using XREngine.Rendering.API.Rendering.OpenXR;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

internal sealed partial class VulkanDeviceContext
{
    private RendererNativeCallbackBridge.VulkanDebugRegistration? _debugRegistration;
    private DebugUtilsMessengerEXT _debugMessenger;

    public Instance Instance { get; private set; }
    public VulkanInstanceExtensionSet EnabledInstanceExtensions { get; private set; } =
        VulkanInstanceExtensionSet.Empty;
    public uint InstanceApiVersion { get; private set; }
    public bool InstanceCreatedThroughOpenXr { get; private set; }
    public OpenXrVulkanEnable2BootstrapContext? OpenXrBootstrapContext { get; private set; }
    public ExtDebugUtils? DebugUtils { get; private set; }
    public bool HasInstance => Instance.Handle != 0;
    public bool HasDebugMessenger => _debugMessenger.Handle != 0;

    public void AttachInstance(
        Instance instance,
        IEnumerable<string> enabledExtensions,
        uint apiVersion,
        bool createdThroughOpenXr,
        OpenXrVulkanEnable2BootstrapContext? openXrBootstrapContext)
    {
        if (instance.Handle == 0)
            throw new ArgumentException("A valid Vulkan instance is required.", nameof(instance));
        ArgumentNullException.ThrowIfNull(enabledExtensions);
        if (HasInstance)
            throw new InvalidOperationException("The Vulkan device context already owns an instance.");
        if (PhysicalDevice.Handle != 0 || HasLogicalDevice)
            throw new InvalidOperationException("Instance identity cannot change after device selection.");
        if (createdThroughOpenXr != (openXrBootstrapContext is not null))
            throw new InvalidOperationException("OpenXR instance creation identity and bootstrap ownership disagree.");

        Instance = instance;
        EnabledInstanceExtensions = new VulkanInstanceExtensionSet(enabledExtensions);
        InstanceApiVersion = apiVersion;
        InstanceCreatedThroughOpenXr = createdThroughOpenXr;
        OpenXrBootstrapContext = openXrBootstrapContext;
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

        context.AbandonXrInstanceOnDispose(reason);
        context.Dispose();
        OpenXrBootstrapContext = null;
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
    }
}
