namespace XREngine.Rendering;

/// <summary>
/// Copies shared visual-feature configuration between pipelines with different output topologies.
/// </summary>
public static class RenderPipelineFeatureSynchronizer
{
    /// <summary>
    /// Copies pipeline-level scene feature choices without sharing pipeline instances or resources.
    /// </summary>
    public static void CopyPipelineFeatures(
        RenderPipeline sourcePipeline,
        RenderPipeline destinationPipeline)
    {
        ArgumentNullException.ThrowIfNull(sourcePipeline);
        ArgumentNullException.ThrowIfNull(destinationPipeline);

        destinationPipeline.IsShadowPass = sourcePipeline.IsShadowPass;

        if (sourcePipeline is not ISceneRenderPipelineFeatureProvider source ||
            destinationPipeline is not ISceneRenderPipelineFeatureProvider destination)
        {
            return;
        }

        destination.GlobalIlluminationMode = source.GlobalIlluminationMode;
        destination.ForwardDepthPrePassEnabled = source.ForwardDepthPrePassEnabled;
        destination.ForwardPrePassSharesGBufferTargets =
            source.ForwardPrePassSharesGBufferTargets;
        destination.ForwardDepthNormalPrePassResolution =
            source.ForwardDepthNormalPrePassResolution;
    }

    /// <summary>
    /// Copies matching post-process stage values between pipeline-specific camera states.
    /// Unknown stages remain at the destination pipeline's defaults.
    /// </summary>
    public static bool TryCopyCameraPostProcessState(
        RenderPipeline sourcePipeline,
        RenderPipeline destinationPipeline,
        XRCamera sourceCamera,
        XRCamera destinationCamera)
    {
        ArgumentNullException.ThrowIfNull(sourcePipeline);
        ArgumentNullException.ThrowIfNull(destinationPipeline);
        ArgumentNullException.ThrowIfNull(sourceCamera);
        ArgumentNullException.ThrowIfNull(destinationCamera);

        try
        {
            var sourceState = sourceCamera.PostProcessStates.GetOrCreateState(sourcePipeline);
            var destinationState =
                destinationCamera.PostProcessStates.GetOrCreateState(destinationPipeline);

            foreach (var (stageKey, sourceStage) in sourceState.Stages)
            {
                if (destinationState.GetStage(stageKey) is not { } destinationStage)
                    continue;

                foreach (var (parameter, value) in sourceStage.Values)
                    destinationStage.SetValue<object?>(parameter, value);
            }

            destinationCamera.PostProcessMaterial = sourceCamera.PostProcessMaterial;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
