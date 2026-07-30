namespace XREngine.Rendering;

/// <summary>
/// Determines whether a material can participate in the visibility pass and
/// which coverage/depth-changing specialization it requires.
/// </summary>
public static class AdvancedVisibilityShaderVariantContract
{
    public static bool IsSupported(
        EAdvancedMaterialCoverageMode coverage,
        EAdvancedVisibilityDisplacementMode displacement)
        => (coverage is
            EAdvancedMaterialCoverageMode.Opaque or
            EAdvancedMaterialCoverageMode.Masked) &&
           displacement == EAdvancedVisibilityDisplacementMode.None;

    public static bool SamplesCoverageTexture(
        EAdvancedMaterialCoverageMode coverage)
        => coverage == EAdvancedMaterialCoverageMode.Masked;

    public static bool ChangesRasterPosition(
        EAdvancedVisibilityDisplacementMode displacement)
        => displacement is
            EAdvancedVisibilityDisplacementMode.VertexDepthAffecting or
            EAdvancedVisibilityDisplacementMode.TessellatedDepthAffecting;
}
