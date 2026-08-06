namespace XREngine.Rendering.Vulkan;

/// <summary>Owns bounded desktop/capture-output queues and capture readiness.</summary>
internal sealed class VulkanCaptureOutputState(int readbackSlotCount)
{
    internal VulkanScreenshotReadbackSlot?[] ScreenshotReadbackSlots { get; } =
        new VulkanScreenshotReadbackSlot?[readbackSlotCount];
    internal int ScreenshotReadbackCursor;
    internal long ScreenshotReadbackReservedRawBytes;
    internal long ScreenshotReadbackQueuedCount;
    internal long ScreenshotReadbackCompletedCount;
    internal long ScreenshotReadbackFailedCount;
    internal long ScreenshotReadbackRejectedCount;
    internal long ScreenshotReadbackTimeoutCount;
    internal bool ObsHookDeviceCaptureReady;
    internal string? ObsHookDeviceCaptureFailure;
}
