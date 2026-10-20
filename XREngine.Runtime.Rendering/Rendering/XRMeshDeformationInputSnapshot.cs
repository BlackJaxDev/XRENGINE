namespace XREngine.Rendering;

/// <summary>
/// Exact renderer/mesh input ownership for a canonical deformation producer.
/// Palette indices and active morph indices belong to the selected mesh, never
/// to another primitive rendered by the same renderer.
/// </summary>
internal readonly record struct XRMeshDeformationInputSnapshot(
    XRDataBuffer? Palette,
    XRDataBuffer? PreviousPalette,
    uint PaletteBase,
    uint PaletteCount,
    bool GpuOwnedPalette,
    XRDataBuffer? ActiveMorphs,
    uint ActiveMorphCount,
    ulong PoseVersion,
    ulong MorphVersion)
{
    /// <summary>GPU ownership selects individual rows in the renderer's local palette.</summary>
    public bool UsesLocalBoneOwnership { get; init; }
}
