using XREngine.Rendering.OpenGL;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;
using XREngine.Rendering.Vulkan;

namespace XREngine;

internal sealed class EngineRuntimeVideoStreamingServices : IRuntimeVideoStreamingServices
{
    public IVideoFrameGpuActions CreateVideoFrameGpuActions(object renderer)
        => renderer switch
        {
            OpenGLRenderer => new OpenGLVideoFrameGpuActions(),
            VulkanRenderer => new VulkanVideoFrameGpuActions(),
            _ => new NullVideoFrameGpuActions(renderer.GetType().Name)
        };

    private sealed class NullVideoFrameGpuActions(string rendererName) : IVideoFrameGpuActions
    {
        public bool UploadVideoFrame(DecodedVideoFrame frame, object? targetTexture, out string? error)
        {
            error = $"Renderer '{rendererName}' cannot upload streaming video frames.";
            return false;
        }

        public void Dispose()
        {
        }
    }
}
