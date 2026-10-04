using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Preserves an XRTexture2DArray as one layered GPU image with exact child mip identity.</summary>
public sealed class WebGpuTexture2DArray : WebGpuLayeredTexture<XRTexture2DArray>
{
    private bool _copyInFrame;
    private uint _frameCopySequence;
    private int _copyDestination;
    private int[] _copyCommands = [];
    private int[] _copySources = [];

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
    protected override bool WantsAutomaticMipmaps => Data.AutoGenerateMipmaps ||
        Data.Textures is { Length: > 0 } && Data.Textures[0].AutoGenerateMipmaps;
    protected override ETexMinFilter MinFilter => Data is ForwardLightProbeTextureArray probes ? probes.SamplingMinFilter : Data.MinFilter;
    protected override ETexMagFilter MagFilter => Data is ForwardLightProbeTextureArray probes ? probes.SamplingMagFilter : Data.MagFilter;
    protected override ETexWrapMode UWrap => Data.UWrap;
    protected override ETexWrapMode VWrap => Data.VWrap;
    protected override ETexWrapMode WWrap => ETexWrapMode.ClampToEdge;
    protected override float LodBias => Data.LodBias;
    protected override float MaxAnisotropy => Data.Textures[0].MaxAnisotropy;
    protected override bool EnableComparison => Data.EnableComparison;
    protected override ETextureCompareFunc CompareFunc => Data.CompareFunc;

    internal bool HasFixedLinearBaseMipSampling => MinFilter == ETexMinFilter.Linear && MagFilter == ETexMagFilter.Linear &&
        MaxAnisotropy == 1 && !EnableComparison && Data.MinLOD <= 0 && Data.MaxLOD >= 0;

    public override void Generate()
    {
        // An aborted producer never initialized its candidate array. Rebuild that
        // generation and record fresh copies only after its sources become usable.
        if (_frameCopySequence != 0 && !HasCommittedProduction &&
            (!Renderer.IsRecordingEngineFrame || _frameCopySequence != Renderer.EngineFrameSequence))
        {
            Invalidate();
            SetField(ref _frameCopySequence, 0u, publishNotifications: false);
        }
        base.Generate();
    }

    protected override void OnContentPrepared()
    {
        SetField(ref _frameCopySequence, _copyInFrame ? Renderer.EngineFrameSequence : 0u, publishNotifications: false);
        if (_copyInFrame) MarkProduced();
    }

    protected override Mipmap2D GetAuthoredMip(int mip, int layer)
    {
        XRTexture2D? texture = Data.Textures[layer];
        if (texture is null || texture.Mipmaps is null ||
            texture.Mipmaps.Length != AuthoredMips ||
            texture.SizedInternalFormat != Data.SizedInternalFormat ||
            texture.MultiSampleCount != 1)
            throw Unsupported("Create", "each array layer requires a matching single-sample 2D source and complete mip chain");
        return texture.Mipmaps[mip];
    }

    protected override void ValidateSources()
    {
        SetField(ref _copyInFrame, false, publishNotifications: false);
        if (!Data.AutoGenerateMipmaps)
            foreach (XRTexture2D source in Data.Textures)
                if (source.AutoGenerateMipmaps != WantsAutomaticMipmaps)
                    throw Unsupported("Create", "array slices must agree on automatic mip generation when the array does not request it");
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
            if (Renderer.GetOrCreateAPIRenderObject(source) is not WebGpuTexture2D api)
                throw Unsupported("Copy", "a GPU source has no physical wrapper");
            api.Generate();
            if (!api.IsCurrentGpuAllocationForCopy || api.ResourceHandle == 0 || api.SampleCount != 1 || api.Format != Format)
                throw Unsupported("Copy", "a GPU source is missing or obsolete");
            bool producedNow = Renderer.IsRecordingEngineFrame && api.WasProducedInFrame(Renderer.EngineFrameSequence);
            if (api.HasUncommittedProduction && !producedNow)
                throw new WebGpuResourcePreparationPendingException("WebGPU.Texture.CopyProducerPending: a source from an incomplete frame must be produced again before its array copy.");
            if (Renderer.IsRecordingEngineFrame && api.WasRecordedInFrame(Renderer.EngineFrameSequence))
                SetField(ref _copyInFrame, true, publishNotifications: false);
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
        if (_copyInFrame && (_copyDestination != handle || _copyCommands.Length != layers * mips))
        {
            ReleaseCopyCommands();
            _copyDestination = handle;
            _copyCommands = new int[layers * mips];
            _copySources = new int[layers];
        }
        for (int layer = 0; layer < layers; layer++)
        {
            WebGpuTexture2D source = (WebGpuTexture2D)Renderer.GetOrCreateAPIRenderObject(Data.Textures[layer])!;
            if (_copyInFrame && _copySources[layer] != source.ResourceHandle)
            {
                for (int mip = 0; mip < mips; mip++)
                {
                    Renderer.RetireEngineResourceAfterFrame(_copyCommands[layer * mips + mip]);
                    _copyCommands[layer * mips + mip] = 0;
                }
                _copySources[layer] = source.ResourceHandle;
            }
            for (int mip = 0; mip < mips; mip++)
            {
                Mipmap2D level = GetAuthoredMip(mip, layer);
                if (_copyInFrame)
                {
                    int index = layer * mips + mip;
                    if (_copyCommands[index] == 0)
                        _copyCommands[index] = Renderer.PrepareEngineTextureCopy(this, source.ResourceHandle, handle, mip, mip, layer,
                            checked((int)level.Width), checked((int)level.Height));
                }
                else
                    Renderer.StageEngineTextureSubresourceCopy(source.ResourceHandle, handle, mip, mip, layer,
                        checked((int)level.Width), checked((int)level.Height));
            }
        }
        if (_copyInFrame)
        {
            foreach (int command in _copyCommands) Renderer.RecordEngineCommands(command, []);
            foreach (int source in _copySources) Renderer.MarkEngineTextureRecorded(source);
            Renderer.MarkEngineTextureRecorded(handle);
            ReleaseCopyCommands();
        }
    }

    private void ReleaseCopyCommands()
    {
        foreach (int command in _copyCommands) Renderer.RetireEngineResourceAfterFrame(command);
        Array.Clear(_copyCommands);
        _copyDestination = 0;
    }

    public override void Destroy()
    {
        ReleaseCopyCommands();
        base.Destroy();
    }

    protected override void OnRetiring()
    {
        Data.Resized -= Invalidate;
        base.OnRetiring();
    }
}
