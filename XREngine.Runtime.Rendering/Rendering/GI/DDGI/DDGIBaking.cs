namespace XREngine.Rendering.GI.DDGI;

/// <summary>Explicit cold-path GPU capture of a viewport's converged DDGI history.</summary>
public static class DDGIBaking
{
    public static bool TryCaptureFromPipeline(XRRenderPipelineInstance pipeline, string name, out DDGIBakedAsset? asset, out string failure)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        asset = null;
        if (!RuntimeEngine.IsRenderThread)
        {
            failure = "DDGI baking must run on the render thread after the update submissions have completed.";
            return false;
        }
        DDGIFrameContext context = DDGIFrameContext.Get(pipeline);
        if (context.State.CameraScrolling)
        {
            failure = "Disable camera scrolling and let every cascade update before baking; baked assets store one stationary origin.";
            return false;
        }
        // A single cascade always traces visibility; the coarse override only
        // changes sampling when an outer cascade actually exists.
        if (context.State.DisableCoarseVisibility && context.State.CascadeCount > 1)
        {
            failure = "Enable visibility in every cascade before baking; baked assets do not store a coarse-visibility override.";
            return false;
        }
        if (!context.HasInitializedResources || context.State.IsInvalidated)
        {
            failure = "DDGI baking requires a completed update of every probe in every cascade.";
            return false;
        }
        XRViewport? viewport = pipeline.LastWindowViewport;
        if (viewport is null)
        {
            failure = "DDGI baking requires a viewport with an accepted render-pipeline submission.";
            return false;
        }
        // A cold-path capture must resolve the same accepted physical generation
        // as the rendered viewport, including its probe-buffer allocation.
        using IDisposable? plannerScope = viewport.EnterRenderPipelineReadbackScope(pipeline);
        XRTexture? irradiance = pipeline.GetTexture<XRTexture>(DDGIResourceNames.IrradianceAtlas);
        XRTexture? visibility = pipeline.GetTexture<XRTexture>(DDGIResourceNames.VisibilityAtlas);
        XRDataBuffer? probes = pipeline.GetBuffer(DDGIResourceNames.ProbeStateBuffer);
        if (irradiance is null || visibility is null || probes is null)
        {
            failure = "The viewport has no active DDGI GPU resources.";
            return false;
        }
        return DDGIBakedAsset.TryCaptureFromGpu(context.State, name, irradiance, visibility, probes, out asset, out failure);
    }
}
