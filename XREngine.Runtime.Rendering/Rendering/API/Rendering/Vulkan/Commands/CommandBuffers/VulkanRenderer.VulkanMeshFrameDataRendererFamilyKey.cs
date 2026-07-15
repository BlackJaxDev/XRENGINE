namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct VulkanMeshFrameDataRendererFamilyKey(
        VkMeshRenderer Renderer,
        VulkanMeshFrameDataFamilyKey Family);
}
