namespace XREngine;

/// <summary>
/// Edge rejection flags used by the engine.
/// </summary>
[Flags]
public enum ERvcEdgeRejectionFlags
{
    /// <summary>
    /// No edge rejection.
    /// </summary>
    None = 0,
    /// <summary>
    /// Depth-based edge rejection.
    /// </summary>
    Depth = 1 << 0,
    /// <summary>
    /// Normal-based edge rejection.
    /// </summary>
    Normal = 1 << 1,
    /// <summary>
    /// Material-based edge rejection.
    /// </summary>
    Material = 1 << 2,
    /// <summary>
    /// Primitive-based edge rejection.
    /// </summary>
    Primitive = 1 << 3,
    /// <summary>
    /// Disocclusion-based edge rejection.
    /// </summary>
    Disocclusion = 1 << 4,
}
