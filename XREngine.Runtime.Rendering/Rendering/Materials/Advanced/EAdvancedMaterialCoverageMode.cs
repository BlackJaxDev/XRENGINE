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
    /// <summary>
    /// Mesh surface decal. It never enters the visibility buffer, Hi-Z, or shadow lanes.
    /// The decal buffer pass draws it, and native shading blends it before lighting.
    /// </summary>
    Decal = 4,
}
