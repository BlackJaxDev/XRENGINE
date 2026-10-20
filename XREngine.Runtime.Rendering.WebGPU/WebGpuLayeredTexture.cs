using System.Buffers;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Owns one engine array or cube image and its generation-scoped subresource views.</summary>
public abstract unsafe class WebGpuLayeredTexture<T> : WebGpuObject<T>, IWebGpuProducedTexture where T : XRTexture
{
    private readonly record struct SampledViewKey(int BaseMip, int MipCount, bool Depth);
    private readonly record struct SamplerState(string U, string V, string W, string Min, string Mag,
        string Mip, float MinLod, float MaxLod, int Anisotropy, string? Compare);
    private struct BaseLayerProduction
    {
        internal ulong Ticket;
        internal ulong CommittedTicket;
        internal uint Frame;
    }

    private readonly Dictionary<(int Mip, int Layer), int> _renderViews = [];
    private readonly Dictionary<SampledViewKey, int> _sampledViews = [];
    private SamplerState _samplerState;
    private int _samplerHandle;
    private WebGpuResourceRequest? _samplerRequest;
    private int _handle;
    private WebGpuResourceRequest? _allocationRequest;
    private uint _width;
    private uint _height;
    private int _layers;
    private int _mips;
    private int _samples;
    private ESizedInternalFormat _format;
    private bool _invalidated = true;
    private bool _storage;
    private uint _lastRecordedFrame;
    private bool _automaticMipmaps;
    private bool _mipmapsDirty;
    private uint _mipmapProducedFrame;
    private WebGpuTextureMipmapPlan? _mipmapPlan;
    private WebGpuTextureMipmapPlan? _candidateMipmapPlan;
    private int _capturedBaseAdoptionHandle;
    private BaseLayerProduction[] _baseLayerProduction = [];

    protected WebGpuLayeredTexture(WebGpuRendererHost renderer, T data) : base(renderer, data)
    {
        data.PropertyChanged += OnDataChanged;
        data.PushDataRequested += PushData;
    }

    protected abstract uint AuthoredWidth { get; }
    protected abstract uint AuthoredHeight { get; }
    protected abstract int AuthoredLayers { get; }
    protected abstract int AuthoredMips { get; }
    protected abstract int AuthoredSamples { get; }
    protected abstract ESizedInternalFormat AuthoredFormat { get; }
    protected abstract Mipmap2D GetAuthoredMip(int mip, int layer);
    protected abstract ETexMinFilter MinFilter { get; }
    protected abstract ETexMagFilter MagFilter { get; }
    protected abstract ETexWrapMode UWrap { get; }
    protected abstract ETexWrapMode VWrap { get; }
    protected abstract ETexWrapMode WWrap { get; }
    protected abstract float LodBias { get; }
    protected abstract float MaxAnisotropy { get; }
    protected abstract bool EnableComparison { get; }
    protected abstract ETextureCompareFunc CompareFunc { get; }
    protected abstract string SampledDimension { get; }
    protected virtual bool UsesGpuSources => false;
    protected virtual bool WantsAutomaticMipmaps => Data.AutoGenerateMipmaps;

    public override bool IsGenerated => _handle != 0 && _allocationRequest is null;
    public int ResourceHandle => _handle;
    public string Format => MapFormat(AuthoredFormat);
    public uint Width => AuthoredWidth;
    public uint Height => AuthoredHeight;
    public uint SampleCount => (uint)AuthoredSamples;
    public int MipLevelCount => StorageMipCount;
    public int ArrayLayerCount => AuthoredLayers;
    public override nint GetHandle() => IsGenerated ? _handle : 0;

    internal bool IsCurrentGpuAllocationForCopy => _handle != 0 && !_invalidated && !IsRetired && !Data.IsDestroyed &&
        _width == AuthoredWidth && _height == AuthoredHeight && _layers == AuthoredLayers &&
        _mips == StorageMipCount && _samples == AuthoredSamples && _format == AuthoredFormat && _storage == Data.RequiresStorageUsage &&
        _automaticMipmaps == WantsAutomaticMipmaps;

    private int StorageMipCount => WantsAutomaticMipmaps && Math.Max(AuthoredWidth, AuthoredHeight) > 0
        ? 1 + System.Numerics.BitOperations.Log2(Math.Max(AuthoredWidth, AuthoredHeight)) : AuthoredMips;

    protected void Invalidate()
    {
        SetField(ref _capturedBaseAdoptionHandle, 0, publishNotifications: false);
        _candidateMipmapPlan?.Dispose();
        SetField(ref _candidateMipmapPlan, null, publishNotifications: false);
        if (_allocationRequest is { } request) Renderer.CancelEngineResourceRequest(request);
        _allocationRequest = null;
        _invalidated = true;
    }
    private void OnDataChanged(object? sender, IXRPropertyChangedEventArgs change)
    {
        if (change.PropertyName is "Mipmaps" or "Textures" or "SizedInternalFormat" or
            "MultiSample" or "CopyGpuLayerSources" or nameof(XRTexture.RequiresStorageUsage) or nameof(XRTexture.AutoGenerateMipmaps))
            Invalidate();
    }

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (IsRetired || Data.IsDestroyed)
            throw new InvalidOperationException("WebGPU.Texture.Retired: a destroyed engine texture cannot regenerate GPU storage.");
        uint width = AuthoredWidth, height = AuthoredHeight;
        int layers = AuthoredLayers, mips = StorageMipCount, samples = AuthoredSamples;
        bool automaticMipmaps = WantsAutomaticMipmaps;
        ESizedInternalFormat format = AuthoredFormat;
        string encoding = MapFormat(format);
        if (width == 0 || height == 0 || layers < 1 || mips < 1 || samples is not (1 or 4))
            throw Unsupported("Create", "layered textures require positive dimensions, layers and mips, with one or four samples");
        if (automaticMipmaps && (samples != 1 || UsesGpuSources ||
            encoding is not ("rgba8unorm" or "rgba8unorm-srgb" or "rgba16float") || Data.LargestMipmapLevel != 0 ||
            (long)(mips - 1) * layers > WebGpuTextureMipmapPlan.MaximumPasses))
            throw Unsupported("Create", "automatic color mips require CPU-authored base pixels, one sample, base mip zero and at most 512 layer/mip passes");
        if (AuthoredMips < 1 || AuthoredMips > mips)
            throw Unsupported("Create", "the authored mip chain must fit its complete texture storage");
        if (samples > 1 && (layers != 1 || mips != 1))
            throw Unsupported("Create", "multisampled textures require one layer and one mip");
        if (mips > 1 + System.Numerics.BitOperations.Log2(Math.Max(width, height)))
            throw Unsupported("Create", "the authored mip chain exceeds the natural texture extent");
        if (_handle != 0 && !_invalidated && _width == width && _height == height && _layers == layers &&
            _mips == mips && _samples == samples && _format == format && _storage == Data.RequiresStorageUsage &&
            _automaticMipmaps == automaticMipmaps)
        {
            if (Renderer.IsRecordingEngineFrame) EnsureMipmapProduction();
            return;
        }
        if (_handle != 0 && Renderer.IsRecordingEngineFrame && _lastRecordedFrame == Renderer.EngineFrameSequence)
            throw Unsupported("InFrameMutation", "texture storage cannot change after a dependent pass was recorded");

        ValidateContent(width, height, layers, AuthoredMips, samples, encoding);
        ValidateSources();
        BrowserTextureUsage usage = WebGpuTextureFormat.Usage(Renderer, encoding, (uint)samples, Data.RequiresStorageUsage);
        // Keep the active generation and every dependent view valid until all candidate transfers succeed.
        BrowserTextureDescription descriptor = new(checked((int)width), checked((int)height),
            encoding, usage, mips, samples, Data.Name ?? "Engine layered texture", layers);
        if (_allocationRequest is { } obsolete && !obsolete.Descriptor.Equals(descriptor)) Invalidate();
        if (_allocationRequest is null)
        {
            _allocationRequest = Renderer.RequestEngineResource(this, 2, descriptor, reuse: false);
            try { CaptureInitialContent(_allocationRequest, layers, AuthoredMips, encoding); }
            catch { Invalidate(); throw; }
        }
        int candidate = Renderer.RequireEngineResource(_allocationRequest, claim: false);
        try
        {
            if (automaticMipmaps && mips > 1)
            {
                WebGpuTextureMipmapPlan mipmapPlan = _candidateMipmapPlan ??
                    new WebGpuTextureMipmapPlan(Renderer, candidate, mips, layers, encoding);
                SetField(ref _candidateMipmapPlan, mipmapPlan, publishNotifications: false);
                mipmapPlan.Prepare();
            }
            Renderer.StageEngineResourceInitialUploads(_allocationRequest, candidate);
            if (UsesGpuSources) UploadContent(candidate, layers, AuthoredMips, format);
        }
        catch (Exception error) when (error is not RenderResourcePreparationPendingException)
        {
            _candidateMipmapPlan?.Dispose();
            SetField(ref _candidateMipmapPlan, null, publishNotifications: false);
            Renderer.RetireEngineResourceAfterFrame(candidate);
            _allocationRequest = null;
            throw;
        }
        BaseLayerProduction[] baseLayerProduction = new BaseLayerProduction[layers];
        if (_handle != 0) ReleaseAllocation();
        Renderer.ClaimEngineResource(_allocationRequest);
        _allocationRequest = null;
        SetField(ref _baseLayerProduction, baseLayerProduction, publishNotifications: false);
        SetField(ref _handle, candidate);
        SetField(ref _width, width);
        SetField(ref _height, height);
        SetField(ref _layers, layers);
        SetField(ref _mips, mips);
        SetField(ref _samples, samples);
        SetField(ref _format, format);
        SetField(ref _storage, Data.RequiresStorageUsage);
        SetField(ref _automaticMipmaps, automaticMipmaps);
        SetField(ref _mipmapPlan, _candidateMipmapPlan, publishNotifications: false);
        SetField(ref _candidateMipmapPlan, null, publishNotifications: false);
        SetField(ref _mipmapsDirty, _mipmapPlan is not null, publishNotifications: false);
        _invalidated = false;
        OnContentPrepared();
        if (Renderer.IsRecordingEngineFrame) EnsureMipmapProduction();
    }

    protected virtual void ValidateSources() { }
    protected virtual void OnContentPrepared() { }

    private void ValidateContent(uint width, uint height, int layers, int mips, int samples, string encoding)
    {
        for (int layer = 0; layer < layers; layer++)
            for (int mip = 0; mip < mips; mip++)
            {
                Mipmap2D level = GetAuthoredMip(mip, layer);
                uint mipWidth = Math.Max(1u, width >> mip), mipHeight = Math.Max(1u, height >> mip);
                if (level is null || level.Width != mipWidth || level.Height != mipHeight)
                    throw Unsupported("Create", "every face or layer must have the same complete authored mip extents");
                DataSource? source = level.Data;
                if (WantsAutomaticMipmaps && mip == 0 && source is null)
                    throw Unsupported("Upload", "automatic mip generation requires initialized CPU base pixels for every layer");
                if (source is null || UsesGpuSources) continue;
                if (samples != 1) throw Unsupported("Upload", "multisampled textures cannot receive CPU mip bytes");
                int bytesPerPixel = WebGpuTextureUploadPixels.SourcePixelBytes(encoding, level.PixelFormat, level.PixelType);
                int length = checked((int)((long)mipWidth * mipHeight * bytesPerPixel));
                if (source.Address == VoidPtr.Zero || source.Length != length)
                    throw Unsupported("Upload", "layer mip data has a missing or mismatched source length");
            }
    }

    private void CaptureInitialContent(WebGpuResourceRequest request, int layers, int mips, string encoding)
    {
        if (UsesGpuSources) return;
        for (int layer = 0; layer < layers; layer++)
            for (int mip = 0; mip < mips; mip++)
            {
                Mipmap2D level = GetAuthoredMip(mip, layer);
                TransferMipContent(request, 0, level, mip, layer, encoding);
            }
    }

    protected virtual void UploadContent(int handle, int layers, int mips, ESizedInternalFormat format)
    {
        string encoding = WebGpuTextureFormat.Map(format);
        for (int layer = 0; layer < layers; layer++)
            for (int mip = 0; mip < mips; mip++)
            {
                Mipmap2D level = GetAuthoredMip(mip, layer);
                TransferMipContent(null, handle, level, mip, layer, encoding);
            }
    }

    private void TransferMipContent(WebGpuResourceRequest? request, int handle, Mipmap2D level, int mip, int layer, string encoding)
    {
        if (level.Data is not { } source) return;
        int length = checked((int)((long)level.Width * level.Height *
            WebGpuTextureUploadPixels.SourcePixelBytes(encoding, level.PixelFormat, level.PixelType)));
        if (source.Address == VoidPtr.Zero || source.Length != length)
            throw Unsupported("Upload", "layer mip data has a missing or mismatched source length");
        Span<byte> bytes = new((void*)source.Address, length);
        if (!WebGpuTextureUploadPixels.RequiresFloatToHalf(encoding, level.PixelFormat, level.PixelType))
        {
            TransferMipBytes(request, handle, level, mip, layer, bytes);
            return;
        }
        // Conversion is a cold upload operation. Both destinations copy the span
        // synchronously into their existing bounded retained transfer storage.
        int encodedLength = length / 2;
        if (encodedLength > 256 * 1024 * 1024)
            throw Unsupported("Upload", "encoded layer mip exceeds the 256 MiB preparation budget");
        byte[] encoded = ArrayPool<byte>.Shared.Rent(encodedLength);
        try
        {
            Span<byte> pixels = encoded.AsSpan(0, encodedLength);
            WebGpuTextureUploadPixels.EncodeRgba16Float(bytes, pixels);
            TransferMipBytes(request, handle, level, mip, layer, pixels);
        }
        finally { ArrayPool<byte>.Shared.Return(encoded); }
    }

    private void TransferMipBytes(WebGpuResourceRequest? request, int handle, Mipmap2D level, int mip, int layer, Span<byte> bytes)
    {
        int width = checked((int)level.Width), height = checked((int)level.Height);
        if (request is not null)
            Renderer.CaptureEngineResourceUpload(request, bytes, mip, layer, width, height);
        else
            Renderer.StageEngineTextureLayerMip(handle, mip, layer, width, height, bytes);
    }

    public int GetRenderView(int mip, int layer)
    {
        Generate();
        if (mip < 0 || mip >= _mips || layer < 0 || layer >= _layers)
            throw Unsupported("View", "a render attachment must select one authored mip and one layer or cube face");
        if (_renderViews.TryGetValue((mip, layer), out int view)) return view;
        if (_renderViews.Count >= 192) throw Unsupported("View", "the texture exceeds 192 retained render subresource views");
        view = Renderer.CreateEngineTextureView(this, new BrowserTextureViewDescription(_handle, mip, 1, "all",
            Data.Name ?? "Engine render layer", layer));
        _renderViews.Add((mip, layer), view);
        return view;
    }

    public int GetSampledView(bool depth)
    {
        Generate();
        EnsureMipmapProduction();
        bool depthFormat = WebGpuTextureFormat.IsDepth(Format);
        if (_samples != 1 || depth != depthFormat || depth && SampledDimension == "cube")
            throw Unsupported("Sample", "the shader binding requires a matching single-sample array or cube encoding");
        int baseMip = Data.LargestMipmapLevel;
        int lastMip = Math.Min(_mips - 1, Data.SmallestAllowedMipmapLevel);
        if (baseMip < 0 || baseMip > lastMip)
            throw Unsupported("Sample", "the authored sampled mip range is empty or outside texture storage");
        SampledViewKey key = new(baseMip, lastMip - baseMip + 1, depth);
        if (_sampledViews.TryGetValue(key, out int view)) return view;
        if (_sampledViews.Count >= 32) throw Unsupported("Sample", "the texture exceeds 32 retained sampled mip ranges");
        view = Renderer.CreateEngineTextureView(this, new BrowserTextureViewDescription(_handle, key.BaseMip, key.MipCount,
            depth ? "depth-only" : "all", Data.Name ?? "Engine sampled layered view", 0, _layers, SampledDimension));
        _sampledViews.Add(key, view);
        return view;
    }

    public int GetSampler(bool comparison)
    {
        Generate();
        if (LodBias != 0) throw Unsupported("Sampler", "nonzero sampler LOD bias has no exact WebGPU encoding");
        if (EnableComparison != comparison || comparison && CompareFunc != ETextureCompareFunc.LessOrEqual)
            throw Unsupported("Sampler", "the shader binding and authored sampler must agree on ordinary or less-equal comparison sampling");
        (string min, string mip, bool mipmapped) = MinFilter switch
        {
            ETexMinFilter.Nearest => ("nearest", "nearest", false),
            ETexMinFilter.Linear => ("linear", "nearest", false),
            ETexMinFilter.NearestMipmapNearest => ("nearest", "nearest", true),
            ETexMinFilter.LinearMipmapNearest => ("linear", "nearest", true),
            ETexMinFilter.NearestMipmapLinear => ("nearest", "linear", true),
            ETexMinFilter.LinearMipmapLinear => ("linear", "linear", true),
            _ => throw Unsupported("Sampler", "the minification filter has no exact WebGPU encoding"),
        };
        string mag = MagFilter switch
        {
            ETexMagFilter.Nearest => "nearest", ETexMagFilter.Linear => "linear",
            _ => throw Unsupported("Sampler", "the magnification filter has no exact WebGPU encoding"),
        };
        float anisotropy = MaxAnisotropy;
        if (!float.IsFinite(anisotropy) || anisotropy < 1 || anisotropy > 16 || anisotropy != MathF.Truncate(anisotropy) ||
            anisotropy > 1 && (min != "linear" || mag != "linear" || mip != "linear"))
            throw Unsupported("Sampler", "anisotropy requires an integer from one to sixteen and linear filters");
        float minLod = mipmapped ? Math.Max(Data.MinLOD, 0) : 0;
        float maxLod = mipmapped ? Math.Min(Data.MaxLOD, 32) : 0;
        if (minLod > maxLod || minLod > 32 || maxLod < 0 || !mipmapped && (Data.MinLOD > 0 || Data.MaxLOD < 0))
            throw Unsupported("Sampler", "the authored LOD clamp range excludes available sampled levels");
        SamplerState state = new(Address(UWrap), Address(VWrap), Address(WWrap), min, mag, mip,
            minLod, maxLod, (int)anisotropy, comparison ? "less-equal" : null);
        if (_samplerHandle != 0 && state == _samplerState)
        {
            if (_samplerRequest is { } obsolete) Renderer.CancelEngineResourceRequest(obsolete);
            _samplerRequest = null;
            return _samplerHandle;
        }
        int handle = Renderer.CreateEngineReplacement(this, ref _samplerRequest, 4, new BrowserSamplerDescription(state.U, state.V, state.Min,
            state.Mag, state.Mip, Data.Name ?? "Engine layered sampler", state.MaxLod, state.Anisotropy,
            LodMinClamp: state.MinLod, Compare: state.Compare, AddressW: state.W));
        if (_samplerHandle != 0)
        {
            Renderer.ReleaseEngineDrawDependencies(this);
            Renderer.RetireEngineResourceAfterFrame(_samplerHandle);
            SetField(ref _samplerHandle, 0);
        }
        SetField(ref _samplerState, state);
        SetField(ref _samplerHandle, handle);
        return handle;
    }

    public void PushData()
    {
        if (Renderer.IsRecordingEngineFrame && _lastRecordedFrame == Renderer.EngineFrameSequence)
            throw Unsupported("InFrameMutation", "texture bytes cannot change after a dependent pass was recorded");
        // Whole-image upload intent supersedes every captured base receipt even
        // while its replacement preparation is still pending.
        Array.Clear(_baseLayerProduction);
        if (UsesGpuSources && _allocationRequest is null) Invalidate();
        WebGpuResourceRequest? pending = _allocationRequest;
        int previous = _handle;
        try { Generate(); }
        catch (RenderResourcePreparationPendingException)
        {
            if (!UsesGpuSources && pending is not null && ReferenceEquals(pending, _allocationRequest))
                CaptureInitialContent(pending, AuthoredLayers, AuthoredMips, Format);
            return;
        }
        if (!UsesGpuSources && (_handle == previous || pending is not null))
        {
            ValidateContent(_width, _height, _layers, AuthoredMips, _samples, Format);
            UploadContent(_handle, _layers, AuthoredMips, _format);
            SetField(ref _mipmapsDirty, _mipmapPlan is not null, publishNotifications: false);
            SetField(ref _mipmapProducedFrame, 0u, publishNotifications: false);
            SetField(ref _capturedBaseAdoptionHandle, 0, publishNotifications: false);
        }
    }

    private void EnsureMipmapProduction()
    {
        if (!_mipmapsDirty || _mipmapPlan is null) return;
        if (!Renderer.IsRecordingEngineFrame)
            throw new WebGpuResourcePreparationPendingException("WebGPU.Texture.MipmapFrameRequired: automatic mip production awaits normal engine frame recording.");
        if (_mipmapProducedFrame == Renderer.EngineFrameSequence) return;
        Renderer.RequireAuthoredOrderingTextureWritable(this);
        _mipmapPlan.Record();
        MarkImageProduced();
        SetField(ref _mipmapProducedFrame, Renderer.EngineFrameSequence, publishNotifications: false);
    }

    /// <summary>Adopts persisted float base pixels after the host attests every captured layer and CPU payload identity.</summary>
    internal void AdoptCapturedBaseMipmaps(int expectedHandle)
    {
        ValidateOwnerGeneration();
        if (IsRetired || Data.IsDestroyed || _handle != expectedHandle || expectedHandle == 0 || _allocationRequest is not null ||
            _width != AuthoredWidth || _height != AuthoredHeight || _layers != 26 || _layers != AuthoredLayers ||
            _width > 1024 || _height > 1024 || _samples != 1 || AuthoredSamples != 1 ||
            _format != ESizedInternalFormat.Rgba16f || AuthoredFormat != _format ||
            _mips != StorageMipCount || AuthoredMips != 1 || !Data.AutoGenerateMipmaps || UsesGpuSources ||
            _storage != Data.RequiresStorageUsage || !HasCommittedProduction)
            throw Unsupported("CaptureAdoption", "captured base metadata must match the same accepted 26-layer RGBA16F allocation and its full physical mip chain");
        if (_capturedBaseAdoptionHandle == expectedHandle) return;
        if (_automaticMipmaps || _mipmapPlan is not null)
            throw Unsupported("CaptureAdoption", "only an unadopted capture allocation can accept persisted base pixels without upload");
        ValidateContent(_width, _height, _layers, 1, _samples, Format);
        for (int layer = 0; layer < _layers; layer++)
        {
            Mipmap2D level = GetAuthoredMip(0, layer);
            if (level.PixelFormat != EPixelFormat.Rgba || level.PixelType != EPixelType.Float || level.Data is null)
                throw Unsupported("CaptureAdoption", "every captured layer requires its exact persisted RGBA float base pixels");
        }
        WebGpuTextureMipmapPlan? plan = _mips > 1 ? new(Renderer, expectedHandle, _mips, _layers, Format) : null;
        SetField(ref _mipmapPlan, plan, publishNotifications: false);
        SetField(ref _automaticMipmaps, true, publishNotifications: false);
        SetField(ref _invalidated, false, publishNotifications: false);
        SetField(ref _mipmapsDirty, plan is not null, publishNotifications: false);
        SetField(ref _mipmapProducedFrame, 0u, publishNotifications: false);
        SetField(ref _capturedBaseAdoptionHandle, expectedHandle, publishNotifications: false);
    }

    internal void PrepareCapturedMipmaps(int expectedHandle)
    {
        RequireCapturedBaseAdoption(expectedHandle);
        _mipmapPlan?.Prepare();
    }

    /// <summary>Records every derived layer/mip in the current normal frame; a 1x1 base needs no additional producer.</summary>
    internal bool RecordCapturedMipmaps(int expectedHandle)
    {
        RequireCapturedBaseAdoption(expectedHandle);
        if (_mipmapPlan is null) return false;
        EnsureMipmapProduction();
        return true;
    }

    private void RequireCapturedBaseAdoption(int expectedHandle)
    {
        if (expectedHandle == 0 || _capturedBaseAdoptionHandle != expectedHandle || _handle != expectedHandle ||
            !IsCurrentGpuAllocationForCopy || !_automaticMipmaps)
            throw Unsupported("CaptureAdoption", "the accepted capture allocation changed before mip production");
    }

    public void MarkRecorded()
    {
        SetField(ref _lastRecordedFrame, Renderer.EngineFrameSequence, publishNotifications: false);
        Renderer.MarkEngineTextureRecorded(_handle);
    }

    internal bool WasRecordedInFrame(uint frameSequence) => _lastRecordedFrame == frameSequence;

    private uint _lastProducedFrame;
    private uint _lastCommittedProducedFrame;
    private ulong _productionTicket;
    private ulong _committedProductionTicket;

    public void MarkProduced()
    {
        MarkImageProduced();
        for (int layer = 0; layer < _baseLayerProduction.Length; layer++)
            MarkBaseLayerProduced(layer);
    }

    /// <summary>Tracks an exact framebuffer write without invalidating untouched base layers.</summary>
    internal void MarkSubresourceProduced(int mip, int layer)
    {
        int selectedLayer = layer == -1 && _layers == 1 ? 0 : layer;
        if (mip < 0 || mip >= _mips || selectedLayer < 0 || selectedLayer >= _baseLayerProduction.Length)
            throw Unsupported("Production", "the produced mip/layer must belong to this physical texture generation");
        MarkImageProduced();
        if (mip == 0) MarkBaseLayerProduced(selectedLayer);
    }

    private void MarkBaseLayerProduced(int layer)
    {
        ref BaseLayerProduction production = ref _baseLayerProduction[layer];
        production.Ticket = _productionTicket;
        production.Frame = Renderer.EngineFrameSequence;
    }

    private void MarkImageProduced()
    {
        Renderer.RequireAuthoredOrderingTextureWritable(this);
        MarkRecorded();
        if (_productionTicket == ulong.MaxValue)
            throw new InvalidOperationException("WebGPU.Texture.ProductionTicketExhausted: replace the renderer.");
        SetField(ref _productionTicket, _productionTicket + 1, publishNotifications: false);
        SetField(ref _lastProducedFrame, Renderer.EngineFrameSequence, publishNotifications: false);
        Renderer.RegisterProducedTexture(this);
        SetField(ref _mipmapsDirty, _mipmapPlan is not null, publishNotifications: false);
        SetField(ref _mipmapProducedFrame, 0u, publishNotifications: false);
    }

    public void CommitProducedFrame(uint frameSequence)
    {
        if (_lastProducedFrame == frameSequence)
        {
            SetField(ref _lastCommittedProducedFrame, frameSequence, publishNotifications: false);
            SetField(ref _committedProductionTicket, _productionTicket, publishNotifications: false);
            if (_mipmapProducedFrame == frameSequence)
                SetField(ref _mipmapsDirty, false, publishNotifications: false);
        }
        for (int layer = 0; layer < _baseLayerProduction.Length; layer++)
        {
            ref BaseLayerProduction production = ref _baseLayerProduction[layer];
            if (production.Frame == frameSequence)
                production.CommittedTicket = production.Ticket;
        }
    }

    /// <summary>Returns the current base content identity, including a recorded but uncommitted write.</summary>
    internal ulong GetBaseLayerTicket(int layer)
        => HasCurrentBaseLayerStorage(layer) ? _baseLayerProduction[layer].Ticket : 0;

    /// <summary>Returns zero until the latest write to this base layer has committed.</summary>
    internal ulong GetCommittedBaseLayerTicket(int layer)
    {
        ulong ticket = GetBaseLayerTicket(layer);
        return ticket != 0 && ticket == _baseLayerProduction[layer].CommittedTicket ? ticket : 0;
    }

    internal bool IsCurrentCommittedBaseLayerTicket(int layer, ulong ticket)
        => ticket != 0 && GetCommittedBaseLayerTicket(layer) == ticket;

    private bool HasCurrentBaseLayerStorage(int layer)
        // Captured CPU mip references and AutoGenerateMipmaps may be installed
        // before adoption. They do not change this physical base content.
        => (uint)layer < (uint)_baseLayerProduction.Length && IsGenerated && !IsRetired && !Data.IsDestroyed &&
            _width == AuthoredWidth && _height == AuthoredHeight && _layers == AuthoredLayers &&
            _mips == StorageMipCount && _samples == AuthoredSamples && _format == AuthoredFormat;

    public bool WasProducedInFrame(uint frameSequence) => _lastProducedFrame == frameSequence;

    public bool HasCommittedProduction
        => IsGenerated && !_mipmapsDirty && _lastProducedFrame != 0 && _lastProducedFrame == _lastCommittedProducedFrame &&
            _productionTicket == _committedProductionTicket;

    internal bool HasUncommittedProduction => _lastProducedFrame != 0 && !HasCommittedProduction;

    public ulong ProductionTicket => _productionTicket;

    public override void Destroy()
    {
        _candidateMipmapPlan?.Dispose();
        SetField(ref _candidateMipmapPlan, null, publishNotifications: false);
        Renderer.CancelEngineResourceRequests(this);
        _allocationRequest = null;
        _samplerRequest = null;
        ReleaseAllocation();
    }

    private void ReleaseAllocation()
    {
        _mipmapPlan?.Dispose();
        SetField(ref _mipmapPlan, null, publishNotifications: false);
        SetField(ref _mipmapsDirty, false, publishNotifications: false);
        SetField(ref _mipmapProducedFrame, 0u, publishNotifications: false);
        SetField(ref _capturedBaseAdoptionHandle, 0, publishNotifications: false);
        SetField(ref _baseLayerProduction, [], publishNotifications: false);
        if (_handle == 0 && _samplerHandle == 0) return;
        Renderer.ReleaseEngineDrawDependencies(this);
        foreach (int view in _renderViews.Values) Renderer.RetireEngineResourceAfterFrame(view);
        foreach (int view in _sampledViews.Values) Renderer.RetireEngineResourceAfterFrame(view);
        _renderViews.Clear();
        _sampledViews.Clear();
        if (_samplerHandle != 0) Renderer.RetireEngineResourceAfterFrame(_samplerHandle);
        SetField(ref _samplerHandle, 0);
        Renderer.RetireEngineResourceAfterFrame(_handle);
        SetField(ref _handle, 0);
        SetField(ref _lastRecordedFrame, 0u);
        SetField(ref _lastProducedFrame, 0u);
        SetField(ref _lastCommittedProducedFrame, 0u);
        SetField(ref _committedProductionTicket, 0u);
        _invalidated = true;
    }

    protected override void OnRetiring()
    {
        Data.PropertyChanged -= OnDataChanged;
        Data.PushDataRequested -= PushData;
        base.OnRetiring();
    }

    protected static string MapFormat(ESizedInternalFormat format) => WebGpuTextureFormat.Map(format);

    protected static NotSupportedException Unsupported(string operation, string reason)
        => new($"WebGPU.Texture.OperationUnsupported: {operation}: {reason}.");

    private static string Address(ETexWrapMode mode) => mode switch
    {
        ETexWrapMode.Repeat => "repeat", ETexWrapMode.MirroredRepeat => "mirror-repeat",
        ETexWrapMode.ClampToEdge => "clamp-to-edge",
        _ => throw Unsupported("Sampler", "the wrap mode has no exact WebGPU encoding"),
    };
}
