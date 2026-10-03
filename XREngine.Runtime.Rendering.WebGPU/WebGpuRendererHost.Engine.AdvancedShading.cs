namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private readonly Dictionary<XRRenderPipelineInstance, WebGpuAdvancedShadingOutput> _advancedShadingOutputs = [];

    private bool TryEnqueueAdvancedShading(in AdvancedVisibilityStageBackendRequest request,
        XRRenderPipelineInstance instance, out string reason)
    {
        if (!TryGetAdvancedVisibilityFrame(instance, in request, out WebGpuAdvancedVisibilityFrame? frame, out reason) || frame is null)
            return false;
        if (!_advancedShadingOutputs.TryGetValue(instance, out WebGpuAdvancedShadingOutput? output))
        {
            output = new(this);
            _advancedShadingOutputs.Add(instance, output);
        }
        return request.Stage == EAdvancedRenderStage.WorkClassification
            ? output.TryClassify(in request, instance, frame, out reason)
            : output.TryShade(in request, instance, frame, out reason);
    }

    private bool TryEnqueueAdvancedDepth(in AdvancedVisibilityStageBackendRequest request,
        XRRenderPipelineInstance instance, out string reason)
    {
        if (!TryGetAdvancedVisibilityFrame(instance, in request, out WebGpuAdvancedVisibilityFrame? frame, out reason) || frame is null)
            return false;
        // This conservative producer places every frustum candidate in the
        // early stream. There is no deferred list for the late raster phase.
        if (request.Phase == EAdvancedVisibilityStageBackendPhase.LateRaster)
        {
            reason = string.Empty;
            return true;
        }
        if (request.Phase != EAdvancedVisibilityStageBackendPhase.LateCompute ||
            !instance.Resources.TryGetTexture(request.DepthTargetName, out XRTexture? source) || source is not XRTexture2D depth ||
            !instance.Resources.TryGetTexture(request.CurrentDepthPyramidTargetName, out XRTexture? destination) || destination is not XRTexture2D pyramid)
        {
            reason = "WebGPU.Advanced.DepthResourcesMissing: conservative depth reduction requires its exact single-sample depth and coarse R32F output.";
            return false;
        }
        WebGpuRenderProgram program = GetAdvancedStageApi(instance.Pipeline!, "advanced::depth-pyramid");
        bool reversed = (frame.View.Flags & EAdvancedViewRecordFlags.ReversedDepth) != 0 || frame.View.DepthParams.W != 0;
        ERendererComputeEnqueueStatus status = TryBuildAdvancedDepthPyramid(program.Data, depth, pyramid, reversed);
        reason = status == ERendererComputeEnqueueStatus.Enqueued ? string.Empty
            : $"WebGPU.Advanced.DepthPyramid.{status}: the complete native frame remains unsubmitted.";
        return status == ERendererComputeEnqueueStatus.Enqueued;
    }

    private void DestroyAdvancedShadingOutputs()
    {
        foreach (WebGpuAdvancedShadingOutput output in _advancedShadingOutputs.Values) output.Dispose();
        _advancedShadingOutputs.Clear();
    }
}
