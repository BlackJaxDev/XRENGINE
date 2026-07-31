using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free description of one acquired final-output target. Phase 4
/// threads this value through the common frame loop.
/// </summary>
internal readonly record struct VulkanFrameTargetLease(
    VulkanRenderFrameTarget Target,
    Format ColorFormat,
    Format DepthFormat,
    SampleCountFlags Samples,
    uint ImageIndex,
    Result AcquireResult,
    Semaphore SubmissionWaitSemaphore,
    PipelineStageFlags SubmissionWaitStage,
    Semaphore SubmissionSignalSemaphore,
    Fence CompletionFence,
    VulkanFrameTargetCompletionKind CompletionKind,
    bool ImagesExternallyOwned,
    uint ViewIndex,
    bool SupportsHiddenAreaMask)
{
    public bool IsValid =>
        Target.ColorImage.Handle != 0 &&
        Target.ColorView.Handle != 0 &&
        Target.Extent.Width != 0 &&
        Target.Extent.Height != 0;
}
