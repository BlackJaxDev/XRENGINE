using XREngine.Rendering.WebGPU;

namespace XREngine.Browser;

internal sealed partial class BrowserEngineSession
{
    /// <summary>Enables optional managed render-callback measurements for the current renderer.</summary>
    public void ConfigureCanvasFrameStatistics(bool enabled, bool reset)
        => (_renderer ?? throw new InvalidOperationException("WebGPU.Statistics.RendererMissing: start an engine canvas first."))
            .ConfigureEngineFrameStatistics(enabled, reset);

    /// <summary>Captures a cold snapshot without formatting diagnostics during frame submission.</summary>
    public WebGpuEngineFrameStatistics CaptureCanvasFrameStatistics()
        => (_renderer ?? throw new InvalidOperationException("WebGPU.Statistics.RendererMissing: start an engine canvas first."))
            .CaptureEngineFrameStatistics();
}
