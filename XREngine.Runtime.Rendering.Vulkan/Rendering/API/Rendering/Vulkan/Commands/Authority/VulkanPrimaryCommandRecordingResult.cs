using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal enum EVulkanPrimaryCommandRecordingDisposition : byte
{
    Recorded,
    Reused,
    ReplanRequired,
    Deferred,
}

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
    string? Reason)
{
    internal bool Succeeded => Disposition is EVulkanPrimaryCommandRecordingDisposition.Recorded or EVulkanPrimaryCommandRecordingDisposition.Reused;
    internal bool RequiresReplan => Disposition == EVulkanPrimaryCommandRecordingDisposition.ReplanRequired;

    internal static VulkanPrimaryCommandRecordingResult ReplanRequired(string reason)
        => new(EVulkanPrimaryCommandRecordingDisposition.ReplanRequired, default, default, 0, default, default,
            ImageLayout.Undefined, 0, 0, reason);

    internal static VulkanPrimaryCommandRecordingResult Deferred(string reason)
        => new(EVulkanPrimaryCommandRecordingDisposition.Deferred, default, default, 0, default, default,
            ImageLayout.Undefined, 0, 0, reason);
}
