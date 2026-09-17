namespace XREngine.Rendering;

/// <summary>
/// Resolves the effective camera for settings evaluation, with fallback to the rendering camera,
/// active execution state, or retained pipeline cameras during delayed/offscreen passes.
/// </summary>
public static class RenderPipelineCameraResolver
{
    public static XRCamera? ResolveCurrentSettingsCamera(XRRenderPipelineInstance? pipeline = null)
    {
        pipeline ??= RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        IRuntimeRenderCommandExecutionState? activeState = RuntimeEngine.Rendering.State.ActiveRenderCommandExecutionState;
        if (pipeline is not null)
        {
            return pipeline.RenderState.SceneCamera
                ?? RuntimeEngine.Rendering.State.RenderingCamera
                ?? pipeline.RenderState.RenderingCamera
                ?? (activeState?.SceneCamera as XRCamera)
                ?? (activeState?.RenderingCamera as XRCamera)
                ?? pipeline.LastSceneCamera
                ?? pipeline.LastRenderingCamera;
        }

        return RuntimeEngine.Rendering.State.RenderingPipelineState?.SceneCamera
            ?? (activeState?.SceneCamera as XRCamera)
            ?? (activeState?.RenderingCamera as XRCamera)
            ?? RuntimeEngine.Rendering.State.RenderingCamera;
    }
}
