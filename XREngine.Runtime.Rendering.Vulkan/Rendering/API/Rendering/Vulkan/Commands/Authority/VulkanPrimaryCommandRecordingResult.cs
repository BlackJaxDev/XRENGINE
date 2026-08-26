using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Allocation-free result returned by a frozen primary recording attempt.</summary>
internal readonly record struct VulkanPrimaryCommandRecordingResult(
    EVulkanPrimaryCommandRecordingDisposition Disposition,
    CommandBuffer CommandBuffer,
    CommandBuffer DynamicUiSecondaryCommandBuffer,
    int DynamicUiOverlayOperationCount,
    CommandBuffer TextureUploadCommandBuffer,
    CommandPool TextureUploadCommandPool,
    ImageLayout SwapchainLayoutAfterCommandBuffer,
    int RecordedSwapchainWriteCount,
    long CommandBufferDirtyGeneration,
    string? Reason,
    FramePlan? OutputExecutionPlan = null,
    ERenderOutputReadinessPolicy ReadinessPolicy = ERenderOutputReadinessPolicy.AllowDeferral,
    ERenderOutputWorkClass WorkClass = ERenderOutputWorkClass.Background,
    ulong SourceFrameId = 0UL)
{
    internal bool Succeeded => Disposition is EVulkanPrimaryCommandRecordingDisposition.Recorded or
        EVulkanPrimaryCommandRecordingDisposition.RecordedWithGpuFallback or
        EVulkanPrimaryCommandRecordingDisposition.Reused;
    internal bool UsedGpuFallback => Disposition == EVulkanPrimaryCommandRecordingDisposition.RecordedWithGpuFallback;
    internal bool IsPresentNowFailure => Disposition == EVulkanPrimaryCommandRecordingDisposition.Failed &&
        WorkClass == ERenderOutputWorkClass.PresentNow;
    internal bool RequiresReplan => Disposition == EVulkanPrimaryCommandRecordingDisposition.ReplanRequired;

    internal static VulkanPrimaryCommandRecordingResult ReplanRequired(string reason)
        => new(EVulkanPrimaryCommandRecordingDisposition.ReplanRequired, default, default, 0, default, default,
            ImageLayout.Undefined, 0, 0, reason);

    internal static VulkanPrimaryCommandRecordingResult Deferred(string reason)
        => new(EVulkanPrimaryCommandRecordingDisposition.Deferred, default, default, 0, default, default,
            ImageLayout.Undefined, 0, 0, reason);

    internal static VulkanPrimaryCommandRecordingResult Failed(string reason,
        ERenderOutputReadinessPolicy readinessPolicy = ERenderOutputReadinessPolicy.BlockForExact,
        ERenderOutputWorkClass workClass = ERenderOutputWorkClass.PresentNow,
        ulong sourceFrameId = 0UL)
        => new(EVulkanPrimaryCommandRecordingDisposition.Failed, default, default, 0, default, default,
            ImageLayout.Undefined, 0, 0, reason, null, readinessPolicy, workClass, sourceFrameId);
}
