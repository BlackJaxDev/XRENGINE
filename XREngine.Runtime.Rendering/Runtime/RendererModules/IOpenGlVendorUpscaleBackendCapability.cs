using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering;

/// <summary>
/// Isolates the temporary OpenGL-to-Vulkan vendor-upscale bridge from stable pipeline code.
/// The bridge-specific parameter types move with the backend extraction in P4.8.
/// </summary>
internal interface IOpenGlVendorUpscaleBackendCapability
{
    ulong FrameIndex { get; }

    bool TryGenerateFrameBuffer(XRFrameBuffer frameBuffer, out string failureReason);

    bool TryExecuteVulkanBridge(
        VulkanUpscaleBridge bridge,
        XRFrameBuffer sourceColorFrameBuffer,
        XRFrameBuffer sourceDepthFrameBuffer,
        XRFrameBuffer sourceMotionFrameBuffer,
        XRFrameBuffer? sourceExposureFrameBuffer,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        out XRTexture? outputTexture,
        out TimeSpan dispatchDuration,
        out string failureReason);
}
