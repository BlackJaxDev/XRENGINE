using System.Numerics;

namespace XREngine.Rendering.Materials;

/// <summary>
/// Renderer-neutral material row and its semantic texture sources.
/// </summary>
public readonly struct MaterialBindingSourceSnapshot(
    GPUMaterialEntry entry,
    XRTexture? albedo,
    XRTexture? normal,
    XRTexture? rm,
    XRTexture? emissive,
    Vector4 emissionColor,
    float emissionStrength,
    Vector4 emissionTextureMetadata,
    Vector4 emissionUvScaleOffset,
    float emissionUvRotation)
{
    public GPUMaterialEntry Entry { get; } = entry;
    public XRTexture? Albedo { get; } = albedo;
    public XRTexture? Normal { get; } = normal;
    public XRTexture? RM { get; } = rm;
    public XRTexture? Emissive { get; } = emissive;

    /// <summary>RGB is the authored factor; W marks explicit modern emission.</summary>
    public Vector4 EmissionColor { get; } = emissionColor;
    public float EmissionStrength { get; } = emissionStrength;
    /// <summary>X is the texture-coordinate set; Y selects manual sRGB decode.</summary>
    public Vector4 EmissionTextureMetadata { get; } = emissionTextureMetadata;
    public Vector4 EmissionUvScaleOffset { get; } = emissionUvScaleOffset;
    public float EmissionUvRotation { get; } = emissionUvRotation;
}
