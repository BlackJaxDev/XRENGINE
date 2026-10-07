namespace XREngine.Rendering.Compute;

/// <summary>Identifies the material state captured for one published draw.</summary>
public readonly record struct PhysicsChainDrawMaterialSnapshot(
    XRMaterial? Material,
    ulong BindingLayoutVersion,
    ulong BindingValueVersion,
    ulong BindingResourceVersion,
    long ShaderStateRevision,
    long UberStateRevision)
{
    /// <summary>Captures one published material reference and its revisions.</summary>
    public static PhysicsChainDrawMaterialSnapshot Capture(XRMaterial? material)
        => new(material, material?.BindingLayoutVersion ?? 0u,
            material?.BindingValueVersion ?? 0u,
            material?.BindingResourceVersion ?? 0u,
            material?.ShaderStateRevision ?? 0L,
            material?.UberStateRevision ?? 0L);

    /// <summary>True while the material still has the captured state.</summary>
    public bool IsCurrent => Material is null ||
        Material.BindingLayoutVersion == BindingLayoutVersion &&
        Material.BindingValueVersion == BindingValueVersion &&
        Material.BindingResourceVersion == BindingResourceVersion &&
        Material.ShaderStateRevision == ShaderStateRevision &&
        Material.UberStateRevision == UberStateRevision;
}
