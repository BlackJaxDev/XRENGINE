namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct VulkanDescriptorReferencePair(
        VulkanResourceLifetimeKey First,
        VulkanResourceLifetimeKey Second);
}
