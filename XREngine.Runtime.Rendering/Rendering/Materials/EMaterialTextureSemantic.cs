namespace XREngine.Rendering.Materials;

/// <summary>
/// Describes the surface role of a material texture independently of its legacy shader sampler slot.
/// </summary>
public enum EMaterialTextureSemantic : byte
{
    BaseColor,
    Opacity,
    Normal,
    Metallic,
    Roughness,
    Emissive,
    Transmission,
}
