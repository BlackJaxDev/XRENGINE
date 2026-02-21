using System;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.VideoStreaming.Interfaces;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.VideoStreaming;

internal static class VideoFrameGpuActionsFactory
{
    public static IVideoFrameGpuActions Create(AbstractRenderer renderer)
        => renderer switch
        {
            OpenGLRenderer => new OpenGLVideoFrameGpuActions(),
            VulkanRenderer => new VulkanVideoFrameGpuActions(),
            _ => new NullVideoFrameGpuActions(renderer.GetType().Name)
        };

    private sealed class NullVideoFrameGpuActions(string rendererName) : IVideoFrameGpuActions
    {
        public bool TryPrepareOutput(XRMaterialFrameBuffer frameBuffer, XRMaterial? material, out uint framebufferId, out string? error)
        {
            framebufferId = 0;
            error = $"Renderer '{rendererName}' does not have a video GPU actions adapter.";
            return false;
        }

        public bool UploadVideoFrame(DecodedVideoFrame frame, XRTexture2D? targetTexture, out string? error)
        {
            error = $"Renderer '{rendererName}' cannot upload streaming video frames.";
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
}
