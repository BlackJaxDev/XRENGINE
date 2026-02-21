using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.VideoStreaming.Interfaces;

public interface IVideoFrameGpuActions : IDisposable
{
    bool TryPrepareOutput(XRMaterialFrameBuffer frameBuffer, XRMaterial? material, out uint framebufferId, out string? error);
    bool UploadVideoFrame(DecodedVideoFrame frame, XRTexture2D? targetTexture, out string? error);
    void Present(IMediaStreamSession session, uint framebufferId);
}
