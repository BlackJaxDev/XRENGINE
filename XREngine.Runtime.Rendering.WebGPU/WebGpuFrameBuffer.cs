using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

/// <summary>Retains an engine framebuffer's exact attachment-view plan for one renderer generation.</summary>
public sealed partial class WebGpuFrameBuffer : WebGpuObject<XRFrameBuffer>
{
    private readonly int[] _clearCommands = new int[4];
    private (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[] _targets = [];
    private AbstractRenderAPIObject[] _textures = [];
    private int[] _views = [];
    private int _validationCommand;
    private BrowserFrameBufferPlan? _plan;
    private string?[] _colorFormats = [];
    private string? _depthFormat;
    private uint _width;
    private uint _height;
    private uint _sampleCount;
    private Vector4 _clearColor;
    private float _clearDepth;
    private bool _invalidated = true;
    private ulong _revision;

    public WebGpuFrameBuffer(WebGpuRendererHost renderer, XRFrameBuffer data) : base(renderer, data)
    {
        data.Resized += Invalidate;
        data.PropertyChanged += OnDataChanged;
        data.BindRequested += Bind;
        data.BindForWriteRequested += BindForWriting;
        data.BindForReadRequested += BindForReading;
        data.UnbindRequested += Unbind;
        data.UnbindFromWriteRequested += UnbindFromWriting;
        data.UnbindFromReadRequested += UnbindFromReading;
    }

    public override bool IsGenerated => _plan is not null;
    public ulong Revision => _revision;
    public BrowserFrameBufferPlan Plan => _plan ?? throw new InvalidOperationException("WebGPU.FrameBuffer.PlanPending: generate the framebuffer first.");
    public ReadOnlySpan<string?> ColorFormats => _colorFormats;
    public string? DepthFormat => _depthFormat;
    public uint Width => _width;
    public uint Height => _height;
    public uint SampleCount => _sampleCount;
    public bool HasColor => _colorFormats.Length != 0;
    public bool HasDepth => _depthFormat is not null;
    public override nint GetHandle() => 0;

    private void Invalidate() => _invalidated = true;

    private void Bind() => BindCurrent(EFramebufferTarget.Framebuffer, Data);
    private void BindForWriting() => BindCurrent(EFramebufferTarget.DrawFramebuffer, Data);
    private void BindForReading() => BindCurrent(EFramebufferTarget.ReadFramebuffer, Data);
    private void Unbind() => BindCurrent(EFramebufferTarget.Framebuffer, null);
    private void UnbindFromWriting() => BindCurrent(EFramebufferTarget.DrawFramebuffer, null);
    private void UnbindFromReading() => BindCurrent(EFramebufferTarget.ReadFramebuffer, null);

    private void BindCurrent(EFramebufferTarget target, XRFrameBuffer? framebuffer)
    {
        // One shared resource can have wrappers in multiple output generations.
        // Only the renderer executing this scope may change its binding state.
        if (ReferenceEquals(AbstractRenderer.Current, Renderer))
            Renderer.BindFrameBuffer(target, framebuffer);
    }

    private void OnDataChanged(object? sender, IXRPropertyChangedEventArgs change)
    {
        if (change.PropertyName is nameof(XRFrameBuffer.Targets) or nameof(XRFrameBuffer.DrawBuffers))
            Invalidate();
    }

    public override void Generate() => EnsureCurrent();

    public void EnsureCurrent()
    {
        ValidateOwnerGeneration();
        if (IsRetired || Data.IsDestroyed)
            throw new InvalidOperationException("WebGPU.FrameBuffer.Retired: a destroyed framebuffer cannot be rebound.");
        var targets = Data.Targets;
        if (targets is null || targets.Length == 0)
            throw Unsupported("Create", "the framebuffer has no declared render attachments");
        bool current = !_invalidated && _plan is not null && targets.Length == _targets.Length;
        if (current)
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] != _targets[i] || ResolveView(targets[i].Target, targets[i].MipLevel, targets[i].LayerIndex) != _views[i])
                {
                    current = false;
                    break;
                }
            }
        if (current) return;

        Destroy();
        try
        {
            AbstractRenderAPIObject[] textures = new AbstractRenderAPIObject[targets.Length];
            int[] views = new int[targets.Length];
            var snapshot = new (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[targets.Length];
            string?[] formats = new string?[8];
            string? depthFormat = null;
            uint width = 0, height = 0, samples = 0;
            int colorCount = 0;
            for (int i = 0; i < targets.Length; i++)
            {
                var target = targets[i];
                var (api, view, textureWidth, textureHeight, textureSamples, format) = DescribeAttachment(target.Target,
                    target.MipLevel, target.LayerIndex);
                uint mipWidth = Math.Max(1u, textureWidth >> target.MipLevel);
                uint mipHeight = Math.Max(1u, textureHeight >> target.MipLevel);
                if (width != 0 && (width != mipWidth || height != mipHeight || samples != textureSamples))
                    throw Unsupported("Create", "all attachments must have identical extents and sample counts");
                width = mipWidth;
                height = mipHeight;
                samples = textureSamples;
                int slot = ColorSlot(target.Attachment);
                if (slot >= 0)
                {
                    if (format is not ("rgba8unorm" or "rgba8unorm-srgb" or "rgba16float" or "r16float") || formats[slot] is not null)
                        throw Unsupported("Create", "color slots require distinct exact RGBA8, RGBA16F or R16F texture views");
                    formats[slot] = format;
                    colorCount = Math.Max(colorCount, slot + 1);
                }
                else
                {
                    if (depthFormat is not null || target.Attachment is not (EFrameBufferAttachment.DepthAttachment or EFrameBufferAttachment.DepthStencilAttachment))
                        throw Unsupported("Create", "one depth or combined depth/stencil attachment is admitted");
                    if (target.Attachment == EFrameBufferAttachment.DepthStencilAttachment && format != "depth24plus-stencil8" ||
                        target.Attachment == EFrameBufferAttachment.DepthAttachment && format is not ("depth16unorm" or "depth24plus" or "depth32float"))
                        throw Unsupported("Create", "the depth attachment kind does not match its exact texture format");
                    depthFormat = format;
                }
                textures[i] = api;
                views[i] = view;
                snapshot[i] = target;
            }
            BrowserFrameBufferPlan plan = CreatePlan(false, false, default, 1);
            int validationCommand = Renderer.PrepareCommands(
                "{\"label\":\"Engine framebuffer validation\",\"commands\":[{\"type\":\"clear\",\"pass\":" + plan.ToJson() + "}]}");
            _targets = snapshot;
            _textures = textures;
            _views = views;
            _colorFormats = formats.AsSpan(0, colorCount).ToArray();
            _depthFormat = depthFormat;
            _width = width;
            _height = height;
            _sampleCount = samples;
            _plan = plan;
            _validationCommand = validationCommand;
            _revision++;
            _invalidated = false;
            Data.IsLastCheckComplete = true;
        }
        catch
        {
            Data.IsLastCheckComplete = false;
            Destroy();
            throw;
        }
    }

    private BrowserFrameBufferPlan CreatePlan(bool clearColor, bool clearDepth, Vector4 color, float depth)
        => BrowserFrameBufferAdapter.FromXRFrameBuffer(Data, ResolveView,
            (slot, view) => new BrowserColorAttachmentPlan(view, clearColor, true, color),
            (view, attachment) => new BrowserDepthStencilAttachmentPlan(view,
                hasDepth: true, hasStencil: attachment == EFrameBufferAttachment.DepthStencilAttachment,
                clearDepth: clearDepth, depthClearValue: depth, clearStencil: false));

    private int ResolveView(IFrameBufferAttachement attachment, int mip, int layer)
        => DescribeAttachment(attachment, mip, layer).View;

    private (AbstractRenderAPIObject Owner, int View, uint Width, uint Height, uint Samples, string Format)
        DescribeAttachment(IFrameBufferAttachement attachment, int mip, int layer)
        => attachment switch
        {
            XRRenderBuffer renderbuffer => Describe((WebGpuRenderBuffer)Renderer.GetOrCreateAPIRenderObject(renderbuffer, generateNow: true)!, mip, layer),
            XRTexture2D texture => Describe((WebGpuTexture2D)Renderer.GetOrCreateAPIRenderObject(texture, generateNow: true)!, mip, layer),
            XRTexture2DArray array => Describe((WebGpuTexture2DArray)Renderer.GetOrCreateAPIRenderObject(array, generateNow: true)!, mip, layer),
            XRTextureCube cube => Describe((WebGpuTextureCube)Renderer.GetOrCreateAPIRenderObject(cube, generateNow: true)!, mip, layer),
            _ => throw Unsupported("View", $"attachment type '{attachment.GetType().Name}' has no exact WebGPU view"),
        };

    private static (AbstractRenderAPIObject Owner, int View, uint Width, uint Height, uint Samples, string Format)
        Describe(WebGpuRenderBuffer renderbuffer, int mip, int layer)
        => (renderbuffer, renderbuffer.GetRenderView(mip, layer), renderbuffer.Width, renderbuffer.Height,
            renderbuffer.SampleCount, renderbuffer.Format);

    private static (AbstractRenderAPIObject Owner, int View, uint Width, uint Height, uint Samples, string Format)
        Describe(WebGpuTexture2D texture, int mip, int layer)
        => (texture, texture.GetRenderView(mip, layer), texture.Width, texture.Height, texture.SampleCount, texture.Format);

    private static (AbstractRenderAPIObject Owner, int View, uint Width, uint Height, uint Samples, string Format)
        Describe<T>(WebGpuLayeredTexture<T> texture, int mip, int layer) where T : XRTexture
        => (texture, texture.GetRenderView(mip, layer), texture.Width, texture.Height, texture.SampleCount, texture.Format);

    internal void MarkRecorded(bool color = true, bool depth = true)
    {
        for (int i = 0; i < _textures.Length; i++)
        {
            bool writes = ColorSlot(_targets[i].Attachment) >= 0 ? color : depth;
            if (writes && _textures[i] is IWebGpuProducedTexture produced) produced.MarkProduced();
            else MarkAttachmentRecorded(_textures[i]);
        }
    }

    public bool DependsOn(AbstractRenderAPIObject resource)
    {
        if (ReferenceEquals(this, resource)) return true;
        foreach (AbstractRenderAPIObject texture in _textures)
            if (ReferenceEquals(texture, resource)) return true;
        return false;
    }

    public int GetClearCommand(bool color, bool depth, Vector4 clearColor, float clearDepth)
    {
        EnsureCurrent();
        if ((!color && !depth) || color && !HasColor || depth && !HasDepth)
            throw Unsupported("Clear", "clear flags must select an attached color or depth aspect");
        if (_clearColor != clearColor || _clearDepth != clearDepth)
        {
            RetireClearCommands();
            _clearColor = clearColor;
            _clearDepth = clearDepth;
        }
        int index = (color ? 1 : 0) | (depth ? 2 : 0);
        if (_clearCommands[index] != 0) return _clearCommands[index];
        BrowserFrameBufferPlan plan = CreatePlan(color, depth, clearColor, clearDepth);
        _clearCommands[index] = Renderer.PrepareCommands(
            "{\"label\":\"Engine framebuffer clear\",\"commands\":[{\"type\":\"clear\",\"pass\":" + plan.ToJson() + "}]}");
        return _clearCommands[index];
    }

    private void RetireClearCommands()
    {
        for (int i = 0; i < _clearCommands.Length; i++)
        {
            if (_clearCommands[i] != 0) Renderer.RetireEngineResourceAfterFrame(_clearCommands[i]);
            _clearCommands[i] = 0;
        }
    }

    public override void Destroy()
    {
        if (_plan is not null) Renderer.ReleaseEngineDrawDependencies(this);
        ReleaseColorResolvesUsing(this);
        RetireClearCommands();
        if (_validationCommand != 0) Renderer.RetireEngineResourceAfterFrame(_validationCommand);
        _validationCommand = 0;
        _plan = null;
        _targets = [];
        _textures = [];
        _views = [];
        _colorFormats = [];
        _depthFormat = null;
        _width = _height = _sampleCount = 0;
        _invalidated = true;
    }

    protected override void OnRetiring()
    {
        Data.Resized -= Invalidate;
        Data.PropertyChanged -= OnDataChanged;
        Data.BindRequested -= Bind;
        Data.BindForWriteRequested -= BindForWriting;
        Data.BindForReadRequested -= BindForReading;
        Data.UnbindRequested -= Unbind;
        Data.UnbindFromWriteRequested -= UnbindFromWriting;
        Data.UnbindFromReadRequested -= UnbindFromReading;
        base.OnRetiring();
    }

    private static int ColorSlot(EFrameBufferAttachment attachment) => attachment switch
    {
        EFrameBufferAttachment.ColorAttachment0 => 0,
        EFrameBufferAttachment.ColorAttachment1 => 1,
        EFrameBufferAttachment.ColorAttachment2 => 2,
        EFrameBufferAttachment.ColorAttachment3 => 3,
        EFrameBufferAttachment.ColorAttachment4 => 4,
        EFrameBufferAttachment.ColorAttachment5 => 5,
        EFrameBufferAttachment.ColorAttachment6 => 6,
        EFrameBufferAttachment.ColorAttachment7 => 7,
        _ => -1,
    };

    private static NotSupportedException Unsupported(string operation, string reason)
        => new($"WebGPU.FrameBuffer.OperationUnsupported: {operation}: {reason}.");
}
