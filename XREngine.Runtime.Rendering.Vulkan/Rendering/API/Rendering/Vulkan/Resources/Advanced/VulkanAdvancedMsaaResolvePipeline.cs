using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen fullscreen MSAA visibility resolve pipeline for one exact canonical
/// single-sample target.
/// </summary>
internal readonly record struct VulkanAdvancedMsaaResolvePipeline(
    VkRenderProgram Program,
    ulong ProgramLinkGeneration,
    Pipeline Pipeline,
    PipelineLayout PipelineLayout,
    VulkanAdvancedVisibilityTargetClosure TargetClosure)
{
    internal bool IsValid
        => Program is { IsLinked: true } && ProgramLinkGeneration != 0UL &&
           Pipeline.Handle != 0UL && PipelineLayout.Handle != 0UL &&
           TargetClosure.IsValid &&
           TargetClosure.RasterizationSamples == SampleCountFlags.Count1Bit;
}
