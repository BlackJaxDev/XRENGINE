namespace XREngine.Rendering;

/// <summary>
/// Provides a backend-specific lifetime scope for synchronous pipeline-resource inspection.
/// </summary>
public interface IRenderPipelineReadbackBackendCapability
{
    IDisposable EnterPipelineResourcePlannerReadbackScope(
        XRRenderPipelineInstance pipeline,
        XRViewport viewport);
}
