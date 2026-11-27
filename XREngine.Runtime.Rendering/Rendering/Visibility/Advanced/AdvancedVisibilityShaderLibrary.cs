namespace XREngine.Rendering;

/// <summary>
/// Stable shader asset identities for visibility raster, coverage, meshlet, and diagnostics.
/// </summary>
public static class AdvancedVisibilityShaderLibrary
{
    public const string Root = "Advanced/Visibility";
    public const string Vertex = Root + "/VisibilityRaster.vert";
    public const string OpaqueFragment = Root + "/VisibilityRaster.frag";
    public const string MaskedFragment = Root + "/VisibilityRasterMasked.frag";
    public const string Mesh = Root + "/VisibilityRaster.mesh";
    /// <summary>
    /// Task stage in front of <see cref="Mesh"/>. Each task workgroup culls up to 32
    /// meshlets of one draw against the view frustum and their normal cones.
    /// </summary>
    public const string Task = Root + "/VisibilityRaster.task";
    /// <summary>Mesh surface decal fragment shader of the decal buffer pass.</summary>
    public const string DecalBufferFragment = Root + "/DecalBuffer.frag";
    /// <summary>Portal and mirror quad vertex shader for stencil carve and depth reset.</summary>
    public const string PortalQuadVertex = Root + "/PortalQuad.vert";
    /// <summary>Stencil-only fragment shader for carving a portal or mirror quad.</summary>
    public const string PortalStencilCarveFragment = Root + "/PortalStencilCarve.frag";
    /// <summary>Visibility and depth reset fragment shader for carved portal pixels.</summary>
    public const string PortalDepthResetFragment = Root + "/PortalDepthReset.frag";
    /// <summary>Depth-only fragment shader that restores primary depth on uncovered transparent-mirror pixels.</summary>
    public const string PortalTransparentDepthRestoreFragment = Root + "/PortalTransparentDepthRestore.frag";
    /// <summary>Fullscreen fragment shader that repopulates single-sample portal stencil bits after MSAA resolve.</summary>
    public const string PortalMsaaStencilRepopulateFragment = Root + "/PortalMsaaStencilRepopulate.frag";
    /// <summary>Fullscreen fragment shader that draws reflected sky/environment onto uncovered portal pixels during unwind.</summary>
    public const string PortalSkyUnwindFragment = Root + "/PortalSkyUnwind.frag";
    /// <summary>Vertex shader that draws the mirror glass composite quad during deepest-out portal unwind.</summary>
    public const string PortalGlassQuadVertex = Root + "/PortalGlassQuad.vert";
    /// <summary>Fragment shader that composites mirror glass tint, Fresnel, and specular during deepest-out portal unwind.</summary>
    public const string PortalGlassCompositeFragment = Root + "/PortalGlassComposite.frag";
    public const string DirectionalShadowVertex = Root + "/DirectionalShadowRaster.vert";
    public const string DirectionalShadowOpaqueFragment = Root + "/DirectionalShadowRaster.frag";
    public const string DirectionalShadowMaskedFragment = Root + "/DirectionalShadowRasterMasked.frag";
    public const string DebugCompute = Root + "/VisibilityDebug.comp";
    public const string ClearCompute = Root + "/ClearVisibility.comp";
    public const string ResetCountersCompute =
        Root + "/ResetVisibilityCounters.comp";
    public const string ResetRangesCompute =
        Root + "/ResetVisibilityRanges.comp";
    public const string ValidateCompute =
        Root + "/ValidateVisibility.comp";
    public const string DepthPyramidCompute =
        "Advanced/Preparation/BuildDepthPyramid.comp";
    public const string EarlyVisibilityCompute =
        "Advanced/Preparation/EarlyVisibility.comp";
    public const string PhysicsChainBoundsPatchCompute =
        "Advanced/Preparation/PatchPhysicsChainBounds.comp";
    public const string LateVisibilityCompute =
        "Advanced/Preparation/LateVisibility.comp";
    public const string BuildIndirectCompute =
        "Advanced/Preparation/BuildVisibilityIndirect.comp";
    public const string FinalizeIndexedInstanceGroupsCompute =
        "Advanced/Preparation/FinalizeIndexedInstanceGroups.comp";
    public const string CullDirectionalShadowCastersCompute =
        "Advanced/Preparation/CullDirectionalShadowCasters.comp";
}
