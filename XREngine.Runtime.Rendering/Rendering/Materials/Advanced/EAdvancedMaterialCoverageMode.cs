namespace XREngine.Rendering;

/// <summary>
/// Declares how a material contributes raster coverage. The value is authored
/// metadata and must never be inferred from a shader file name.
/// </summary>
public enum EAdvancedMaterialCoverageMode : uint
{
    Opaque = 0,
    Masked = 1,
    Transparent = 2,
    Refractive = 3,
}
