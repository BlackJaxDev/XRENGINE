namespace XREngine.Rendering;

/// <summary>
/// Stable cross-domain synchronization boundaries in the advanced desktop frame.
/// </summary>
public enum EAdvancedSynchronizationBoundary
{
    ComputePreparationToVisibilityRaster = 0,
    VisibilityRasterToComputeShading,
    ComputeShadingToLateGraphics,
    LateGraphicsToPresentation,
}
