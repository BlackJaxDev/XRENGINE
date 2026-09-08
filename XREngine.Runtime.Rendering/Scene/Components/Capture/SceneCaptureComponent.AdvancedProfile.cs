using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.Components.Lights;

public partial class SceneCaptureComponent
{
    private bool _useAdvancedCapturePipeline;

    /// <summary>
    /// Requests the capability-gated Advanced HDR capture family. Failure to bind
    /// this family defers the required capture; it never selects a legacy substitute.
    /// </summary>
    public bool UseAdvancedCapturePipeline
    {
        get => _useAdvancedCapturePipeline;
        set => SetField(ref _useAdvancedCapturePipeline, value);
    }

    protected virtual RenderPipelineOffscreenIntent AdvancedCaptureIntent
        => new(ERenderPipelineOffscreenViewIntent.SceneCapture,
            ERenderPipelineOffscreenOutput.HdrColor, EnableLateTransparency: true);

    private void ConfigureCapturePipeline(XRViewport viewport)
    {
        RenderPipelineRequest request = UseAdvancedCapturePipeline
            ? RenderPipelineRequest.AdvancedOffscreenCapture(AdvancedCaptureIntent)
            : RenderPipelineRequest.OffscreenCapture();
        // The shared viewport is serialized by the capture queue. A component's
        // resource refresh cannot reach here while its previous face writer is live.
        bool replace = viewport.PipelineRequest.OffscreenIntent != request.OffscreenIntent ||
            viewport.RenderPipeline is null;
        viewport.PipelineRequest = request;
        if (replace)
            viewport.RenderPipeline = RuntimeEngine.Rendering.NewRenderPipeline(viewport.PipelineRequest);

        if (UseAdvancedCapturePipeline && viewport.RenderPipeline is AdvancedRenderPipeline advanced)
        {
            // Capturing the probe's own published IBL would recursively accumulate
            // indirect light on every refresh. Capture direct/emissive radiance only.
            advanced.GlobalIlluminationMode = EGlobalIlluminationMode.None;
        }
    }
}
