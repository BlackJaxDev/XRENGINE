using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Owns the logical-device handle and the queues selected for engine work.
/// Device and queue handles are published together and cleared together so no
/// renderer code can retain a partially initialized queue set.
/// </summary>
internal sealed class VulkanDeviceContext
{
    public VulkanDeviceExtensionFunctions ExtensionFunctions { get; } = new();

    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public bool CreatedThroughOpenXr { get; private set; }
    public bool SupportsMultipleGraphicsQueues { get; private set; }
    public bool IsReady => Device.Handle != 0;
    public bool HasSecondaryGraphicsQueue =>
        SupportsMultipleGraphicsQueues &&
        SecondaryGraphicsQueue.Handle != 0;

    public uint GraphicsFamily { get; private set; }
    public uint PresentFamily { get; private set; }
    public uint ComputeFamily { get; private set; }
    public uint TransferFamily { get; private set; }

    public Queue GraphicsQueue { get; private set; }
    public Queue SecondaryGraphicsQueue { get; private set; }
    public Queue PresentQueue { get; private set; }
    public Queue ComputeQueue { get; private set; }
    public Queue TransferQueue { get; private set; }

    public void AttachPhysicalDevice(PhysicalDevice physicalDevice)
    {
        if (physicalDevice.Handle == 0)
            throw new ArgumentException("A valid physical device is required.", nameof(physicalDevice));
        if (IsReady)
            throw new InvalidOperationException("Physical-device identity cannot change after logical-device creation.");

        PhysicalDevice = physicalDevice;
    }

    /// <summary>
    /// Publishes a newly created device before extension functions are loaded.
    /// Queue handles remain unavailable until <see cref="ResolveQueues"/>.
    /// </summary>
    public void AttachDevice(Device device, bool createdThroughOpenXr)
    {
        if (device.Handle == 0)
            throw new ArgumentException("A valid logical device is required.", nameof(device));
        if (IsReady)
            throw new InvalidOperationException("The Vulkan device context already owns a logical device.");

        Device = device;
        CreatedThroughOpenXr = createdThroughOpenXr;
    }

    /// <summary>
    /// Resolves every queue handle once after <c>vkCreateDevice</c>.
    /// </summary>
    public void ResolveQueues(
        Vk api,
        in VulkanRenderer.QueueFamilyIndices indices,
        bool supportsMultipleGraphicsQueues)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (!IsReady)
            throw new InvalidOperationException("A logical device must be attached before resolving queues.");

        uint graphicsFamily = indices.GraphicsFamilyIndex
            ?? throw new InvalidOperationException("A graphics queue family is required before logical-device creation.");
        uint presentFamily = indices.PresentFamilyIndex
            ?? throw new InvalidOperationException("A presentation queue family is required before logical-device creation.");
        uint computeFamily = indices.ComputeFamilyIndex ?? graphicsFamily;
        uint transferFamily = indices.TransferFamilyIndex ?? computeFamily;

        api.GetDeviceQueue(Device, graphicsFamily, 0, out Queue graphicsQueue);
        Queue secondaryGraphicsQueue = default;
        if (supportsMultipleGraphicsQueues)
            api.GetDeviceQueue(Device, graphicsFamily, 1, out secondaryGraphicsQueue);

        api.GetDeviceQueue(Device, presentFamily, 0, out Queue presentQueue);
        api.GetDeviceQueue(Device, computeFamily, 0, out Queue computeQueue);
        api.GetDeviceQueue(Device, transferFamily, 0, out Queue transferQueue);

        GraphicsFamily = graphicsFamily;
        PresentFamily = presentFamily;
        ComputeFamily = computeFamily;
        TransferFamily = transferFamily;
        SupportsMultipleGraphicsQueues = supportsMultipleGraphicsQueues;
        GraphicsQueue = graphicsQueue;
        SecondaryGraphicsQueue = secondaryGraphicsQueue;
        PresentQueue = presentQueue;
        ComputeQueue = computeQueue;
        TransferQueue = transferQueue;
    }

    public void LoadExtensionFunctions(
        Vk api,
        Instance instance,
        string[] enabledExtensions)
    {
        if (!IsReady)
            throw new InvalidOperationException("A logical device must be attached before loading extension functions.");

        HashSet<string> enabledExtensionSet = new(enabledExtensions, StringComparer.Ordinal);
        ExtensionFunctions.Load(api, instance, Device, enabledExtensionSet);
    }

    /// <summary>
    /// Destroys the owned logical device exactly once and clears all published
    /// handles as one lifecycle transition.
    /// </summary>
    public unsafe void Destroy(Vk api)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (!IsReady)
            return;

        api.DestroyDevice(Device, null);
        ExtensionFunctions.Clear();
        Device = default;
        PhysicalDevice = default;
        CreatedThroughOpenXr = false;
        SupportsMultipleGraphicsQueues = false;
        GraphicsFamily = 0;
        PresentFamily = 0;
        ComputeFamily = 0;
        TransferFamily = 0;
        GraphicsQueue = default;
        SecondaryGraphicsQueue = default;
        PresentQueue = default;
        ComputeQueue = default;
        TransferQueue = default;
    }
}
