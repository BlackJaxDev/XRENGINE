namespace XREngine.Rendering.Vulkan;

internal readonly record struct VulkanUpscaleBridgeFrameInfo(
    int SlotIndex,
    XRFrameBuffer SourceColorFrameBuffer,
    XRFrameBuffer SourceDepthFrameBuffer,
    XRFrameBuffer SourceMotionFrameBuffer,
    XRFrameBuffer ExposureFrameBuffer,
    XRFrameBuffer OutputColorFrameBuffer);
