namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct VulkanBindlessMaterialDescriptorBinding(
        VkRenderProgram Program,
        string Consumer);
}