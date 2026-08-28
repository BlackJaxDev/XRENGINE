using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine;

/// <summary>Rejects GPU video-frame operations when no backend service is installed.</summary>
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
