namespace XREngine.Rendering.Commands;

/// <summary>
/// Validation-only correspondence between one legacy resident command row and
/// its canonical renderer-neutral records. The mapping owns no identities.
/// </summary>
public readonly record struct LegacyCanonicalDrawMapping(
    uint LegacyCommandIndex,
    uint LegacyMeshId,
    uint LegacyMaterialId,
    uint LegacyRenderPass,
    int SourcePrimitiveIndex,
    AdvancedGpuHandle Draw,
    AdvancedGpuHandle Geometry,
    AdvancedGpuHandle Material,
    ulong DependencySignature);
