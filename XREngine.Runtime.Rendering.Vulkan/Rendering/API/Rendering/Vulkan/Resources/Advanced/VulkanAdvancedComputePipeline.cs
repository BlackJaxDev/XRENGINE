using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Exact compute program generation admitted before primary recording.</summary>
internal readonly record struct VulkanAdvancedComputePipeline(
    VkRenderProgram Program, Pipeline Pipeline, ulong LinkGeneration)
{
    internal bool IsCurrent => Program is { IsLinked: true } &&
        Program.LinkGeneration == LinkGeneration && Pipeline.Handle != 0;
}
