using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable structural identity captured with a prepared mesh operation.  It
/// intentionally excludes per-frame transform and view data, which are patched
/// from the current raw request on a cohort hit.
/// </summary>
internal readonly record struct VulkanPreparedMeshOperationCohortEntry(
    bool IsReusable,
    VkMeshRenderer Renderer,
    int PassIndex,
    XRRenderPipelineInstance? Pipeline,
    XRMaterial Material,
    XRMaterial? MaterialOverride,
    RenderingParameters? RenderOptionsOverride,
    ulong PreparationCompatibilitySignature,
    bool ForceNoStereo,
    XRFrameBuffer? Target,
    Extent2D TargetExtent,
    Viewport Viewport,
    Rect2D Scissor,
    IndexedViewportScissorSnapshot IndexedViewportScissors,
    VulkanFixedFunctionStateSnapshot FixedFunctionState,
    int PipelineIdentity,
    int ViewportIdentity,
    int OutputTargetIdentity,
    int OutputFrameBufferIdentity,
    ulong RecordingFingerprint,
    ulong ResourceGeneration,
    ulong DescriptorGeneration,
    uint InternalWidth,
    uint InternalHeight,
    bool StereoEnabled,
    bool MultiviewEnabled,
    ulong MaterialBindingLayoutVersion,
    ulong MaterialBindingValueVersion,
    ulong MaterialBindingResourceVersion,
    long MaterialShaderStateRevision,
    long MaterialUberStateRevision);
