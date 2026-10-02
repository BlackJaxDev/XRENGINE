using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Preserves an XRTexture2DArray as one layered GPU image with exact child mip identity.</summary>
public sealed class WebGpuTexture2DArray : WebGpuLayeredTexture<XRTexture2DArray>
{
    public WebGpuTexture2DArray(WebGpuRendererHost renderer, XRTexture2DArray data) : base(renderer, data)
        => data.Resized += Invalidate;

    protected override uint AuthoredWidth => Data.Width;
    protected override uint AuthoredHeight => Data.Height;
    protected override int AuthoredLayers => Data.Textures?.Length ?? 0;
    protected override int AuthoredMips => Data.Mipmaps?.Length ?? 0;
    protected override int AuthoredSamples => Data.MultiSample ? 4 : 1;
    protected override ESizedInternalFormat AuthoredFormat => Data.SizedInternalFormat;
    protected override string SampledDimension => "2d-array";
    protected override bool UsesGpuSources => Data.CopyGpuLayerSources;
    protected override ETexMinFilter MinFilter => Data.MinFilter;
    protected override ETexMagFilter MagFilter => Data.MagFilter;
    protected override ETexWrapMode UWrap => Data.UWrap;
    protected override ETexWrapMode VWrap => Data.VWrap;
    protected override ETexWrapMode WWrap => ETexWrapMode.ClampToEdge;
    protected override float LodBias => Data.LodBias;
    protected override float MaxAnisotropy => Data.Textures[0].MaxAnisotropy;
    protected override bool EnableComparison => Data.EnableComparison;
    protected override ETextureCompareFunc CompareFunc => Data.CompareFunc;

    protected override Mipmap2D GetAuthoredMip(int mip, int layer)
    {
        XRTexture2D? texture = Data.Textures[layer];
        if (texture is null || texture.AutoGenerateMipmaps || texture.Mipmaps is null ||
            texture.Mipmaps.Length != AuthoredMips ||
            texture.SizedInternalFormat != Data.SizedInternalFormat ||
            texture.MultiSampleCount != 1)
            throw Unsupported("Create", "each array layer requires a matching single-sample 2D source and complete mip chain");
        return texture.Mipmaps[mip];
    }

    protected override void ValidateSources()
    {
        if (!Data.CopyGpuLayerSources) return;
        if (Data.MultiSample || Data.AutoGenerateMipmaps)
            throw Unsupported("Copy", "GPU layer copies require single-sample, authored source mips");
        for (int layer = 0; layer < Data.Textures.Length; layer++)
        {
            XRTexture2D? source = Data.Textures[layer];
            if (source is null || source.IsDestroyed || source.AutoGenerateMipmaps ||
                source.Width != Width || source.Height != Height ||
                source.SizedInternalFormat != Data.SizedInternalFormat ||
                source.Mipmaps.Length != AuthoredMips)
                throw Unsupported("Copy", "each GPU source must be a live matching 2D image with complete authored mips");
            if (Renderer.GetOrCreateAPIRenderObject(source) is not WebGpuTexture2D api || !api.IsCurrentGpuAllocationForCopy ||
                api.ResourceHandle == 0 || api.SampleCount != 1 || api.Format != Format ||
                Renderer.IsRecordingEngineFrame && api.WasRecordedInFrame(Renderer.EngineFrameSequence))
                throw Unsupported("Copy", "a GPU source is missing, obsolete, or recorded later in the current frame");
        }
        if (Format is not ("r8unorm" or "rgba8unorm" or "rgba8unorm-srgb" or "rgba16float"))
            throw Unsupported("Copy", "GPU layer copies require an exact color format with copy usages");
    }

    protected override void UploadContent(int handle, int layers, int mips, ESizedInternalFormat format)
    {
        if (!Data.CopyGpuLayerSources)
        {
            base.UploadContent(handle, layers, mips, format);
            return;
        }
        for (int layer = 0; layer < layers; layer++)
        {
            WebGpuTexture2D source = (WebGpuTexture2D)Renderer.GetOrCreateAPIRenderObject(Data.Textures[layer])!;
            for (int mip = 0; mip < mips; mip++)
            {
                Mipmap2D level = GetAuthoredMip(mip, layer);
                Renderer.CopyTextureSubresource(source.ResourceHandle, handle, mip, mip, layer,
                    checked((int)level.Width), checked((int)level.Height));
            }
        }
    }

    protected override void OnRetiring()
    {
        Data.Resized -= Invalidate;
        base.OnRetiring();
    }
}
