namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkRenderProgram
    {
        private readonly record struct ComputeUniformBufferKey(
            EComputeUniformBufferKind Kind,
            uint ImageIndex,
            uint Set,
            uint Binding,
            string Name);
    }
}
