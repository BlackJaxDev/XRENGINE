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

    /// <summary>
    /// Projects the native Vulkan lease into the portable render-pipeline
    /// output contract. Native handles and synchronization remain private to
    /// the Vulkan frame loop.
    /// </summary>
    public RenderFrameOutputDescription ToOutputDescription(
        RenderExecutionMode executionMode,
        in RenderTargetOutputProperties properties)
    {
        if (Target.Extent.Width != properties.Width ||
            Target.Extent.Height != properties.Height ||
            Target.Layers != properties.Layers ||
            (uint)Samples != properties.SampleCount)
        {
            throw new InvalidOperationException(
                $"Vulkan frame lease does not match its portable output contract. " +
                $"Lease={Target.Extent.Width}x{Target.Extent.Height}x{Target.Layers}/{Samples}; " +
                $"Output={properties.Width}x{properties.Height}x{properties.Layers}/{properties.SampleCount}.");
        }

        RenderFrameOutputCapabilities capabilities = CompletionKind switch
        {
            VulkanFrameTargetCompletionKind.WsiPresent => RenderFrameOutputCapabilities.Presentation,
            VulkanFrameTargetCompletionKind.OpenXrRuntimeRelease =>
                RenderFrameOutputCapabilities.Presentation |
                RenderFrameOutputCapabilities.ExternallyOwnedImages,
            _ => RenderFrameOutputCapabilities.None,
        };
        if (ImagesExternallyOwned)
            capabilities |= RenderFrameOutputCapabilities.ExternallyOwnedImages;
        if (SupportsHiddenAreaMask)
            capabilities |= RenderFrameOutputCapabilities.HiddenAreaMask;

        return new RenderFrameOutputDescription(
            executionMode,
            properties,
            Target.TargetGeneration,
            Target.FrameSlotIndex,
            ViewIndex,
            capabilities);
    }
}
