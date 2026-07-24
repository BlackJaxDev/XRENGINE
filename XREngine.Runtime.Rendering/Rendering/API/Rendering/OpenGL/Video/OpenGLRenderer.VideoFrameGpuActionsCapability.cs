using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer : IVideoFrameGpuActionsBackendCapability
{
    IVideoFrameGpuActions IVideoFrameGpuActionsBackendCapability.CreateVideoFrameGpuActions()
        => new OpenGLVideoFrameGpuActions(new OpenGLVideoFrameUploadContext(this));
}
