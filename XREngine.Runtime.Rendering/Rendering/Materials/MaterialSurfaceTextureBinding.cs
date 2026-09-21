using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Materials;

/// <summary>
/// Immutable source-surface texture metadata. The texture reference identifies the binding; its
/// position in <see cref="XRMaterial.Textures"/> is deliberately not part of this contract.
/// </summary>
public sealed record MaterialSurfaceTextureBinding
{
    public EMaterialTextureSemantic Semantic { get; init; }
    public XRTexture Texture { get; init; }
    public int TexCoordSet { get; init; }
    public int Channel { get; init; }
    public bool IsSrgb { get; init; }
    public ETexWrapMode WrapU { get; init; }
    public ETexWrapMode WrapV { get; init; }
    public Vector4 UvScaleOffset { get; init; }
    public float UvRotation { get; init; }

    public MaterialSurfaceTextureBinding(
        EMaterialTextureSemantic Semantic,
        XRTexture Texture,
        int TexCoordSet = 0,
        int Channel = 0,
        bool IsSrgb = false,
        ETexWrapMode WrapU = ETexWrapMode.Repeat,
        ETexWrapMode WrapV = ETexWrapMode.Repeat,
        Vector4? UvScaleOffset = null,
        float UvRotation = 0.0f)
    {
        this.Semantic = Semantic;
        this.Texture = Texture;
        this.TexCoordSet = TexCoordSet;
        this.Channel = Channel;
        this.IsSrgb = IsSrgb;
        this.WrapU = WrapU;
        this.WrapV = WrapV;
        this.UvScaleOffset = UvScaleOffset ?? new Vector4(1.0f, 1.0f, 0.0f, 0.0f);
        this.UvRotation = UvRotation;
    }
}
