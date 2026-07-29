namespace XREngine.Rendering;

/// <summary>
/// Coarse render state shared by many material rows.
/// </summary>
public enum EAdvancedMaterialRenderStateClass : uint
{
    Invalid = 0,
    OpaqueSingleSided = 1,
    OpaqueDoubleSided = 2,
    MaskedSingleSided = 3,
    MaskedDoubleSided = 4,
    Transparent = 5,
    Refractive = 6,
}
