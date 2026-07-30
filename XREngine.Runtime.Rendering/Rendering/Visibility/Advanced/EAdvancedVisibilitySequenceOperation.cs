namespace XREngine.Rendering;

/// <summary>
/// Ordered operations that produce one authoritative visibility/depth result.
/// </summary>
public enum EAdvancedVisibilitySequenceOperation
{
    ResetCounters = 0,
    ClearTargets,
    PrepareEarlyVisibility,
    ResetEarlyArgumentCounts,
    BuildEarlyArguments,
    RasterEarlyVisibility,
    BuildCurrentDepthPyramid,
    PrepareLateVisibility,
    ResetLateArgumentCounts,
    BuildLateArguments,
    RasterLateVisibility,
    ValidateFinalTargets,
    PublishFinalTargets,
}
