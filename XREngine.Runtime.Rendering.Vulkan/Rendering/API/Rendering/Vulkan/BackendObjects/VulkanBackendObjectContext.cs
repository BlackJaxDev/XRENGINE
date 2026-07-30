using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow device and identity services shared by backend wrappers from one
/// renderer generation.
/// </summary>
internal sealed class VulkanBackendObjectContext(
    VulkanDeviceContext deviceContext,
    VulkanBackendObjectRegistry registry,
    VulkanResourceLifetimeTracker lifetime,
    VulkanDescriptorManager descriptors,
    VulkanPipelineManager pipelines)
{
    public Device Device => deviceContext.Device;
    public PhysicalDevice PhysicalDevice => deviceContext.PhysicalDevice;
    public bool IsLogicalDeviceReady => deviceContext.IsReady;
    public VulkanBackendObjectRegistry Registry { get; } = registry;
    public VulkanBindingAllocator BindingAllocator => Registry.BindingAllocator;
    public VulkanResourceLifetimeTracker Lifetime { get; } = lifetime;
    public VulkanDescriptorManager Descriptors { get; } = descriptors;
    public VulkanPipelineManager Pipelines { get; } = pipelines;
}
