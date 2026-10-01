using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private Vector4 _engineClearColor = new(0, 0, 0, 1);
    private float _engineClearDepth = 1;
    private bool _engineClearPlanDirty = true;
    private int _engineClearCommands;
    private ulong _engineClearSurfaceGeneration;

    /// <summary>Uses browser-owned asynchronous device startup rather than a desktop window loop.</summary>
    public override void Initialize()
        => ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);

    /// <summary>Retires this renderer and its browser-owned device session.</summary>
    public override void CleanUp() => Dispose();

    /// <summary>Records an engine-owned canvas clear and presents through the generic executor.</summary>
    protected override void RenderFrameCallback(double delta)
    {
        if (!double.IsFinite(delta) || delta < 0)
            throw new ArgumentOutOfRangeException(nameof(delta));
        RequireReady();
        if (!TryDescribeFrameOutput(out RenderFrameOutputDescription output))
            return;

        using ThreadCurrentScope rendererScope = EnterThreadCurrentScope(this);
        using FrameOutputScope outputScope = PushFrameOutput(output);
        BeginEngineFrame();
        try
        {
            if (_engineViewport is null)
            {
                PrepareEngineClear(output);
                RecordEngineCommands(_engineClearCommands, []);
            }
            else if (!_engineViewport.TryRender())
            {
                return;
            }
            if (_engineCommandCount == 0)
                return;
            SubmitEngineFrame(output);
        }
        finally
        {
            SetField(ref _engineRecording, false, publishNotifications: false);
            for (int i = 0; i < _engineDeferredReleases.Count; i++)
                RetireEngineResource(_engineDeferredReleases[i]);
            _engineDeferredReleases.Clear();
        }
    }

    private void PrepareEngineClear(in RenderFrameOutputDescription output)
    {
        if (!_engineClearPlanDirty && _engineClearCommands != 0 &&
            _engineClearSurfaceGeneration == output.TargetGeneration)
            return;

        // Descriptions and arrays are cold state/generation work. Steady-state frames
        // replay the retained handle without serializing commands or allocating storage.
        BrowserFrameBufferPlan plan = new(
            [new BrowserColorAttachmentPlan(0, clear: true, store: true, _engineClearColor)],
            new BrowserDepthStencilAttachmentPlan(-1, depthClearValue: _engineClearDepth));
        int replacement = PrepareCommands(
            "{\"label\":\"Engine canvas clear\",\"commands\":[{\"type\":\"clear\",\"pass\":" + plan.ToJson() + "}]}");
        int previous = _engineClearCommands;
        SetField(ref _engineClearCommands, replacement);
        SetField(ref _engineClearSurfaceGeneration, output.TargetGeneration);
        SetField(ref _engineClearPlanDirty, false);
        if (previous != 0)
        {
            if (_engineRecording) _engineDeferredReleases.Add(previous);
            else RetireEngineResource(previous);
        }
    }

    public override void ClearColor(ColorF4 color)
    {
        Vector4 value = new(color.R, color.G, color.B, color.A);
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) || !float.IsFinite(value.W))
            throw new ArgumentOutOfRangeException(nameof(color));
        if (SetField(ref _engineClearColor, value))
            SetField(ref _engineClearPlanDirty, true);
    }

    public override void ClearDepth(float value)
    {
        if (!float.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (SetField(ref _engineClearDepth, value))
            SetField(ref _engineClearPlanDirty, true);
    }

    public override void Clear(bool color, bool depth, bool stencil)
    {
        RequireReady();
        if (!color || !depth || stencil)
            throw UnsupportedEngineOperation(nameof(Clear), "only a full color/depth canvas clear is admitted");
        if (_engineRecording && CurrentFrameOutput is { } output)
        {
            PrepareEngineClear(output);
            RecordEngineCommands(_engineClearCommands, []);
        }
    }

    public override void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo)
    {
        RequireReady();
        if (fbo is not null)
            throw UnsupportedEngineOperation(nameof(BindFrameBuffer), "engine framebuffer wrappers are not available");
    }

    public override void SetRenderArea(BoundingRectangle region)
    {
        RequireReady();
        if (!_target.TryDescribeFrameOutput(out RenderFrameOutputDescription output) ||
            region.X != 0 || region.Y != 0 ||
            region.Width != output.Properties.Width || region.Height != output.Properties.Height)
            throw UnsupportedEngineOperation(nameof(SetRenderArea), "only the complete drawable canvas is admitted");
    }

    public override void CropRenderArea(BoundingRectangle region) => SetRenderArea(region);

    public override void SetCroppingEnabled(bool enabled)
    {
        if (enabled)
            throw UnsupportedEngineOperation(nameof(SetCroppingEnabled), "scissored engine draws are not available");
    }

    protected override AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject)
        => renderObject switch
        {
            XRDataBuffer buffer => new WebGpuDataBuffer(this, buffer),
            XRRenderProgram program => new WebGpuRenderProgram(this, program),
            XRMaterial material => new WebGpuMaterial(this, material),
            XRMeshRenderer.BaseVersion mesh => new WebGpuMeshRenderer(this, mesh),
            _ => throw UnsupportedEngineOperation(nameof(CreateAPIRenderObject),
                $"engine resource type '{renderObject.GetType().FullName}' has no WebGPU wrapper"),
        };

    public override AdvancedRenderPipelineCapabilities GetAdvancedRenderPipelineCapabilities()
        => AdvancedRenderPipelineCapabilities.UnsupportedBackend with
        {
            Backend = RuntimeGraphicsApiKind.WebGPU,
            RendererAvailable = State == BrowserRendererState.Ready,
        };

    public override ScreenshotReadbackStatus GetScreenshotReadbackStatus()
        => new() { Backend = "WebGPU", Supported = false, NonBlockingGpuWait = true };

    public override void PrepareForApiObjectTeardown()
    {
        // Commands retain buffers, programs and layouts. Release those dependency
        // edges before the unordered shared wrapper cache retires its objects.
        foreach (AbstractRenderAPIObject api in RenderObjectCache.Values)
            if (api is WebGpuMeshRenderer mesh)
                mesh.DestroyDraws();
    }

    public override void WaitForGpu()
        => throw UnsupportedEngineOperation(nameof(WaitForGpu),
            "browser owner threads cannot block; use CompleteSubmittedWorkAsync");

    internal void RetireEngineResource(int handle)
    {
        if (!_resources.Contains(handle))
            return;
        if (State == BrowserRendererState.Ready)
            WebGpuImports.RetireResource(_session, handle);
        _resources.Remove(handle);
    }

    internal void ReleaseEngineDrawDependencies(AbstractRenderAPIObject resource)
    {
        foreach (AbstractRenderAPIObject api in RenderObjectCache.Values)
            if (api is WebGpuMeshRenderer mesh)
                mesh.ReleaseDrawsUsing(resource);
    }

    private static NotSupportedException UnsupportedEngineOperation(string operation,
        string reason = "the selected engine renderer profile does not implement this operation")
        => new($"WebGPU.Renderer.OperationUnsupported: {operation}: {reason}.");
}
