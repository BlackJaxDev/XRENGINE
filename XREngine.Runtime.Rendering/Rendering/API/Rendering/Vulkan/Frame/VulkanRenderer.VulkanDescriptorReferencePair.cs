namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct VulkanDescriptorReferencePair(
        VulkanResourceLifetimeKey First,
        VulkanResourceLifetimeKey Second);
}
