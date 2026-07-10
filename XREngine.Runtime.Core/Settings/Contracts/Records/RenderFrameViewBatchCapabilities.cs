namespace XREngine;

public readonly record struct RenderFrameViewBatchCapabilities(
    bool SupportsLayeredStereoPairs,
    bool SupportsLayeredQuadView,
    bool SupportsParallelCommandBufferRecording,
    bool SupportsMixedLayerExtents,
    int MaxLayerCount)
{
    public static RenderFrameViewBatchCapabilities None => new(false, false, false, false, 1);
    public static RenderFrameViewBatchCapabilities VulkanMultiviewStereoPairs => new(true, false, true, false, 2);
    public static RenderFrameViewBatchCapabilities VulkanMultiviewQuadView => new(true, true, true, false, 4);
}
