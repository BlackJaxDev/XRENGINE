namespace XREngine.Rendering.RenderGraph.Diagnostics;

/// <summary>
/// A renderer that publishes the render graph plan it executes, so tools can show the backend order, batches, and
/// barriers.
/// </summary>
public interface IRenderGraphPlanDiagnosticsSource
{
    /// <summary>
    /// Returns the newest published plan for <paramref name="instance"/>, or null when the renderer has not published
    /// one. The call must not allocate when the plan did not change since the last call.
    /// </summary>
    RenderGraphPlanSnapshot? GetPlanSnapshot(XRRenderPipelineInstance? instance);
}
