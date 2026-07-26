namespace XREngine.Rendering;

/// <summary>
/// Semantic role used to choose a safe fallback sample when a material texture
/// is absent.
/// </summary>
public enum EUberSamplerRole
{
    Color,
    Normal,
    MaskWhite,
    MaskBlack,
    DataZero,
    HeightNeutral,
    EmissionBlack,
}
