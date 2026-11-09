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
    MultisampleResolve,
    /// <summary>
    /// Draws mesh surface decals into the decal buffer after the final visibility depth.
    /// Only backends that report <see cref="IAdvancedVisibilityStageBackendCapability.SupportsAdvancedSurfaceDecalBuffer"/> receive it.
    /// </summary>
    DecalBuffer,
}
