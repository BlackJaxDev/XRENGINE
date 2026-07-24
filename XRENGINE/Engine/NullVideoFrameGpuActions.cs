using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine;

internal sealed class NullVideoFrameGpuActions(string rendererName) : IVideoFrameGpuActions
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
