using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Owns one engine array or cube image and its generation-scoped subresource views.</summary>
public abstract unsafe class WebGpuLayeredTexture<T> : WebGpuObject<T> where T : XRTexture
{
    private readonly record struct SampledViewKey(int BaseMip, int MipCount, bool Depth);
    private readonly record struct SamplerState(string U, string V, string W, string Min, string Mag,
        string Mip, float MinLod, float MaxLod, int Anisotropy, string? Compare);

    private readonly Dictionary<(int Mip, int Layer), int> _renderViews = [];
    private readonly Dictionary<SampledViewKey, int> _sampledViews = [];
    private SamplerState _samplerState;
    private int _samplerHandle;
    private int _handle;
    private uint _width;
    private uint _height;
    private int _layers;
    private int _mips;
    private int _samples;
    private ESizedInternalFormat _format;
    private bool _invalidated = true;
    private uint _lastRecordedFrame;

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

    public override bool IsGenerated => _handle != 0;
    public int ResourceHandle => _handle;
    public string Format => MapFormat(AuthoredFormat);
    public uint Width => AuthoredWidth;
    public uint Height => AuthoredHeight;
    public uint SampleCount => (uint)AuthoredSamples;
    public override nint GetHandle() => _handle;

    protected void Invalidate() => _invalidated = true;
    private void OnDataChanged(object? sender, IXRPropertyChangedEventArgs change)
    {
        if (change.PropertyName is "Mipmaps" or "Textures" or "SizedInternalFormat" or
            "MultiSample" or "CopyGpuLayerSources")
            Invalidate();
    }

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (IsRetired || Data.IsDestroyed)
            throw new InvalidOperationException("WebGPU.Texture.Retired: a destroyed engine texture cannot regenerate GPU storage.");
        uint width = AuthoredWidth, height = AuthoredHeight;
        int layers = AuthoredLayers, mips = AuthoredMips, samples = AuthoredSamples;
        ESizedInternalFormat format = AuthoredFormat;
        string encoding = MapFormat(format);
        if (width == 0 || height == 0 || layers < 1 || mips < 1 || samples is not (1 or 4))
            throw Unsupported("Create", "layered textures require positive dimensions, layers and mips, with one or four samples");
        if (Data.AutoGenerateMipmaps)
            throw Unsupported("Create", "automatic mip generation has no admitted WebGPU resource path");
        if (samples > 1 && (layers != 1 || mips != 1))
            throw Unsupported("Create", "multisampled textures require one layer and one mip");
        if (mips > 1 + System.Numerics.BitOperations.Log2(Math.Max(width, height)))
            throw Unsupported("Create", "the authored mip chain exceeds the natural texture extent");
        if (_handle != 0 && !_invalidated && _width == width && _height == height && _layers == layers &&
            _mips == mips && _samples == samples && _format == format)
            return;
        if (_handle != 0 && Renderer.IsRecordingEngineFrame && _lastRecordedFrame == Renderer.EngineFrameSequence)
            throw Unsupported("InFrameMutation", "texture storage cannot change after a dependent pass was recorded");

        ValidateContent(width, height, layers, mips, samples, encoding);
        ValidateSources();
        bool red = encoding == "r8unorm";
        bool color = red || encoding is "rgba8unorm" or "rgba8unorm-srgb" or "rgba16float";
        BrowserTextureUsage usage = BrowserTextureUsage.TextureBinding;
        if (!red) usage |= BrowserTextureUsage.RenderAttachment;
        if (color && samples == 1) usage |= BrowserTextureUsage.CopySource | BrowserTextureUsage.CopyDestination;
        else if (encoding == "depth32float" && samples == 1) usage |= BrowserTextureUsage.CopySource;
        // Keep the active generation and every dependent view valid until all candidate transfers succeed.
        int candidate = Renderer.CreateTexture(new BrowserTextureDescription(checked((int)width), checked((int)height),
            encoding, usage, mips, samples, Data.Name ?? "Engine layered texture", layers));
        try { UploadContent(candidate, layers, mips, format); }
        catch
        {
            Renderer.RetireEngineResourceAfterFrame(candidate);
            throw;
        }
        if (_handle != 0) Destroy();
        SetField(ref _handle, candidate);
        SetField(ref _width, width);
        SetField(ref _height, height);
        SetField(ref _layers, layers);
        SetField(ref _mips, mips);
        SetField(ref _samples, samples);
        SetField(ref _format, format);
        _invalidated = false;
    }

    protected virtual void ValidateSources() { }

    private void ValidateContent(uint width, uint height, int layers, int mips, int samples, string encoding)
    {
        bool red = encoding == "r8unorm";
        bool byteColor = encoding is "rgba8unorm" or "rgba8unorm-srgb";
        if (red && samples != 1)
            throw Unsupported("Create", "R8 coverage textures require one sample");
        for (int layer = 0; layer < layers; layer++)
            for (int mip = 0; mip < mips; mip++)
            {
                Mipmap2D level = GetAuthoredMip(mip, layer);
                uint mipWidth = Math.Max(1u, width >> mip), mipHeight = Math.Max(1u, height >> mip);
                if (level is null || level.Width != mipWidth || level.Height != mipHeight)
                    throw Unsupported("Create", "every face or layer must have the same complete authored mip extents");
                DataSource? source = level.Data;
                if (source is null || UsesGpuSources) continue;
                if (samples != 1 || level.PixelType != EPixelType.UnsignedByte ||
                    !(byteColor && level.PixelFormat == EPixelFormat.Rgba || red && level.PixelFormat == EPixelFormat.Red))
                    throw Unsupported("Upload", "only single-sample, tightly packed RGBA8 or R8 layer mip bytes can be uploaded");
                int length = checked((int)((long)mipWidth * mipHeight * (red ? 1 : 4)));
                if (source.Address == VoidPtr.Zero || source.Length != length)
                    throw Unsupported("Upload", "layer mip data has a missing or mismatched source length");
            }
    }

    protected virtual void UploadContent(int handle, int layers, int mips, ESizedInternalFormat format)
    {
        bool red = format == ESizedInternalFormat.R8;
        for (int layer = 0; layer < layers; layer++)
            for (int mip = 0; mip < mips; mip++)
            {
                Mipmap2D level = GetAuthoredMip(mip, layer);
                DataSource? source = level.Data;
                if (source is null) continue;
                int length = checked((int)((long)level.Width * level.Height * (red ? 1 : 4)));
                Renderer.UploadTextureLayerMip(handle, mip, layer, checked((int)level.Width),
                    checked((int)level.Height), new Span<byte>((void*)source.Address, length));
            }
    }

    public int GetRenderView(int mip, int layer)
    {
        Generate();
        if (Format == "r8unorm") throw Unsupported("View", "R8 coverage textures are sampled, not render attachments");
        if (mip < 0 || mip >= _mips || layer < 0 || layer >= _layers)
            throw Unsupported("View", "a render attachment must select one authored mip and one layer or cube face");
        if (_renderViews.TryGetValue((mip, layer), out int view)) return view;
        if (_renderViews.Count >= 192) throw Unsupported("View", "the texture exceeds 192 retained render subresource views");
        view = Renderer.CreateTextureView(new BrowserTextureViewDescription(_handle, mip, 1, "all",
            Data.Name ?? "Engine render layer", layer));
        _renderViews.Add((mip, layer), view);
        return view;
    }

    public int GetSampledView(bool depth)
    {
        Generate();
        bool depthFormat = Format is "depth16unorm" or "depth24plus" or "depth32float" or "depth24plus-stencil8";
        if (_samples != 1 || depth != depthFormat || depth && SampledDimension == "cube" ||
            !depth && Format is not ("r8unorm" or "rgba8unorm" or "rgba8unorm-srgb" or "rgba16float"))
            throw Unsupported("Sample", "the shader binding requires a matching single-sample array or cube encoding");
        int baseMip = Data.LargestMipmapLevel;
        int lastMip = Math.Min(_mips - 1, Data.SmallestAllowedMipmapLevel);
        if (baseMip < 0 || baseMip > lastMip)
            throw Unsupported("Sample", "the authored sampled mip range is empty or outside texture storage");
        SampledViewKey key = new(baseMip, lastMip - baseMip + 1, depth);
        if (_sampledViews.TryGetValue(key, out int view)) return view;
        if (_sampledViews.Count >= 32) throw Unsupported("Sample", "the texture exceeds 32 retained sampled mip ranges");
        view = Renderer.CreateTextureView(new BrowserTextureViewDescription(_handle, key.BaseMip, key.MipCount,
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
        if (_samplerHandle != 0 && state == _samplerState) return _samplerHandle;
        if (_samplerHandle != 0)
        {
            Renderer.ReleaseEngineDrawDependencies(this);
            Renderer.RetireEngineResourceAfterFrame(_samplerHandle);
            SetField(ref _samplerHandle, 0);
        }
        int handle = Renderer.CreateSampler(new BrowserSamplerDescription(state.U, state.V, state.Min,
            state.Mag, state.Mip, Data.Name ?? "Engine layered sampler", state.MaxLod, state.Anisotropy,
            LodMinClamp: state.MinLod, Compare: state.Compare, AddressW: state.W));
        SetField(ref _samplerState, state);
        SetField(ref _samplerHandle, handle);
        return handle;
    }

    public void PushData()
    {
        if (Renderer.IsRecordingEngineFrame && _lastRecordedFrame == Renderer.EngineFrameSequence)
            throw Unsupported("InFrameMutation", "texture bytes cannot change after a dependent pass was recorded");
        Invalidate();
        Generate();
    }

    public void MarkRecorded() => SetField(ref _lastRecordedFrame, Renderer.EngineFrameSequence, publishNotifications: false);

    public override void Destroy()
    {
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
        _invalidated = true;
    }

    protected override void OnRetiring()
    {
        Data.PropertyChanged -= OnDataChanged;
        Data.PushDataRequested -= PushData;
        base.OnRetiring();
    }

    protected static string MapFormat(ESizedInternalFormat format) => format switch
    {
        ESizedInternalFormat.R8 => "r8unorm",
        ESizedInternalFormat.Rgba8 => "rgba8unorm",
        ESizedInternalFormat.Srgb8Alpha8 => "rgba8unorm-srgb",
        ESizedInternalFormat.Rgba16f => "rgba16float",
        ESizedInternalFormat.DepthComponent16 => "depth16unorm",
        ESizedInternalFormat.DepthComponent24 => "depth24plus",
        ESizedInternalFormat.DepthComponent32f => "depth32float",
        ESizedInternalFormat.Depth24Stencil8 => "depth24plus-stencil8",
        _ => throw Unsupported("Create", $"texture format '{format}' has no exact admitted WebGPU encoding"),
    };

    protected static NotSupportedException Unsupported(string operation, string reason)
        => new($"WebGPU.Texture.OperationUnsupported: {operation}: {reason}.");

    private static string Address(ETexWrapMode mode) => mode switch
    {
        ETexWrapMode.Repeat => "repeat", ETexWrapMode.MirroredRepeat => "mirror-repeat",
        ETexWrapMode.ClampToEdge => "clamp-to-edge",
        _ => throw Unsupported("Sampler", "the wrap mode has no exact WebGPU encoding"),
    };
}
