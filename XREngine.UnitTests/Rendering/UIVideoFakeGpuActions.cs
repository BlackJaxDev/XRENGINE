using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.UnitTests.Rendering;

internal sealed class UIVideoFakeGpuActions : IVideoFrameGpuActions
{
    public int DisposeCount { get; private set; }
    public bool UploadVideoFrame(DecodedVideoFrame frame, object? targetTexture, out string? error)
    {
        error = null;
        return true;
    }
    public void Dispose() => DisposeCount++;
}
