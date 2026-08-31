namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provenance for a completed coarse Vulkan command-buffer elapsed-time query.
/// Source-frame identity is retained separately from the live pending state.
/// </summary>
public readonly record struct VulkanGpuCommandBufferTimingSample(
    EVulkanGpuTimingAvailability Availability,
    ulong SourceRenderFrameId,
    ulong AgeFrames,
    ulong Sequence,
    int ImageSlot,
    ulong ElapsedNanoseconds)
{
    public bool IsCompleted => Availability == EVulkanGpuTimingAvailability.Completed;
}
