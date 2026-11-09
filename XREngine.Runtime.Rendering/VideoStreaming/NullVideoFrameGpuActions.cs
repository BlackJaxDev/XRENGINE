using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine;

/// <summary>Rejects GPU video-frame operations when no backend service is installed.</summary>
internal sealed class NullVideoFrameGpuActions(string rendererName) : IVideoFrameGpuActions
{
    // Build the error once: the caller tries an upload for every decoded frame.
    private readonly string _error = $"Renderer '{rendererName}' cannot upload streaming video frames.";

    public bool UploadVideoFrame(DecodedVideoFrame frame, object? targetTexture, out string? error)
    {
        error = _error;
        return false;
    }

    public void Dispose()
    {
    }
}
