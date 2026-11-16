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
    /// <summary>Linear red-channel intensity multiplying the authored specular factor.</summary>
    Specular = 7,
    /// <summary>
    /// Linear ambient occlusion. <see cref="MaterialSurfaceTextureBinding.Channel"/> selects the
    /// channel (glTF and ORM textures store it in red). It darkens only indirect light.
    /// </summary>
    Occlusion = 8,
}
