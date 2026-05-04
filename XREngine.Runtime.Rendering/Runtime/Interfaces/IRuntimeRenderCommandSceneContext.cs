namespace XREngine.Rendering;

/// <summary>
/// Scene-level render command callbacks used by GPU-driven render passes.
/// </summary>
public interface IRuntimeRenderCommandSceneContext
{
    /// <summary>
    /// Executes or records the backend GPU render pass against the active scene command context.
    /// </summary>
    void RenderGpuPass(IRuntimeGpuRenderPassHost gpuPass);

    /// <summary>
    /// Records GPU visibility telemetry for the active scene command collection.
    /// </summary>
    void RecordGpuVisibility(uint draws, uint instances);
}
