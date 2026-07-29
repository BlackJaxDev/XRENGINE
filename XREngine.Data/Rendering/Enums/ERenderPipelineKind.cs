namespace XREngine.Data.Rendering;

/// <summary>
/// Identifies the renderer architecture selected for a standard scene pipeline.
/// </summary>
public enum ERenderPipelineKind
{
    /// <summary>
    /// No pipeline may be created for the requested selection.
    /// </summary>
    None = 0,

    /// <summary>
    /// The retained default renderer used as the temporary reference and fallback.
    /// </summary>
    LegacyDefault,

    /// <summary>
    /// The advanced renderer migration surface.
    /// </summary>
    Advanced,
}
