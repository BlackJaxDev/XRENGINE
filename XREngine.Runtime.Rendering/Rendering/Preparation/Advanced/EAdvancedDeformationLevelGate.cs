namespace XREngine.Rendering;

/// <summary>
/// The outcome of the deformation level choice for one skinned draw that gets the skinned
/// cluster cut. Each value names the first check in
/// <see cref="AdvancedPreparationExtractor"/> that kept the draw at level 0, or
/// <see cref="Chosen"/> when the draw got a coarser level. The deformation slot diagnostics
/// count the outcomes so that a session shows why deformation level of detail does or does not
/// reduce the deformed vertex count.
/// </summary>
public enum EAdvancedDeformationLevelGate
{
    /// <summary>Deformation level of detail is off, or the build has no usable view set.</summary>
    NoViews,
    /// <summary>The draw is transparent. The transparent pass reads the deformed vertices without the clamped cut.</summary>
    ExcludedTransparent,
    /// <summary>The draw writes fragment depth (parallax), so it uses the depth-writing masked raster.</summary>
    ExcludedFragmentDepth,
    /// <summary>The draw uses the mesh surface decal lane.</summary>
    ExcludedSurfaceDecal,
    /// <summary>The draw is hovered or selected in the editor and has a highlight stencil draw.</summary>
    ExcludedEditorHighlight,
    /// <summary>The draw uses the wireframe raster.</summary>
    ExcludedWireframe,
    /// <summary>The draw culls front faces or all faces. Double-sided draws do not stop here.</summary>
    ExcludedCullState,
    /// <summary>The draw uses only the CPU fallback path.</summary>
    ExcludedCpuFallback,
    /// <summary>The canonical geometry is not resident.</summary>
    GeometryNotResident,
    /// <summary>The canonical geometry has no meshlets, because the mesh has no meshlet payload.</summary>
    NoMeshlets,
    /// <summary>The canonical geometry has no cluster hierarchy.</summary>
    NoClusterHierarchy,
    /// <summary>The geometry has no meshlets above the draw range, so no coarser level exists.</summary>
    NoCoarserLevels,
    /// <summary>A meshlet buffer of the geometry is not valid.</summary>
    MeshletBuffersInvalid,
    /// <summary>The mesh payload has no cooked deformation order.</summary>
    NoDeformationOrder,
    /// <summary>The deformation order length differs from the mesh vertex count.</summary>
    DeformationOrderMismatch,
    /// <summary>The draw transform is not in the publication.</summary>
    NoTransform,
    /// <summary>The draw has GPU physics bounds that are not usable in this frame.</summary>
    PhysicsBoundsUnusable,
    /// <summary>The level selector chose level 0 for the draw distance.</summary>
    SelectedLevelZero,
    /// <summary>The selected level has no deformation prefix or no published level cover.</summary>
    NoPrefixOrCover,
    /// <summary>The draw got a level above 0.</summary>
    Chosen,
    /// <summary>The number of values. Not an outcome.</summary>
    Count,
}
