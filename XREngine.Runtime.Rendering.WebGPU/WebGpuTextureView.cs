using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Retains exact authored mip/layer/aspect views without allocating or copying another texture.</summary>
public sealed class WebGpuTextureView : WebGpuObject<XRTextureViewBase>, IWebGpuProducedTexture
{
    private readonly record struct ViewKey(int Texture, int Mip, int Mips, int Layer, int Layers, string Format, string Aspect, string Dimension);
    private readonly record struct SamplerState(string U, string V, string Min, string Mag, string Mip, float Minimum, float Maximum);
    private readonly Dictionary<(int Mip, int Layer), int> _renderViews = [];
    private ViewKey _key;
    private WebGpuTextureResource _source;
    private int _view;
    private WebGpuResourceRequest? _viewRequest;
    private WebGpuResourceRequest? _samplerRequest;
    private uint _lastRecordedFrame;
    private int _sampler;
    private SamplerState _samplerState;

    public WebGpuTextureView(WebGpuRendererHost renderer, XRTextureViewBase data) : base(renderer, data) { }
    public override bool IsGenerated => _view != 0 && _viewRequest is null;
    public override nint GetHandle() => _view;
    public string Format => _key.Format;
    public uint Width => Math.Max(1u, _source.Width >> checked((int)Data.MinLevel));
    public uint Height => Math.Max(1u, _source.Height >> checked((int)Data.MinLevel));
    public uint SampleCount => _source.Samples;
    internal WebGpuTextureResource Resource => new(this, _source.Handle, Width, Height, _key.Mips, _key.Layers,
        SampleCount, Format, _source.Storage, _key.Mip, _key.Layer, _key.Aspect);

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (IsRetired || Data.IsDestroyed) throw Unsupported("a retired texture view cannot regenerate");
        XRTexture source = Data.GetViewedTexture();
        // Shared materialization already checks ranges. Validate again here for direct authored resources.
        if (source is XRTextureViewBase) throw Unsupported("nested texture views require an explicit physical source");
        WebGpuTextureResource physical = WebGpuTextureResource.Resolve(Renderer, source);
        string format = WebGpuTextureFormat.Map(Data.InternalFormat);
        if (format != physical.Format) throw Unsupported("texture view format reinterpretation is not admitted");
        if (Data.NumLevels == 0 || Data.MinLevel >= physical.Mips || Data.NumLevels > physical.Mips - Data.MinLevel ||
            Data.NumLayers == 0 || Data.MinLayer >= physical.Layers || Data.NumLayers > physical.Layers - Data.MinLayer)
            throw Unsupported("the authored mip or layer range is outside the physical texture");
        string dimension = Data.TextureTarget switch
        {
            ETextureTarget.Texture2D or ETextureTarget.Texture2DMultisample => "2d",
            ETextureTarget.Texture2DArray => "2d-array",
            ETextureTarget.TextureCubeMap => "cube",
            _ => throw Unsupported("the authored view target has no exact WebGPU encoding"),
        };
        bool multisample = Data is XRTexture2DView view2D ? view2D.Multisample :
            Data is XRTexture2DArrayView array ? array.Multisample : false;
        if (multisample != (physical.Samples > 1)) throw Unsupported("the authored view multisample flag must match its source");
        EDepthStencilFmt selected = Data switch
        {
            XRTexture2DView view => view.DepthStencilViewFormat,
            XRTexture2DArrayView view => view.DepthStencilViewFormat,
            _ => EDepthStencilFmt.None,
        };
        string aspect = selected switch
        {
            EDepthStencilFmt.None => "all", EDepthStencilFmt.Depth => "depth-only", EDepthStencilFmt.Stencil => "stencil-only",
            _ => throw Unsupported("the authored depth/stencil aspect is invalid"),
        };
        if (selected == EDepthStencilFmt.Depth && !WebGpuTextureFormat.IsDepth(format) ||
            selected == EDepthStencilFmt.Stencil && !WebGpuTextureFormat.HasStencil(format))
            throw Unsupported("the authored view aspect is absent from its source");
        ViewKey key = new(physical.Handle, checked((int)Data.MinLevel), checked((int)Data.NumLevels),
            checked((int)Data.MinLayer), checked((int)Data.NumLayers), format, aspect, dimension);
        if (_view != 0 && key == _key)
        {
            if (_viewRequest is { } obsolete) Renderer.CancelEngineResourceRequest(obsolete);
            _viewRequest = null;
            return;
        }
        if (_view != 0 && Renderer.IsRecordingEngineFrame && _lastRecordedFrame == Renderer.EngineFrameSequence)
            throw Unsupported("view ranges cannot change after a dependent pass was recorded");
        int candidate = Renderer.CreateEngineReplacement(this, ref _viewRequest, 3, new BrowserTextureViewDescription(physical.Handle, key.Mip,
            key.Mips, key.Aspect, Data.Name ?? "Engine texture view", key.Layer, key.Layers, key.Dimension));
        ReleaseAllocation();
        SetField(ref _source, physical, publishNotifications: false);
        SetField(ref _key, key, publishNotifications: false);
        SetField(ref _view, candidate);
    }

    internal int GetSampledView() { Generate(); return _view; }

    public int GetRenderView(int mip, int layer)
    {
        Generate();
        int selectedLayer = layer == -1 && _key.Layers == 1 ? 0 : layer;
        if (_key.Aspect != "all" || mip < 0 || mip >= _key.Mips || selectedLayer < 0 || selectedLayer >= _key.Layers)
            throw Unsupported("render views must select one contained mip/layer and expose every texture aspect");
        if (_key.Mips == 1 && _key.Layers == 1 && _key.Dimension == "2d") return _view;
        if (_renderViews.TryGetValue((mip, selectedLayer), out int handle)) return handle;
        if (_renderViews.Count >= 192) throw Unsupported("the view exceeds 192 retained render subresources");
        handle = Renderer.CreateEngineTextureView(this, new BrowserTextureViewDescription(_source.Handle, _key.Mip + mip,
            1, "all", Data.Name ?? "Engine render texture view", _key.Layer + selectedLayer));
        _renderViews.Add((mip, selectedLayer), handle);
        return handle;
    }

    internal int GetSampler(bool comparison)
    {
        Generate();
        if (comparison || Data.LodBias != 0)
            throw Unsupported("comparison or nonzero LOD-bias sampling is not declared by this texture view");
        (string min, string mip, bool mapped) = Data.MinFilter switch
        {
            ETexMinFilter.Nearest => ("nearest", "nearest", false), ETexMinFilter.Linear => ("linear", "nearest", false),
            ETexMinFilter.NearestMipmapNearest => ("nearest", "nearest", true), ETexMinFilter.LinearMipmapNearest => ("linear", "nearest", true),
            ETexMinFilter.NearestMipmapLinear => ("nearest", "linear", true), ETexMinFilter.LinearMipmapLinear => ("linear", "linear", true),
            _ => throw Unsupported("invalid view minification filter"),
        };
        string mag = Data.MagFilter switch { ETexMagFilter.Nearest => "nearest", ETexMagFilter.Linear => "linear", _ => throw Unsupported("invalid view magnification filter") };
        float minimum = mapped ? Math.Max(0, Data.MinLOD) : 0, maximum = mapped ? Math.Min(32, Data.MaxLOD) : 0;
        if (minimum > maximum || minimum > 32 || maximum < 0 || !mapped && (Data.MinLOD > 0 || Data.MaxLOD < 0))
            throw Unsupported("the authored view LOD range is invalid");
        SamplerState state = new(Address(Data.UWrap), Address(Data.VWrap), min, mag, mip, minimum, maximum);
        if (_sampler != 0 && state == _samplerState)
        {
            if (_samplerRequest is { } obsolete) Renderer.CancelEngineResourceRequest(obsolete);
            _samplerRequest = null;
            return _sampler;
        }
        int candidate = Renderer.CreateEngineReplacement(this, ref _samplerRequest, 4, new BrowserSamplerDescription(state.U, state.V, min, mag, mip,
            Data.Name ?? "Engine view sampler", maximum, LodMinClamp: minimum));
        if (_sampler != 0)
        {
            Renderer.ReleaseEngineDrawDependencies(this);
            Renderer.RetireEngineResourceAfterFrame(_sampler);
        }
        SetField(ref _sampler, candidate);
        SetField(ref _samplerState, state, publishNotifications: false);
        return candidate;
    }

    internal bool DependsOn(AbstractRenderAPIObject resource) => ReferenceEquals(resource, _source.Owner);
    internal void MarkRecorded()
    {
        SetField(ref _lastRecordedFrame, Renderer.EngineFrameSequence, publishNotifications: false);
        WebGpuTextureResource.MarkRecorded(_source.Owner);
    }
    private IWebGpuProducedTexture SourceProduction => (IWebGpuProducedTexture)_source.Owner;
    public void MarkProduced()
    {
        MarkRecorded();
        SourceProduction.MarkProduced();
    }
    internal void MarkSubresourceProduced(int mip, int layer)
    {
        int selectedLayer = layer == -1 && _key.Layers == 1 ? 0 : layer;
        if (mip < 0 || mip >= _key.Mips || selectedLayer < 0 || selectedLayer >= _key.Layers)
            throw Unsupported("the produced subresource must belong to the retained view range");
        MarkRecorded();
        int sourceMip = _key.Mip + mip, sourceLayer = _key.Layer + selectedLayer;
        switch (_source.Owner)
        {
            case WebGpuTexture2DArray array: array.MarkSubresourceProduced(sourceMip, sourceLayer); break;
            case WebGpuTextureCube cube: cube.MarkSubresourceProduced(sourceMip, sourceLayer); break;
            default: SourceProduction.MarkProduced(); break;
        }
    }
    public void CommitProducedFrame(uint frameSequence) => SourceProduction.CommitProducedFrame(frameSequence);
    public bool WasProducedInFrame(uint frameSequence) => SourceProduction.WasProducedInFrame(frameSequence);
    public bool HasCommittedProduction => IsGenerated && SourceProduction.HasCommittedProduction;

    public override void Destroy()
    {
        Renderer.CancelEngineResourceRequests(this);
        _viewRequest = _samplerRequest = null;
        ReleaseAllocation();
    }

    private void ReleaseAllocation()
    {
        if (_view == 0 && _sampler == 0) return;
        Renderer.ReleaseEngineDrawDependencies(this);
        foreach (int view in _renderViews.Values) Renderer.RetireEngineResourceAfterFrame(view);
        _renderViews.Clear();
        if (_view != 0) Renderer.RetireEngineResourceAfterFrame(_view);
        if (_sampler != 0) Renderer.RetireEngineResourceAfterFrame(_sampler);
        SetField(ref _view, 0);
        SetField(ref _lastRecordedFrame, 0u);
        SetField(ref _sampler, 0);
    }

    private static string Address(ETexWrapMode value) => value switch
    {
        ETexWrapMode.Repeat => "repeat", ETexWrapMode.MirroredRepeat => "mirror-repeat", ETexWrapMode.ClampToEdge => "clamp-to-edge",
        _ => throw Unsupported("the authored view wrap mode has no exact WebGPU encoding"),
    };
    private static NotSupportedException Unsupported(string reason) => new($"WebGPU.TextureView.OperationUnsupported: {reason}.");
}
