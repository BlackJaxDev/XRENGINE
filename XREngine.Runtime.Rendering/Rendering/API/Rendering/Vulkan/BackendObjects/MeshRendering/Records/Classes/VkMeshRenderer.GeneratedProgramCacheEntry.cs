namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkMeshRenderer
    {
        private sealed class GeneratedProgramCacheEntry
        {
            public required XRRenderProgram Data { get; init; }
            public required VkRenderProgram Program { get; init; }
        }
    }
}