namespace XREngine.Rendering;

/// <summary>
/// Ordered cross-domain boundaries inside the visibility-buffer sequence.
/// </summary>
public enum EAdvancedVisibilitySynchronizationBoundary
{
    PreparationToEarlyRaster = 0,
    EarlyRasterToDepthPyramid,
    DepthPyramidToLatePreparation,
    LatePreparationToLateRaster,
    LateRasterToConsumers,
}
