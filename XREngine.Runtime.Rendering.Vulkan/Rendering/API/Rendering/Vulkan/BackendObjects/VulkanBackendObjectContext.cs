using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Narrow device and identity services shared by backend wrappers from one
/// renderer generation.
/// </summary>
internal sealed class VulkanBackendObjectContext(
    VulkanDeviceContext deviceContext,
    VulkanBackendObjectRegistry registry)
{
    public Device Device => deviceContext.Device;
    public PhysicalDevice PhysicalDevice => deviceContext.PhysicalDevice;
    public bool IsLogicalDeviceReady => deviceContext.IsReady;
    public VulkanBackendObjectRegistry Registry { get; } = registry;
    public VulkanBindingAllocator BindingAllocator => Registry.BindingAllocator;
}
