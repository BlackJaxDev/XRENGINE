namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkRenderProgram
    {
        private enum EComputeUniformBufferKind : byte
        {
            Auto,
            Fallback
        }
    }
}