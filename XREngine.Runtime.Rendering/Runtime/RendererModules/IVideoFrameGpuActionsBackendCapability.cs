using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering;

/// <summary>
/// Creates the video-frame uploader owned by a concrete renderer backend.
/// </summary>
public interface IVideoFrameGpuActionsBackendCapability
{
    IVideoFrameGpuActions CreateVideoFrameGpuActions();
}
