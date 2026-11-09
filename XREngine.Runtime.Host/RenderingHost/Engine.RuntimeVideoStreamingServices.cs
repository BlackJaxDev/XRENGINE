using XREngine.Rendering;
using XREngine.Rendering.VideoStreaming;
using XREngine.Rendering.VideoStreaming.Interfaces;

namespace XREngine;

internal sealed class EngineRuntimeVideoStreamingServices : IRuntimeVideoStreamingServices
{
    /// <summary>
    /// Creates the frame uploader of the renderer's backend. A renderer without
    /// <see cref="IVideoFrameGpuActionsBackendCapability"/> gets an uploader that
    /// rejects every frame, and a warning names the renderer in every build.
    /// </summary>
    public IVideoFrameGpuActions CreateVideoFrameGpuActions(object renderer)
    {
        if (renderer is IRuntimeRendererHost runtimeRenderer &&
            runtimeRenderer.TryGetBackendCapability<IVideoFrameGpuActionsBackendCapability>(out var capability) &&
            capability is not null)
            return capability.CreateVideoFrameGpuActions();

        string rendererName = renderer.GetType().Name;
        VideoStreamingDiagnostics.Warning(
            $"Renderer '{rendererName}' does not provide {nameof(IVideoFrameGpuActionsBackendCapability)}. " +
            "Streaming video frames will not display.");
        return new NullVideoFrameGpuActions(rendererName);
    }
}
