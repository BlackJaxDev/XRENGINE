namespace XREngine.Rendering.Vulkan.RenderGraph;

internal readonly record struct VulkanPhysicalPlanningResult(
    bool Updated,
    int AliasReuseCount,
    int RetiredImageCount,
    int RetiredBufferCount)
{
    internal static VulkanPhysicalPlanningResult Deferred => default;
}
