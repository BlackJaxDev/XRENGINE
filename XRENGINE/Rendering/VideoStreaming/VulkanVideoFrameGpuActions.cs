using System.Linq;
using XREngine.Data.Rendering;
using XREngine.Rendering.VideoStreaming.Interfaces;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.VideoStreaming;

internal sealed class VulkanVideoFrameGpuActions : IVideoFrameGpuActions
{
    public bool TryPrepareOutput(XRMaterialFrameBuffer frameBuffer, XRMaterial? material, out uint framebufferId, out string? error)
    {
        error = null;
        framebufferId = 0;

        frameBuffer.Material = material;
        frameBuffer.Generate();

        if (frameBuffer.APIWrappers.OfType<VulkanRenderer.VkFrameBuffer>().FirstOrDefault() is not VulkanRenderer.VkFrameBuffer vkFbo)
        {
            error = "Unable to locate Vulkan framebuffer wrapper for streaming video output.";
            return false;
        }

        vkFbo.Generate();
        return true;
    }

    public bool UploadVideoFrame(DecodedVideoFrame frame, XRTexture2D? targetTexture, out string? error)
    {
        error = "Vulkan video upload path is not implemented yet.";
        return false;
    }

    public void Present(IMediaStreamSession session, uint framebufferId)
    {
        session.SetTargetFramebuffer(framebufferId);
        session.Present();
    }

    public void Dispose()
    {
    }
}
