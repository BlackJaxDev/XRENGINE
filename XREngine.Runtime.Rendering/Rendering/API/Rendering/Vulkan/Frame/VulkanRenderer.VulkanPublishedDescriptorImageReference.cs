namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct VulkanPublishedDescriptorImageReference(
        uint Binding,
        uint Element,
        VulkanDescriptorImageReference Reference);
}
