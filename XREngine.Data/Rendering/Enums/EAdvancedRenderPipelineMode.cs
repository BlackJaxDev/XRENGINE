namespace XREngine.Data.Rendering;

/// <summary>
/// Controls how the engine evaluates and selects the advanced render pipeline.
/// </summary>
public enum EAdvancedRenderPipelineMode
{
    /// <summary>
    /// Keeps the legacy default pipeline active and skips advanced capability evaluation.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Selects the advanced pipeline when its required capabilities are available.
    /// Otherwise, reports the rejection and keeps the legacy default pipeline active.
    /// </summary>
    Available,

    /// <summary>
    /// Requires the advanced pipeline. Missing capabilities reject pipeline creation.
    /// </summary>
    Required,

    /// <summary>
    /// Evaluates and reports advanced capabilities without selecting the advanced pipeline.
    /// </summary>
    Diagnostic,
}
