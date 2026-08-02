namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanMeshFrameDataRendererFamilyKey(
    VkMeshRenderer Renderer,
    VulkanMeshFrameDataFamilyKey Family);