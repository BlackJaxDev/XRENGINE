using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IOpenGlVendorUpscaleBackendCapability
{
    ulong IOpenGlVendorUpscaleBackendCapability.FrameIndex
        => unchecked((ulong)Math.Max(0L, _frameCounter));

    bool IOpenGlVendorUpscaleBackendCapability.TryGenerateFrameBuffer(
        XRFrameBuffer frameBuffer,
        out string failureReason)
    {
        if (GenericToAPI<GLFrameBuffer>(frameBuffer) is not GLFrameBuffer glFrameBuffer)
        {
            failureReason = $"Failed to create the OpenGL framebuffer wrapper for '{frameBuffer.Name ?? "<unnamed>"}'.";
            return false;
        }

        glFrameBuffer.Generate();
        failureReason = string.Empty;
        return true;
    }

    bool IOpenGlVendorUpscaleBackendCapability.TryExecuteVulkanBridge(
        VulkanUpscaleBridge bridge,
        XRFrameBuffer sourceColorFrameBuffer,
        XRFrameBuffer sourceDepthFrameBuffer,
        XRFrameBuffer sourceMotionFrameBuffer,
        XRFrameBuffer? sourceExposureFrameBuffer,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        out XRTexture? outputTexture,
        out TimeSpan dispatchDuration,
        out string failureReason)
        => bridge.TryExecuteVendorUpscale(
            this,
            sourceColorFrameBuffer,
            sourceDepthFrameBuffer,
            sourceMotionFrameBuffer,
            sourceExposureFrameBuffer,
            in parameters,
            out outputTexture,
            out dispatchDuration,
            out failureReason);
}
