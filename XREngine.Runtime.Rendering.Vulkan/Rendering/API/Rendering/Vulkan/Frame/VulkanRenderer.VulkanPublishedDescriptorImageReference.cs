namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct VulkanPublishedDescriptorImageReference(
        uint Binding,
        uint Element,
        VulkanDescriptorImageReference Reference);
}
