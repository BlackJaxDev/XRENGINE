using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Owns the selected physical device and immutable capability snapshot, the
/// logical-device handle, enabled extension authority, and the queues selected
/// for engine work. Queue handles and final capabilities are published once and
/// cleared together so renderer code cannot observe partial native readiness.
/// </summary>
internal sealed partial class VulkanDeviceContext
{
    private VulkanNativeDeviceFault? _firstNativeDeviceFault;
    private int _deviceLossDiagnosticsClaimed;
    private int _capabilityPublicationState;
    private int _queuesPublished;
    private readonly object _submissionDiagnosticsLock = new();
    private VulkanSubmissionDiagnosticContext _lastSubmissionDiagnostics;

    public VulkanDeviceContext(
        VulkanDeviceContextConfiguration? configuration = null,
        VulkanPresentationSupportProbe? presentationSupportProbe = null)
    {
        Configuration = configuration ?? VulkanDeviceContextConfiguration.Default;
        PresentationSupportProbe = presentationSupportProbe;
    }

    /// <summary>
    /// Immutable policy captured before native device creation begins.
    /// </summary>
    public VulkanDeviceContextConfiguration Configuration { get; }

    /// <summary>
    /// Explicit output-owned presentation support query. The context does not
    /// infer output state or retain an output runtime.
    /// </summary>
    public VulkanPresentationSupportProbe? PresentationSupportProbe { get; private set; }

    /// <summary>
    /// Owns admission to the native device lifetime. The context, rather than
    /// its renderer composition root, defines whether new Vulkan work is legal.
    /// </summary>
    public VulkanDeviceStateMachine StateMachine { get; } = new();

    public VulkanDeviceExtensionFunctions ExtensionFunctions { get; } = new();
    public VulkanDeviceMutableCapabilities MutableCapabilities { get; } = new();
    public VulkanDeviceFaultFacility DeviceFaultFacility { get; } = new();
    public VulkanValidationDiagnostics ValidationDiagnostics { get; } = new();

    public PhysicalDevice PhysicalDevice { get; private set; }
    public VulkanPhysicalDeviceCapabilitySnapshot? PhysicalDeviceCapabilities { get; private set; }
    public QueueFamilyIndices QueueFamilies { get; private set; }
    public VulkanDeviceExtensionSet AvailableDeviceExtensions { get; private set; } = VulkanDeviceExtensionSet.Empty;
    public VulkanDeviceExtensionSet EnabledDeviceExtensions { get; private set; } = VulkanDeviceExtensionSet.Empty;
    public VulkanDeviceCapabilities Capabilities { get; private set; } = VulkanDeviceCapabilities.Empty;
    public Device Device { get; private set; }
    public bool CreatedThroughOpenXr { get; private set; }
    public bool SupportsMultipleGraphicsQueues { get; private set; }
    public bool HasLogicalDevice => Device.Handle != 0;
    public bool CapabilitiesPublished =>
        Volatile.Read(ref _capabilityPublicationState) == 2;
    public bool QueuesPublished => Volatile.Read(ref _queuesPublished) != 0;
    public bool IsReady =>
        HasInstance &&
        HasLogicalDevice &&
        QueuesPublished &&
        CapabilitiesPublished;
    public bool IsOperational => IsReady && StateMachine.IsOperational;
    public EVulkanDeviceState State => StateMachine.State;
    public VulkanNativeDeviceFault? FirstNativeDeviceFault
        => Volatile.Read(ref _firstNativeDeviceFault);
    public bool HasSecondaryGraphicsQueue =>
        SupportsMultipleGraphicsQueues &&
        SecondaryGraphicsQueue.Handle != 0;
    public ulong NonCoherentAtomSize { get; private set; } = 1UL;
    public ulong MinUniformBufferOffsetAlignment { get; private set; } = 1UL;

    public Queue GraphicsQueue { get; private set; }
    public Queue SecondaryGraphicsQueue { get; private set; }
    public Queue PresentQueue { get; private set; }
    public Queue ComputeQueue { get; private set; }
    public Queue TransferQueue { get; private set; }

    public void RecordSubmissionDiagnostics(in VulkanSubmissionDiagnosticContext diagnostics)
    {
        lock (_submissionDiagnosticsLock)
            _lastSubmissionDiagnostics = diagnostics;
    }

    public VulkanSubmissionDiagnosticContext SnapshotSubmissionDiagnostics()
    {
        lock (_submissionDiagnosticsLock)
            return _lastSubmissionDiagnostics;
    }

    /// <summary>
    /// Publishes the output-owned presentation query before physical-device
    /// selection. The delegate must close over native surface state only, never
    /// the renderer facade or output runtime.
    /// </summary>
    public void AttachPresentationSupportProbe(
        VulkanPresentationSupportProbe? presentationSupportProbe)
    {
        if (PhysicalDevice.Handle != 0)
            throw new InvalidOperationException("Presentation support cannot change after physical-device selection.");
        if (Configuration.RequirePresentQueue && presentationSupportProbe is null)
            throw new InvalidOperationException("A presentation support probe is required by this device context.");
        if (!Configuration.RequirePresentQueue && presentationSupportProbe is not null)
            throw new InvalidOperationException("A presentation support probe cannot be attached to a presentationless device context.");
        if (PresentationSupportProbe is not null)
            throw new InvalidOperationException("Presentation support has already been published for this device context.");

        PresentationSupportProbe = presentationSupportProbe;
    }

    public QueueFamilyIndices SelectQueueFamilies(
        PhysicalDevice physicalDevice,
        VulkanPhysicalDeviceCapabilitySnapshot capabilities)
    {
        if (physicalDevice.Handle == 0)
            throw new ArgumentException("A valid physical device is required.", nameof(physicalDevice));
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!HasInstance)
            throw new InvalidOperationException("A Vulkan instance must exist before selecting a physical device.");
        if (Configuration.RequirePresentQueue && PresentationSupportProbe is null)
            throw new InvalidOperationException("Presentation support must be published before queue-family selection.");

        return VulkanQueueFamilySelector.Select(
            capabilities.QueueFamilyArray,
            physicalDevice,
            PresentationSupportProbe);
    }

    public bool SupportsRequiredDeviceExtensions(
        VulkanDeviceExtensionSet availableExtensions,
        IEnumerable<string>? additionalRequiredExtensions = null)
    {
        ArgumentNullException.ThrowIfNull(availableExtensions);
        foreach (string requiredExtension in Configuration.RequiredDeviceExtensions)
        {
            if (!availableExtensions.Contains(requiredExtension))
                return false;
        }

        if (additionalRequiredExtensions is null)
            return true;
        foreach (string requiredExtension in additionalRequiredExtensions)
        {
            if (!string.IsNullOrWhiteSpace(requiredExtension) &&
                !availableExtensions.Contains(requiredExtension))
            {
                return false;
            }
        }

        return true;
    }

    public void AttachPhysicalDevice(
        PhysicalDevice physicalDevice,
        VulkanPhysicalDeviceCapabilitySnapshot capabilities,
        in QueueFamilyIndices queueFamilies)
    {
        if (physicalDevice.Handle == 0)
            throw new ArgumentException("A valid physical device is required.", nameof(physicalDevice));
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!queueFamilies.IsComplete(Configuration.RequirePresentQueue))
            throw new ArgumentException("The selected queue families do not satisfy device-context policy.", nameof(queueFamilies));
        if (!HasInstance)
            throw new InvalidOperationException("A Vulkan instance must exist before selecting a physical device.");
        if (PhysicalDevice.Handle != 0)
            throw new InvalidOperationException("The Vulkan device context already owns a physical-device selection.");
        if (HasLogicalDevice)
            throw new InvalidOperationException("Physical-device identity cannot change after logical-device creation.");

        PhysicalDevice = physicalDevice;
        PhysicalDeviceCapabilities = capabilities;
        QueueFamilies = queueFamilies;
        AvailableDeviceExtensions = capabilities.AvailableExtensions;
        EnabledDeviceExtensions = VulkanDeviceExtensionSet.Empty;
        Capabilities = VulkanDeviceCapabilities.Empty;
        NonCoherentAtomSize = Math.Max(capabilities.Properties.Limits.NonCoherentAtomSize, 1UL);
        MinUniformBufferOffsetAlignment = Math.Max(
            capabilities.Properties.Limits.MinUniformBufferOffsetAlignment,
            1UL);
    }

    /// <summary>
    /// Publishes the exact extension set selected for logical-device creation.
    /// </summary>
    public VulkanDeviceExtensionSet ValidateEnabledDeviceExtensions(IEnumerable<string> enabledExtensions)
    {
        ArgumentNullException.ThrowIfNull(enabledExtensions);
        if (PhysicalDevice.Handle == 0)
            throw new InvalidOperationException("A physical device must be selected before enabling device extensions.");
        if (HasLogicalDevice)
            throw new InvalidOperationException("Enabled device extensions cannot change after logical-device creation.");

        VulkanDeviceExtensionSet selected = new(enabledExtensions);
        foreach (string requiredExtension in Configuration.RequiredDeviceExtensions)
        {
            if (!selected.Contains(requiredExtension))
                throw new InvalidOperationException($"Required Vulkan device extension '{requiredExtension}' was not enabled.");
        }
        foreach (string extension in selected)
        {
            if (!AvailableDeviceExtensions.Contains(extension))
                throw new InvalidOperationException($"Enabled Vulkan device extension '{extension}' was not advertised by the selected physical device.");
        }

        return selected;
    }

    /// <summary>
    /// Publishes the immutable post-bootstrap capability contract exactly once.
    /// </summary>
    public void PublishCapabilities(VulkanDeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!HasLogicalDevice)
            throw new InvalidOperationException("A logical device must exist before publishing Vulkan capabilities.");
        if (EnabledDeviceExtensions.Count == 0 && capabilities.EnabledExtensions.Count != 0)
            throw new InvalidOperationException("Enabled Vulkan device extensions must be published before capabilities.");
        if (Interlocked.CompareExchange(ref _capabilityPublicationState, 1, 0) != 0)
            throw new InvalidOperationException("Vulkan device capabilities have already been published for this device lifetime.");
        try
        {
            if (!ReferenceEquals(capabilities.AvailableExtensions, AvailableDeviceExtensions) ||
                !ReferenceEquals(capabilities.EnabledExtensions, EnabledDeviceExtensions))
            {
                throw new InvalidOperationException("Published Vulkan capabilities must reuse the device context's extension authorities.");
            }

            Capabilities = capabilities;
            Volatile.Write(ref _capabilityPublicationState, 2);
        }
        catch
        {
            Volatile.Write(ref _capabilityPublicationState, 0);
            throw;
        }
    }

    /// <summary>
    /// Atomically closes admission to new native work for the first observer of
    /// device loss. Cold-path diagnostics are collected by the caller before
    /// <see cref="CompleteDeviceLossCollection"/> quiesces the context.
    /// </summary>
    public bool TryBeginDeviceLossCollection()
        => StateMachine.TryBeginLossCollection();

    /// <summary>
    /// Closes native admission at the first typed device-loss result and preserves the original
    /// operation independently of renderer lifetime or callback execution.
    /// </summary>
    public void ObserveNativeResult(string operation, Result result)
    {
        if (result != Result.ErrorDeviceLost)
            return;

        VulkanNativeDeviceFault fault = new(operation, result);
        _ = Interlocked.CompareExchange(
            ref _firstNativeDeviceFault,
            fault,
            null);
        _ = TryBeginDeviceLossCollection();
    }

    /// <summary>
    /// Claims the one renderer-side cold diagnostic pass after admission has already closed.
    /// </summary>
    public bool TryClaimDeviceLossDiagnostics()
        => Interlocked.CompareExchange(
            ref _deviceLossDiagnosticsClaimed,
            1,
            0) == 0;

    /// <summary>
    /// Publishes completion of cold-path device-loss collection.
    /// </summary>
    public void CompleteDeviceLossCollection()
        => StateMachine.CompleteLossCollection();

    /// <summary>
    /// Permanently closes the current native device lifetime.
    /// </summary>
    public void MarkDisposed()
        => StateMachine.Dispose();

    /// <summary>
    /// Publishes a newly created device before extension functions are loaded.
    /// Queue handles remain unavailable until <see cref="ResolveQueues"/>.
    /// </summary>
    public void AttachDevice(
        Device device,
        bool createdThroughOpenXr,
        VulkanDeviceExtensionSet enabledExtensions)
    {
        if (device.Handle == 0)
            throw new ArgumentException("A valid logical device is required.", nameof(device));
        ArgumentNullException.ThrowIfNull(enabledExtensions);
        if (PhysicalDevice.Handle == 0 || PhysicalDeviceCapabilities is null)
            throw new InvalidOperationException("A physical device must be selected before attaching a logical device.");
        if (HasLogicalDevice)
            throw new InvalidOperationException("The Vulkan device context already owns a logical device.");
        foreach (string extension in enabledExtensions)
        {
            if (!AvailableDeviceExtensions.Contains(extension))
                throw new InvalidOperationException($"Enabled Vulkan device extension '{extension}' was not advertised by the selected physical device.");
        }

        Device = device;
        CreatedThroughOpenXr = createdThroughOpenXr;
        EnabledDeviceExtensions = enabledExtensions;
    }

    /// <summary>
    /// Resolves every queue handle once after <c>vkCreateDevice</c>.
    /// </summary>
    public void ResolveQueues(
        Vk api,
        bool supportsMultipleGraphicsQueues)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (!HasLogicalDevice)
            throw new InvalidOperationException("A logical device must be attached before resolving queues.");
        if (QueuesPublished)
            throw new InvalidOperationException("Vulkan device queues have already been published for this device lifetime.");
        VulkanPhysicalDeviceCapabilitySnapshot capabilities = PhysicalDeviceCapabilities
            ?? throw new InvalidOperationException("Physical-device capabilities are unavailable while resolving queues.");

        QueueFamilyIndices indices = QueueFamilies;
        uint graphicsFamily = indices.GraphicsFamilyIndex
            ?? throw new InvalidOperationException("A graphics queue family is required before logical-device creation.");
        uint? presentFamily = indices.PresentFamilyIndex;
        if (Configuration.RequirePresentQueue && !presentFamily.HasValue)
            throw new InvalidOperationException("A presentation queue family is required before logical-device creation.");
        uint computeFamily = indices.ComputeFamilyIndex ?? graphicsFamily;
        uint transferFamily = indices.TransferFamilyIndex ?? computeFamily;
        ValidateQueueFamily(capabilities, graphicsFamily, requiredQueueCount: supportsMultipleGraphicsQueues ? 2U : 1U, "graphics");
        if (presentFamily.HasValue)
            ValidateQueueFamily(capabilities, presentFamily.Value, 1U, "present");
        ValidateQueueFamily(capabilities, computeFamily, 1U, "compute");
        ValidateQueueFamily(capabilities, transferFamily, 1U, "transfer");

        api.GetDeviceQueue(Device, graphicsFamily, 0, out Queue graphicsQueue);
        Queue secondaryGraphicsQueue = default;
        if (supportsMultipleGraphicsQueues)
            api.GetDeviceQueue(Device, graphicsFamily, 1, out secondaryGraphicsQueue);

        Queue presentQueue = default;
        if (presentFamily.HasValue)
            api.GetDeviceQueue(Device, presentFamily.Value, 0, out presentQueue);
        api.GetDeviceQueue(Device, computeFamily, 0, out Queue computeQueue);
        api.GetDeviceQueue(Device, transferFamily, 0, out Queue transferQueue);
        if (graphicsQueue.Handle == 0 ||
            computeQueue.Handle == 0 ||
            transferQueue.Handle == 0 ||
            (Configuration.RequirePresentQueue && presentQueue.Handle == 0) ||
            (supportsMultipleGraphicsQueues && secondaryGraphicsQueue.Handle == 0))
        {
            throw new InvalidOperationException("Vulkan returned an incomplete queue set for the selected device context.");
        }

        SupportsMultipleGraphicsQueues = supportsMultipleGraphicsQueues;
        GraphicsQueue = graphicsQueue;
        SecondaryGraphicsQueue = secondaryGraphicsQueue;
        PresentQueue = presentQueue;
        ComputeQueue = computeQueue;
        TransferQueue = transferQueue;
        Volatile.Write(ref _queuesPublished, 1);
    }

    private static void ValidateQueueFamily(
        VulkanPhysicalDeviceCapabilitySnapshot capabilities,
        uint familyIndex,
        uint requiredQueueCount,
        string role)
    {
        if (familyIndex >= capabilities.QueueFamilyArray.Length)
            throw new InvalidOperationException($"Selected Vulkan {role} queue family {familyIndex} is outside the physical-device snapshot.");
        if (capabilities.QueueFamilyArray[familyIndex].QueueCount < requiredQueueCount)
        {
            throw new InvalidOperationException(
                $"Selected Vulkan {role} queue family {familyIndex} exposes {capabilities.QueueFamilyArray[familyIndex].QueueCount} queues; {requiredQueueCount} are required.");
        }
    }

    public void LoadExtensionFunctions(Vk api)
    {
        if (!HasLogicalDevice)
            throw new InvalidOperationException("A logical device must be attached before loading extension functions.");

        HashSet<string> enabledExtensionSet = new(EnabledDeviceExtensions, StringComparer.Ordinal);
        ExtensionFunctions.Load(api, Instance, Device, enabledExtensionSet);
    }

    /// <summary>
    /// Destroys the owned logical device exactly once and clears all published
    /// handles as one lifecycle transition.
    /// </summary>
    public unsafe void Destroy(Vk api)
    {
        ArgumentNullException.ThrowIfNull(api);
        MarkDisposed();
        if (HasLogicalDevice)
            api.DestroyDevice(Device, null);
        ExtensionFunctions.Clear();
        DeviceFaultFacility.Reset();
        Device = default;
        PhysicalDevice = default;
        PhysicalDeviceCapabilities = null;
        QueueFamilies = default;
        PresentationSupportProbe = null;
        AvailableDeviceExtensions = VulkanDeviceExtensionSet.Empty;
        EnabledDeviceExtensions = VulkanDeviceExtensionSet.Empty;
        Capabilities = VulkanDeviceCapabilities.Empty;
        Volatile.Write(ref _capabilityPublicationState, 0);
        NonCoherentAtomSize = 1UL;
        MinUniformBufferOffsetAlignment = 1UL;
        CreatedThroughOpenXr = false;
        SupportsMultipleGraphicsQueues = false;
        GraphicsQueue = default;
        SecondaryGraphicsQueue = default;
        PresentQueue = default;
        ComputeQueue = default;
        TransferQueue = default;
        Volatile.Write(ref _queuesPublished, 0);
    }
}
