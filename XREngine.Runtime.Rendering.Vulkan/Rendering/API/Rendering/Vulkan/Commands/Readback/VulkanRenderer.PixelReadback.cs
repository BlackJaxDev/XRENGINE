using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Public/API translation boundary for pixel readback. Native command recording,
/// synchronization, mapping, conversion, and settlement live in the command runtime.
/// </summary>
public partial class VulkanRenderer
{
    internal bool TryReadDepthPixelDebug(
        XRFrameBuffer frameBuffer,
        int x,
        int y,
        out VulkanCommandRuntime.VulkanDepthReadbackDebugInfo info)
    {
        info = VulkanCommandRuntime.VulkanDepthReadbackDebugInfo.Failed(
            "No framebuffer supplied.",
            x,
            y);
        if (frameBuffer is null)
            return false;

        x = Math.Clamp(x, 0, Math.Max((int)frameBuffer.Width - 1, 0));
        y = Math.Clamp(y, 0, Math.Max((int)frameBuffer.Height - 1, 0));
        if (!TryResolveBlitImage(
                frameBuffer,
                OutputRuntime.Desktop.LastPresentedImageIndex,
                GetReadBufferMode(),
                wantColor: false,
                wantDepth: true,
                wantStencil: false,
                out BlitImageInfo depthSource,
                isSource: true))
        {
            info = VulkanCommandRuntime.VulkanDepthReadbackDebugInfo.Failed(
                "Could not resolve a depth attachment image for the framebuffer.",
                x,
                y);
            return false;
        }

        return _commandRuntime.TryReadDepthPixelDebug(depthSource, x, y, out info);
    }
}
