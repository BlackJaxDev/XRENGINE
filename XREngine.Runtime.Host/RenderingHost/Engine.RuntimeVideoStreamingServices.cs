using XREngine.Rendering;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine;

internal sealed class EngineRuntimeVideoStreamingServices : IRuntimeVideoStreamingServices
{
    public IVideoFrameGpuActions CreateVideoFrameGpuActions(object renderer)
        => renderer is IRuntimeRendererHost runtimeRenderer &&
           runtimeRenderer.TryGetBackendCapability<IVideoFrameGpuActionsBackendCapability>(out var capability) &&
           capability is not null
            ? capability.CreateVideoFrameGpuActions()
            : new NullVideoFrameGpuActions(renderer.GetType().Name);
}
