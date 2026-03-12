using System;

namespace XREngine.Rendering.VideoStreaming.Interfaces;

public interface IVideoFrameGpuActions : IDisposable
{
    bool UploadVideoFrame(DecodedVideoFrame frame, object? targetTexture, out string? error);
}
