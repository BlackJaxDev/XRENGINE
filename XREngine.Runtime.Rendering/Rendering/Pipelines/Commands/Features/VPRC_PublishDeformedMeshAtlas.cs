using XREngine.Rendering.Compute;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Publishes deformed meshes to the mesh atlas before the GPU indirect mesh passes. It runs inside
/// the pipeline so that its compute work belongs to the frame's resource generation.
/// </summary>
[RenderPipelineScriptCommand]
public class VPRC_PublishDeformedMeshAtlas : ViewportRenderCommand
{
    protected override void Execute()
    {
        if (DefaultRenderPipeline.DrawsFromMeshAtlas &&
            SkinningPrepassDispatcher.OwnsDeformation(
                ActivePipelineInstance.RenderState.StereoPass,
                ActivePipelineInstance.RenderState.ShadowPass) &&
            ActivePipelineInstance.RenderState.Scene is { } scene)
            SkinnedAtlasPublisher.Instance.Run(
                scene.GPUCommands,
                ActivePipelineInstance.RenderState.WindowViewport?.FrameViewHistorySnapshot);
    }
}
