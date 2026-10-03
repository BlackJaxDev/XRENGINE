using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Owns an exact-format 2D WebGPU texture and its generation-scoped render views.</summary>
public sealed unsafe partial class WebGpuTexture2D : WebGpuObject<XRTexture2D>, IWebGpuProducedTexture
{
    private readonly Dictionary<int, int> _views = [];
    private int _handle;
    private uint _width;
    private uint _height;
    private uint _samples;
    private int _mipCount;
    private ESizedInternalFormat _format;
    private bool _invalidated = true;
    private bool _storage;
    private uint _lastRecordedFrame;

    public WebGpuTexture2D(WebGpuRendererHost renderer, XRTexture2D data) : base(renderer, data)
    {
        data.Resized += Invalidate;
        data.PropertyChanged += OnDataChanged;
        data.PushDataRequested += PushData;
    }

    public override bool IsGenerated => _handle != 0;
    public int ResourceHandle => _handle;
    public string Format => MapFormat(Data.SizedInternalFormat);
    public uint Width => Data.Width;
    public uint Height => Data.Height;
    public uint SampleCount => Data.MultiSampleCount;
    public override nint GetHandle() => _handle;

    internal bool IsCurrentGpuAllocationForCopy => _handle != 0 && !_invalidated && !IsRetired && !Data.IsDestroyed &&
        _width == Data.Width && _height == Data.Height && _samples == Data.MultiSampleCount &&
        _mipCount == Data.Mipmaps.Length && _format == Data.SizedInternalFormat && _storage == Data.RequiresStorageUsage;

    private void Invalidate() => _invalidated = true;

    private void OnDataChanged(object? sender, IXRPropertyChangedEventArgs change)
    {
        if (change.PropertyName is nameof(XRTexture2D.Mipmaps) or nameof(XRTexture2D.SizedInternalFormat)
            or nameof(XRTexture2D.MultiSampleCount) or nameof(XRTexture.RequiresStorageUsage))
            Invalidate();
    }

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (IsRetired || Data.IsDestroyed)
            throw new InvalidOperationException("WebGPU.Texture.Retired: a destroyed engine texture cannot regenerate GPU storage.");
        Mipmap2D[] mips = Data.Mipmaps;
        if (mips.Length == 0 || Data.Width == 0 || Data.Height == 0 || Data.MultiSampleCount is not (1 or 4))
            throw Unsupported("Create", "a nonempty 2D texture requires positive dimensions and one or four samples");
        if (Data.AutoGenerateMipmaps)
            throw Unsupported("Create", "automatic mip generation has no admitted WebGPU resource path");
        string format = MapFormat(Data.SizedInternalFormat);
        if (_handle != 0 && !_invalidated && _width == Data.Width && _height == Data.Height &&
            _samples == Data.MultiSampleCount && _mipCount == mips.Length && _format == Data.SizedInternalFormat && _storage == Data.RequiresStorageUsage)
            return;
        if (_handle != 0 && Renderer.IsRecordingEngineFrame && _lastRecordedFrame == Renderer.EngineFrameSequence)
            throw Unsupported("InFrameMutation", "texture storage cannot change after a dependent pass was recorded");

        BrowserTextureUsage usage = WebGpuTextureFormat.Usage(Renderer, format, Data.MultiSampleCount, Data.RequiresStorageUsage);
        if (Data.MultiSampleCount > 1 && mips.Length != 1)
            throw Unsupported("Create", "multisampled textures cannot have a mip chain");
        for (int mip = 0; mip < mips.Length; mip++)
        {
            Mipmap2D level = mips[mip];
            if (level.Width != Math.Max(1u, Data.Width >> mip) || level.Height != Math.Max(1u, Data.Height >> mip))
                throw Unsupported("Create", "the authored mip extents do not form a complete 2D chain");
            if (level.Data is not null)
            {
                if (Data.MultiSampleCount != 1) throw Unsupported("Upload", "multisampled textures cannot receive CPU mip bytes");
                int bytesPerPixel = WebGpuTextureFormat.UploadPixelBytes(format, level.PixelFormat, level.PixelType);
                int length = checked((int)((long)level.Width * level.Height * bytesPerPixel));
                if (level.Data.Address == VoidPtr.Zero || level.Data.Length != length)
                    throw Unsupported("Upload", "mip data has a missing or mismatched source length");
            }
        }

        int handle = Renderer.CreateTexture(new BrowserTextureDescription(checked((int)Data.Width), checked((int)Data.Height),
            format, usage, mips.Length, checked((int)Data.MultiSampleCount), Data.Name ?? "Engine texture"));
        // Validate and populate a replacement before retiring the active allocation.
        // A failed HDR upload must not invalidate existing views and draw bindings.
        try { UploadMipmaps(handle, Data.Width, Data.Height, Data.MultiSampleCount, Data.SizedInternalFormat, mips); }
        catch
        {
            Renderer.RetireEngineResourceAfterFrame(handle);
            throw;
        }
        if (_handle != 0) Destroy();
        SetField(ref _handle, handle);
        SetField(ref _width, Data.Width);
        SetField(ref _height, Data.Height);
        SetField(ref _samples, Data.MultiSampleCount);
        SetField(ref _mipCount, mips.Length);
        SetField(ref _format, Data.SizedInternalFormat);
        SetField(ref _storage, Data.RequiresStorageUsage);
        _invalidated = false;
    }

    public int GetRenderView(int mip, int layer)
    {
        Generate();
        if (layer != -1 || mip < 0 || mip >= _mipCount)
            throw Unsupported("View", "only one 2D layer and an authored mip level are admitted");
        if (_views.TryGetValue(mip, out int view)) return view;
        view = Renderer.CreateTextureView(new BrowserTextureViewDescription(_handle, mip, 1, "all",
            Data.Name ?? "Engine render view"));
        _views.Add(mip, view);
        return view;
    }

    public void PushData()
    {
        if (Renderer.IsRecordingEngineFrame && _lastRecordedFrame == Renderer.EngineFrameSequence)
            throw new NotSupportedException("WebGPU.Texture.InFrameMutationUnsupported: texture bytes cannot change after a dependent pass was recorded; publish updates before the next engine frame.");
        int previous = _handle;
        Generate();
        // Storage creation uploads all authored levels before publishing the new generation.
        if (_handle == previous) UploadMipmaps(_handle, _width, _height, _samples, _format, Data.Mipmaps);
    }

    internal void MarkRecorded()
        => SetField(ref _lastRecordedFrame, Renderer.EngineFrameSequence, publishNotifications: false);

    internal bool WasRecordedInFrame(uint frameSequence) => _lastRecordedFrame == frameSequence;

    private uint _lastProducedFrame;
    private uint _lastCommittedProducedFrame;
    private ulong _productionTicket;
    private ulong _committedProductionTicket;

    public void MarkProduced()
    {
        MarkRecorded();
        if (_productionTicket == ulong.MaxValue)
            throw new InvalidOperationException("WebGPU.Texture.ProductionTicketExhausted: replace the renderer.");
        SetField(ref _productionTicket, _productionTicket + 1, publishNotifications: false);
        SetField(ref _lastProducedFrame, Renderer.EngineFrameSequence, publishNotifications: false);
        Renderer.RegisterProducedTexture(this);
    }

    public void CommitProducedFrame(uint frameSequence)
    {
        if (_lastProducedFrame == frameSequence)
        {
            SetField(ref _lastCommittedProducedFrame, frameSequence, publishNotifications: false);
            SetField(ref _committedProductionTicket, _productionTicket, publishNotifications: false);
        }
    }

    public bool WasProducedInFrame(uint frameSequence) => _lastProducedFrame == frameSequence;

    public bool HasCommittedProduction
        => IsGenerated && _lastProducedFrame != 0 && _lastProducedFrame == _lastCommittedProducedFrame &&
            _productionTicket == _committedProductionTicket;

    public ulong ProductionTicket => _productionTicket;

    private void UploadMipmaps(int handle, uint width, uint height, uint samples,
        ESizedInternalFormat format, Mipmap2D[] mips)
    {
        string encoding = WebGpuTextureFormat.Map(format);
        for (int mip = 0; mip < mips.Length; mip++)
        {
            Mipmap2D level = mips[mip];
            if (level.Width != Math.Max(1u, width >> mip) || level.Height != Math.Max(1u, height >> mip))
                throw Unsupported("Upload", "the authored mip extents do not form a complete 2D chain");
            DataSource? source = level.Data;
            if (source is null) continue;
            if (samples != 1) throw Unsupported("Upload", "multisampled textures cannot receive CPU mip bytes");
            int bytesPerPixel = WebGpuTextureFormat.UploadPixelBytes(encoding, level.PixelFormat, level.PixelType);
            int length = checked((int)((long)level.Width * level.Height * bytesPerPixel));
            if (source.Address == VoidPtr.Zero || source.Length != length)
                throw Unsupported("Upload", "mip data has a missing or mismatched source length");
            Renderer.UploadTextureMip(handle, mip, 0, 0, checked((int)level.Width), checked((int)level.Height),
                new Span<byte>((void*)source.Address, length));
        }
    }

    public override void Destroy()
    {
        if (_handle == 0 && _samplerHandle == 0) return;
        Renderer.ReleaseEngineDrawDependencies(this);
        RetireSamplingResources();
        foreach (int view in _views.Values)
            Renderer.RetireEngineResourceAfterFrame(view);
        _views.Clear();
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
        Data.Resized -= Invalidate;
        Data.PropertyChanged -= OnDataChanged;
        Data.PushDataRequested -= PushData;
        base.OnRetiring();
    }

    private static string MapFormat(ESizedInternalFormat format) => WebGpuTextureFormat.Map(format);

    private static NotSupportedException Unsupported(string operation, string reason)
        => new($"WebGPU.Texture.OperationUnsupported: {operation}: {reason}.");
}
