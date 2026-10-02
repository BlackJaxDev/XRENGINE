using XREngine.Data.Core;

namespace XREngine.Rendering.WebGPU;

/// <summary>Owns generation-scoped, attachment-only storage for an engine renderbuffer.</summary>
public sealed class WebGpuRenderBuffer : WebGpuObject<XRRenderBuffer>, IWebGpuProducedTexture
{
    private int _handle;
    private int _view;
    private uint _width;
    private uint _height;
    private uint _samples;
    private ERenderBufferStorage _format;
    private bool _invalidated = true;
    private uint _lastRecordedFrame;
    private uint _lastProducedFrame;
    private uint _lastCommittedProducedFrame;
    private ulong _productionTicket;
    private ulong _committedProductionTicket;

    public WebGpuRenderBuffer(WebGpuRendererHost renderer, XRRenderBuffer data) : base(renderer, data)
    {
        data.PropertyChanged += OnDataChanged;
        data.AllocateRequested += AllocateCurrent;
    }

    public override bool IsGenerated => _handle != 0;
    public override nint GetHandle() => _handle;
    public uint Width => Data.Width;
    public uint Height => Data.Height;
    public uint SampleCount => Data.MultisampleCount;
    public string Format => MapFormat(Data.Type);

    private void AllocateCurrent()
    {
        // A shared logical resource may also have wrappers for other physical outputs.
        if (ReferenceEquals(AbstractRenderer.Current, Renderer)) Generate();
    }

    private void OnDataChanged(object? sender, IXRPropertyChangedEventArgs change)
    {
        if (change.PropertyName is nameof(XRRenderBuffer.Width) or nameof(XRRenderBuffer.Height)
            or nameof(XRRenderBuffer.MultisampleCount) or nameof(XRRenderBuffer.Type))
            SetField(ref _invalidated, true, publishNotifications: false);
    }

    public override void Generate()
    {
        ValidateOwnerGeneration();
        if (IsRetired || Data.IsDestroyed)
            throw new InvalidOperationException("WebGPU.RenderBuffer.Retired: destroyed renderbuffer storage cannot regenerate.");
        if (Data.Width == 0 || Data.Height == 0 || Data.MultisampleCount is not (1 or 4))
            throw Unsupported("Create", "positive dimensions and exactly one or four samples are required");
        string format = MapFormat(Data.Type);
        if (_handle != 0 && !_invalidated && _width == Data.Width && _height == Data.Height &&
            _samples == Data.MultisampleCount && _format == Data.Type)
            return;
        if (_handle != 0 && Renderer.IsRecordingEngineFrame && _lastRecordedFrame == Renderer.EngineFrameSequence)
            throw Unsupported("InFrameMutation", "storage cannot change after a dependent pass was recorded");

        int handle = Renderer.CreateTexture(new BrowserTextureDescription(checked((int)Data.Width),
            checked((int)Data.Height), format, BrowserTextureUsage.RenderAttachment,
            SampleCount: checked((int)Data.MultisampleCount), Label: Data.Name ?? "Engine renderbuffer"));
        int view;
        try
        {
            view = Renderer.CreateTextureView(new BrowserTextureViewDescription(handle, 0, 1, "all",
                Data.Name ?? "Engine renderbuffer view"));
        }
        catch
        {
            Renderer.RetireEngineResourceAfterFrame(handle);
            throw;
        }

        // Publish a complete replacement only after its texture and view were accepted.
        Destroy();
        SetField(ref _handle, handle);
        SetField(ref _view, view);
        SetField(ref _width, Data.Width);
        SetField(ref _height, Data.Height);
        SetField(ref _samples, Data.MultisampleCount);
        SetField(ref _format, Data.Type);
        SetField(ref _invalidated, false, publishNotifications: false);
    }

    public int GetRenderView(int mip, int layer)
    {
        if (mip != 0 || layer != -1)
            throw Unsupported("View", "renderbuffers expose only mip zero and one non-layered attachment");
        Generate();
        return _view;
    }

    internal void MarkRecorded()
        => SetField(ref _lastRecordedFrame, Renderer.EngineFrameSequence, publishNotifications: false);

    public void MarkProduced()
    {
        MarkRecorded();
        if (_productionTicket == ulong.MaxValue)
            throw new InvalidOperationException("WebGPU.RenderBuffer.ProductionTicketExhausted: replace the renderer.");
        SetField(ref _productionTicket, _productionTicket + 1, publishNotifications: false);
        SetField(ref _lastProducedFrame, Renderer.EngineFrameSequence, publishNotifications: false);
        Renderer.RegisterProducedTexture(this);
    }

    public void CommitProducedFrame(uint frameSequence)
    {
        if (_lastProducedFrame != frameSequence) return;
        SetField(ref _lastCommittedProducedFrame, frameSequence, publishNotifications: false);
        SetField(ref _committedProductionTicket, _productionTicket, publishNotifications: false);
    }

    public bool WasProducedInFrame(uint frameSequence) => _lastProducedFrame == frameSequence;
    public bool HasCommittedProduction => IsGenerated && _lastProducedFrame != 0 &&
        _lastProducedFrame == _lastCommittedProducedFrame && _productionTicket == _committedProductionTicket;

    public override void Destroy()
    {
        if (_handle == 0) return;
        Renderer.ReleaseEngineDrawDependencies(this);
        Renderer.RetireEngineResourceAfterFrame(_view);
        Renderer.RetireEngineResourceAfterFrame(_handle);
        SetField(ref _view, 0);
        SetField(ref _handle, 0);
        SetField(ref _lastRecordedFrame, 0u, publishNotifications: false);
        SetField(ref _lastProducedFrame, 0u, publishNotifications: false);
        SetField(ref _lastCommittedProducedFrame, 0u, publishNotifications: false);
        SetField(ref _committedProductionTicket, 0u, publishNotifications: false);
        SetField(ref _invalidated, true, publishNotifications: false);
    }

    protected override void OnRetiring()
    {
        Data.PropertyChanged -= OnDataChanged;
        Data.AllocateRequested -= AllocateCurrent;
        base.OnRetiring();
    }

    private static string MapFormat(ERenderBufferStorage format) => format switch
    {
        ERenderBufferStorage.Rgba8 => "rgba8unorm",
        ERenderBufferStorage.Srgb8Alpha8 => "rgba8unorm-srgb",
        ERenderBufferStorage.Rgba16f => "rgba16float",
        ERenderBufferStorage.R16f => "r16float",
        ERenderBufferStorage.DepthComponent16 => "depth16unorm",
        ERenderBufferStorage.DepthComponent24 => "depth24plus",
        ERenderBufferStorage.DepthComponent32f => "depth32float",
        ERenderBufferStorage.Depth24Stencil8 => "depth24plus-stencil8",
        _ => throw Unsupported("Create", $"storage format '{format}' has no exact admitted WebGPU encoding"),
    };

    private static NotSupportedException Unsupported(string operation, string reason)
        => new($"WebGPU.RenderBuffer.OperationUnsupported: {operation}: {reason}.");
}
