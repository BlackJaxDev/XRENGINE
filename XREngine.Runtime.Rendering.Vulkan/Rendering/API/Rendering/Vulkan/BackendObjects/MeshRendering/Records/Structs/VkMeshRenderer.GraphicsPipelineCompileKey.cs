namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkMeshRenderer
    {
        internal readonly record struct GraphicsPipelineCompileKey(PipelineKey Pipeline);
    }
}