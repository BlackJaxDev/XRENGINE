namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanUpscaleBridgeFrameResources(
    int DisplayWidth,
    int DisplayHeight,
    int InternalWidth,
    int InternalHeight,
    bool OutputHdr,
    EAntiAliasingMode AntiAliasingMode,
    uint MsaaSampleCount,
    bool Stereo,
    bool EnableDlss,
    EDlssQualityMode DlssQuality,
    bool EnableXess,
    EXessQualityMode XessQuality,
    EVulkanUpscaleBridgeQueueModel QueueModel)
{
    public bool VendorRequested => EnableDlss || EnableXess;
}
