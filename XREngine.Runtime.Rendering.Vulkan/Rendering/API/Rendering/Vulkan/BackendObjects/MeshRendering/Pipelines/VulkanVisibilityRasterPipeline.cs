using System.Collections.Generic;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen native visibility-raster pipeline closure for one mesh topology and
/// exact render target. The ordinary material program is never retained here.
/// </summary>
internal readonly record struct VulkanVisibilityRasterPipeline(
    VkRenderProgram Program,
    ulong ProgramLinkGeneration,
    Pipeline Pipeline,
    PipelineLayout PipelineLayout,
    PrimitiveTopology Topology,
    bool IsMeshShaderPipeline,
    VulkanVisibilityVertexInputSnapshot VertexInput,
    VulkanAdvancedVisibilityTargetClosure TargetClosure)
{
    internal bool IsValid
        => Program is { IsLinked: true } && ProgramLinkGeneration != 0UL &&
           Pipeline.Handle != 0UL && PipelineLayout.Handle != 0UL &&
           (IsMeshShaderPipeline || VertexInput.IsValid) && TargetClosure.IsValid;
}
