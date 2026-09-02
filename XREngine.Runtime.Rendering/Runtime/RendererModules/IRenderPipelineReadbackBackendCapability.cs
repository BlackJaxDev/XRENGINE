namespace XREngine.Rendering;

/// <summary>
/// Provides a backend-specific lifetime scope for synchronous pipeline-resource inspection.
/// </summary>
public interface IRenderPipelineReadbackBackendCapability
{
    bool TryEnterPipelineResourcePlannerReadbackScope(
        XRRenderPipelineInstance pipeline,
        XRViewport viewport,
        out IDisposable? scope);

    IDisposable EnterPipelineResourcePlannerReadbackScope(
        XRRenderPipelineInstance pipeline,
        XRViewport viewport);
}
