using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Target-owned acquisition, completion, and recovery boundary used by
/// deterministic component and headless frame execution.
/// </summary>
internal interface IVulkanExplicitFrameTargetDriver
{
    RenderTargetOutputProperties OutputProperties { get; }
    ulong TargetGeneration { get; }
    bool IsDeviceLost { get; }
    double LastCompletedGpuFrameNanoseconds { get; }
    string PresentationDescription { get; }
    /// <summary>
    /// Returns the stable logical output for the next acquire without reserving
    /// an image, fence, semaphore, or WSI ownership. The returned token must be
    /// checked against the later lease before native recording.
    /// </summary>
    VulkanExplicitFrameTargetPreview PreviewNextFrameTarget();
    VulkanFrameTargetLease AcquireFrameTarget(out CommandBuffer commandBuffer);
    void BeginFrameRecording(in VulkanFrameTargetLease lease, CommandBuffer commandBuffer);
    void EndFrameRecording(in VulkanFrameTargetLease lease, CommandBuffer commandBuffer);
    void NotifyFrameSubmitted(in VulkanFrameTargetLease lease);
    void CompleteFrameTarget(in VulkanFrameTargetLease lease);
    void AbortFrameTarget(in VulkanFrameTargetLease lease, bool submissionAccepted);
    byte[] ReadbackLastSubmittedColor(int maxByteCount, ImageLayout sourceLayout);
    string ComputeLastSubmittedColorHash(ImageLayout sourceLayout);
}
