using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// One immutable bootstrap query of physical-device core features, properties,
/// queue families, and advertised extensions.
/// </summary>
internal sealed class VulkanPhysicalDeviceCapabilitySnapshot
{
    public VulkanPhysicalDeviceCapabilitySnapshot(
        PhysicalDeviceFeatures coreFeatures,
        PhysicalDeviceProperties properties,
        QueueFamilyProperties[] queueFamilies,
        VulkanDeviceExtensionSet availableExtensions)
    {
        CoreFeatures = coreFeatures;
        Properties = properties;
        QueueFamilies = queueFamilies;
        QueueFamilyArray = queueFamilies;
        AvailableExtensions = availableExtensions;
    }

    public PhysicalDeviceFeatures CoreFeatures { get; }
    public PhysicalDeviceProperties Properties { get; }
    public IReadOnlyList<QueueFamilyProperties> QueueFamilies { get; }
    internal QueueFamilyProperties[] QueueFamilyArray { get; }
    public VulkanDeviceExtensionSet AvailableExtensions { get; }
}
