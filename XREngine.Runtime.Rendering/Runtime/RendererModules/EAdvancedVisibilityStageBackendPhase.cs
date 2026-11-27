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
    /// <summary>
    /// Runs outside-in stencil carving, far-depth and visibility reset, single-phase GPU culling,
    /// and reflected visibility raster for scheduled in-place portal and mirror sub-views.
    /// Ordered after primary <see cref="LateRaster"/> and before <see cref="MultisampleResolve"/>
    /// and <see cref="DecalBuffer"/>. Skipped when no portal views are active.
    /// </summary>
    PortalVisibility,
    MultisampleResolve,
    /// <summary>
    /// Draws mesh surface decals into the decal buffer after the final visibility depth.
    /// Only backends that report <see cref="IAdvancedVisibilityStageBackendCapability.SupportsAdvancedSurfaceDecalBuffer"/> receive it.
    /// </summary>
    DecalBuffer,
    /// <summary>
    /// Draws reflected sky on uncovered far-depth portal pixels before opt-in reflected
    /// transparents execute in <see cref="EAdvancedRenderStage.LatePasses"/>.
    /// </summary>
    PortalSkyUnwind,
    /// <summary>
    /// Runs deepest-out portal unwind at the start of <see cref="EAdvancedRenderStage.LatePasses"/>:
    /// draws reflected sky on uncovered mirror pixels when not already drawn, composites mirror
    /// glass tint, Fresnel, and specular highlights, restores real mirror plane depth, and unwinds
    /// stencil bits 2..6 to 0.
    /// </summary>
    PortalUnwind,
}
