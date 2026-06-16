namespace XREngine.Rendering;

/// <summary>
/// Debug metadata exposed by the active pipeline without depending on the concrete pipeline type.
/// </summary>
public interface IRuntimeRenderPipelineDebugContext
{
    /// <summary>
    /// Gets the short human-readable name for the active pipeline or diagnostic context.
    /// </summary>
    string DebugName { get; }

    /// <summary>
    /// Gets a fuller descriptor used in render logs and presentation diagnostics.
    /// </summary>
    string DebugDescriptor { get; }

    /// <summary>
    /// True when the context belongs to a shadow-map render pipeline.
    /// </summary>
    bool IsShadowPipeline { get; }
}
