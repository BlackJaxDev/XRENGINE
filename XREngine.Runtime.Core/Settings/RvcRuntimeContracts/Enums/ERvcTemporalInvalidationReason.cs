namespace XREngine;

/// <summary>
/// Temporal invalidation reasons used by the engine.
/// </summary>
[Flags]
public enum ERvcTemporalInvalidationReason
{
    /// <summary>
    /// No temporal invalidation reason.
    /// </summary>
    None = 0,
    /// <summary>
    /// Material has changed.
    /// </summary>
    MaterialChanged = 1 << 0,
    /// <summary>
    /// Material resource generation has changed.
    /// </summary>
    MaterialResourceGenerationChanged = 1 << 1,
    /// <summary>
    /// Animation or deformation has changed.
    /// </summary>
    AnimationOrDeformationChanged = 1 << 2,
    /// <summary>
    /// Level of detail has changed.
    /// </summary>
    LodChanged = 1 << 3,
    /// <summary>
    /// Shadow caster set has changed.
    /// </summary>
    ShadowCasterSetChanged = 1 << 4,
    /// <summary>
    /// View set has changed.
    /// </summary>
    ViewSetChanged = 1 << 5,
    /// <summary>
    /// Gaze region has changed.
    /// </summary>
    GazeRegionChanged = 1 << 6,
    /// <summary>
    /// Topology has changed.
    /// </summary>
    TopologyChanged = 1 << 7,
}
