namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkMeshRenderer
    {
        private sealed class GeneratedProgramCacheEntry
        {
            public required string Identity { get; init; }
            public required XRRenderProgram Data { get; init; }
            public required VkRenderProgram Program { get; init; }
        }
    }
}
