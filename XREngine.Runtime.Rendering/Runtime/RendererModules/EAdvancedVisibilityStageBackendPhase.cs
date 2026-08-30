namespace XREngine.Rendering;

/// <summary>
/// Physical backend phase used to lower one logical advanced visibility
/// stage into independently synchronized render-graph operations.
/// </summary>
public enum EAdvancedVisibilityStageBackendPhase
{
    Complete,
    LateCompute,
    LateRaster,
}
