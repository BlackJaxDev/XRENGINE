namespace XREngine.Rendering;

/// <summary>
/// Pipeline-compatible visibility raster state independent of material instance.
/// </summary>
public readonly record struct AdvancedVisibilityRasterStateClass(
    uint StateClassId,
    bool FrontFaceCounterClockwise,
    bool DoubleSided,
    bool DepthBiasEnabled,
    bool ClippingEnabled,
    uint CullMode,
    EAdvancedVisibilityDisplacementMode DisplacementMode)
{
    public bool IsSupported
        => DisplacementMode == EAdvancedVisibilityDisplacementMode.None;
}
