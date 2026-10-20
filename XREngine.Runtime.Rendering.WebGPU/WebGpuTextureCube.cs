using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials.Textures;

namespace XREngine.Rendering.WebGPU;

/// <summary>Preserves a six-face engine cubemap as one layered image with a cube sampled view.</summary>
public sealed class WebGpuTextureCube : WebGpuLayeredTexture<XRTextureCube>
{
    public WebGpuTextureCube(WebGpuRendererHost renderer, XRTextureCube data) : base(renderer, data)
        => data.Resized += Invalidate;

    protected override uint AuthoredWidth => Data.Extent;
    protected override uint AuthoredHeight => Data.Extent;
    protected override int AuthoredLayers => 6;
    protected override int AuthoredMips => Data.Mipmaps?.Length ?? 0;
    protected override int AuthoredSamples => 1;
    protected override ESizedInternalFormat AuthoredFormat => Data.SizedInternalFormat;
    protected override string SampledDimension => "cube";
    protected override ETexMinFilter MinFilter => Data.MinFilter;
    protected override ETexMagFilter MagFilter => Data.MagFilter;
    protected override ETexWrapMode UWrap => Data.UWrap;
    protected override ETexWrapMode VWrap => Data.VWrap;
    protected override ETexWrapMode WWrap => Data.WWrap;
    protected override float LodBias => Data.LodBias;
    protected override float MaxAnisotropy => 1;
    protected override bool EnableComparison => false;
    protected override ETextureCompareFunc CompareFunc => ETextureCompareFunc.LessOrEqual;

    protected override Mipmap2D GetAuthoredMip(int mip, int layer)
    {
        CubeMipmap? level = Data.Mipmaps[mip];
        if (level?.Sides is not { Length: 6 } || level.Sides[layer] is null)
            throw Unsupported("Create", "cubemaps require six separately authored faces at each mip level");
        return level.Sides[layer];
    }

    protected override void OnRetiring()
    {
        Data.Resized -= Invalidate;
        base.OnRetiring();
    }
}
