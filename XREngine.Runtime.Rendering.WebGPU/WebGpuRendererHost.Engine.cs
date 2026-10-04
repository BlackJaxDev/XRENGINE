using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private Vector4 _engineClearColor = new(0, 0, 0, 1);
    private float _engineClearDepth = 1;
    private int _engineClearCommands;
    private readonly int[] _engineClearVariants = new int[4];
    private readonly WebGpuResourceRequest?[] _engineClearRequests = new WebGpuResourceRequest?[4];
    private ulong _engineClearSurfaceGeneration;
    private int _engineClearStencil;
    private BoundingRectangle? _engineRenderArea;
    private BoundingRectangle? _engineCropArea;
    private bool _engineCroppingEnabled;

    /// <summary>Uses browser-owned asynchronous device startup rather than a desktop window loop.</summary>
    public override void Initialize()
        => ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);

    /// <summary>Retires this renderer and its browser-owned device session.</summary>
    public override void CleanUp() => Dispose();

    /// <summary>Records an engine-owned canvas clear and presents through the generic executor.</summary>
    protected override void RenderFrameCallback(double delta)
    {
        WebGpuEngineFrameStatistics? statistics = BeginEngineFrameStatistics();
        long allocationStart = statistics is null ? 0 : GC.GetAllocatedBytesForCurrentThread();
        long recordingBytes = 0, submissionBytes = 0;
        bool recordingMeasured = false, frameBegun = false;
        string outcome = "Faulted";
        try
        {
            if (!double.IsFinite(delta) || delta < 0)
                throw new ArgumentOutOfRangeException(nameof(delta));
            RequireReady();
            if (!TryDescribeFrameOutput(out RenderFrameOutputDescription output))
            {
                outcome = "NoOutput";
                return;
            }

            using ThreadCurrentScope rendererScope = EnterThreadCurrentScope(this);
            using FrameOutputScope outputScope = PushFrameOutput(output);
            BeginEngineFrame();
            frameBegun = true;
            bool submitted = false;
            IRuntimeRenderWorld? world = _engineViewport?.World;
            try
            {
                RetireDestroyedMeshDeformations();
                RetireObsoleteAutoExposureHistories();
                bool ready;
                if (world is not null)
                {
                    ValidateDirectionalShadowProfile(world);
                    try
                    {
                        world.GlobalPreRender();
                        ready = RecordEngineViewport(output);
                    }
                    finally { world.GlobalPostRender(); }
                }
                else ready = RecordEngineViewport(output);
                if (ready && !_engineDrawPending)
                    ready = RecordPendingSceneLighting();
                if (ready && !_engineDrawPending)
                    ready = RecordPendingSceneCapture();
                // A consumer must never see a partially prepared producer (for example,
                // a clear without its mesh draws, or HDR without a ready output pass).
                if (!ready || _engineCommandCount == 0 || _engineDrawPending || !AreAdvancedFamiliesComplete())
                {
                    outcome = "Incomplete";
                    return;
                }
                long submissionStart = statistics is null ? 0 : GC.GetAllocatedBytesForCurrentThread();
                recordingBytes = submissionStart - allocationStart;
                recordingMeasured = true;
                try
                {
                    SubmitEngineFrame(output, ref submitted);
                    outcome = _submittedFrame ? "Presented" : "Unpresented";
                }
                finally
                {
                    if (statistics is not null)
                        submissionBytes = GC.GetAllocatedBytesForCurrentThread() - submissionStart;
                }
            }
            catch (RenderResourcePreparationPendingException)
            {
                MarkEngineDrawPending();
                outcome = "Incomplete";
            }
            finally
            {
                try
                {
                    try
                    {
                        if (!_engineAcceptanceAttempted)
                        {
                            long submissionStart = statistics is null ? 0 : GC.GetAllocatedBytesForCurrentThread();
                            if (statistics is not null && !recordingMeasured)
                            {
                                recordingBytes = submissionStart - allocationStart;
                                recordingMeasured = true;
                            }
                            try { SubmitPendingEnginePreparation(); }
                            finally
                            {
                                if (statistics is not null)
                                    submissionBytes += GC.GetAllocatedBytesForCurrentThread() - submissionStart;
                            }
                        }
                    }
                    finally { DiscardEngineViewHistory(); }
                }
                finally
                {
                    if (statistics is not null && !recordingMeasured)
                    {
                        recordingBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                        recordingMeasured = true;
                    }
                    SetField(ref _engineRecording, false, publishNotifications: false);
                    _engineProducedTextures.Clear();
                    try
                    {
                        try { EndAdvancedSceneRecording(submitted); }
                        finally { EndAuthoredIndexedRecording(submitted); }
                    }
                    finally
                    {
                        try { ArmPendingEngineFences(submitted); }
                        finally
                        {
                            try { RetireEngineDeferredResources(); }
                            finally { CompleteAcceptedSceneCapture(submitted && !_engineDrawPending); }
                        }
                    }
                }
            }
        }
        catch
        {
            outcome = "Faulted";
            throw;
        }
        finally
        {
            if (statistics is not null)
            {
                long totalBytes = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
                if (!recordingMeasured)
                    recordingBytes = totalBytes;
                CompleteEngineFrameStatistics(statistics, frameBegun, outcome, recordingBytes, submissionBytes, totalBytes);
            }
        }
    }

    private bool RecordEngineViewport(in RenderFrameOutputDescription output)
    {
        if (_engineViewport is not null) return _engineViewport.TryRender();
        PrepareEngineClear(output);
        RecordEngineClear(_engineClearCommands);
        return true;
    }

    private void PrepareEngineClear(in RenderFrameOutputDescription output, bool color = true, bool depth = true)
    {
        if (_engineClearSurfaceGeneration != output.TargetGeneration)
        {
            for (int variant = 0; variant < _engineClearVariants.Length; variant++)
            {
                RetireEngineResourceAfterFrame(_engineClearVariants[variant]);
                _engineClearVariants[variant] = 0;
                if (_engineClearRequests[variant] is { } request) CancelEngineResourceRequest(request);
                _engineClearRequests[variant] = null;
            }
            SetField(ref _engineClearSurfaceGeneration, output.TargetGeneration, publishNotifications: false);
        }
        int index = (color ? 1 : 0) | (depth ? 2 : 0);
        if (_engineClearVariants[index] != 0)
        {
            SetField(ref _engineClearCommands, _engineClearVariants[index], publishNotifications: false);
            return;
        }

        // Descriptions and arrays are cold state/generation work. Steady-state frames
        // replay the retained handle without serializing commands or allocating storage.
        BrowserFrameBufferPlan plan = new(
            [new BrowserColorAttachmentPlan(0, clear: color, store: true, Vector4.Zero)],
            new BrowserDepthStencilAttachmentPlan(-1, clearDepth: depth, depthClearValue: 1));
        int replacement = CreateEngineReplacement(this, ref _engineClearRequests[index], 7,
            "{\"label\":\"Engine canvas clear\",\"commands\":[{\"type\":\"clear\",\"engineClearValues\":true,\"pass\":" + plan.ToJson() + "}]}");
        _engineClearVariants[index] = replacement;
        SetField(ref _engineClearCommands, replacement, publishNotifications: false);
    }

    public override void ClearColor(ColorF4 color)
    {
        Vector4 value = new(color.R, color.G, color.B, color.A);
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) || !float.IsFinite(value.W))
            throw new ArgumentOutOfRangeException(nameof(color));
        SetField(ref _engineClearColor, value);
    }

    public override void ClearDepth(float value)
    {
        if (!float.IsFinite(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(nameof(value));
        SetField(ref _engineClearDepth, value);
    }

    public override void ClearStencil(int value)
        => SetField(ref _engineClearStencil, value);

    public override void Clear(bool color, bool depth, bool stencil)
    {
        RequireReady();
        if (color || depth || stencil)
        {
            BoundingRectangle? scissor = ResolveEngineDrawArea(validateViewport: false).Scissor;
            if (scissor is { } clearArea)
            {
                WebGpuFrameBuffer? target = _boundEngineFrameBuffer;
                _target.TryDescribeFrameOutput(out RenderFrameOutputDescription clearOutput);
                uint width = target?.Width ?? clearOutput.Properties.Width;
                uint height = target?.Height ?? clearOutput.Properties.Height;
                throw UnsupportedEngineOperation(nameof(Clear),
                    $"scissored attachment clears are not admitted by the engine framebuffer profile; " +
                    $"target='{(target is null ? "<canvas>" : target.Data.Name ?? "<unnamed framebuffer>")}' " +
                    $"attachment={width}x{height} effectiveScissor(top-left)=({clearArea.X},{clearArea.Y},{clearArea.Width},{clearArea.Height}) " +
                    $"crop(bottom-left)={_engineCropArea} renderArea(bottom-left)={_engineRenderArea} " +
                    $"color={color} depth={depth} stencil={stencil}");
            }
        }
        if (_boundEngineFrameBuffer is { } framebuffer)
        {
            if (stencil)
                throw UnsupportedEngineOperation(nameof(Clear), "stencil clears are not admitted by the engine framebuffer profile");
            if (!_engineRecording)
                throw new InvalidOperationException("WebGPU.FrameBuffer.ClearOutsideFrame: framebuffer clears require an active engine frame.");
            int command = framebuffer.GetClearCommand(color, depth, _engineClearColor, _engineClearDepth);
            RecordEngineClear(command);
            framebuffer.MarkRecorded(color, depth);
            return;
        }
        if (stencil)
            throw UnsupportedEngineOperation(nameof(Clear), "the canvas has no stencil attachment");
        if (!color && !depth) return;
        if (_engineRecording && CurrentFrameOutput is { } output)
        {
            PrepareEngineClear(output, color, depth);
            RecordEngineClear(_engineClearCommands);
            if (color)
                MarkEngineViewHistoryCanvasWrite(in output);
        }
    }

    public override void BindFrameBuffer(EFramebufferTarget fboTarget, XRFrameBuffer? fbo)
    {
        RequireReady();
        if (fboTarget == EFramebufferTarget.ReadFramebuffer)
            throw UnsupportedEngineOperation(nameof(BindFrameBuffer), "synchronous framebuffer reads are unavailable");
        if (fboTarget is not (EFramebufferTarget.DrawFramebuffer or EFramebufferTarget.Framebuffer))
            throw UnsupportedEngineOperation(nameof(BindFrameBuffer), "the framebuffer target is unsupported");
        WebGpuFrameBuffer? wrapper = fbo is null ? null :
            (WebGpuFrameBuffer)GetOrCreateAPIRenderObject(fbo, generateNow: true)!;
        wrapper?.EnsureCurrent();
        SetField(ref _boundEngineFrameBuffer, wrapper, publishNotifications: false);
    }

    public override void SetRenderArea(BoundingRectangle region)
    {
        RequireReady();
        ValidatePositiveArea(region);
        SetField(ref _engineRenderArea, region, publishNotifications: false);
    }

    public override void ClearRenderArea()
        => SetField(ref _engineRenderArea, null, publishNotifications: false);

    public override void CropRenderArea(BoundingRectangle region)
    {
        RequireReady();
        if (region.Width < 0 || region.Height < 0)
            throw new ArgumentOutOfRangeException(nameof(region), "A crop area cannot have negative extents.");
        SetField(ref _engineCropArea, region, publishNotifications: false);
    }

    public override void SetCroppingEnabled(bool enabled)
        => SetField(ref _engineCroppingEnabled, enabled, publishNotifications: false);

    private static void ValidatePositiveArea(BoundingRectangle region)
    {
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(region), "A render area requires positive extents and nonnegative coordinates.");
    }

    /// <summary>Validates scoped viewport state when its render target is known, after binding transitions.</summary>
    internal void ValidateEngineDrawArea(bool validateViewport = true)
        => _ = ResolveEngineDrawArea(validateViewport);

    /// <summary>Resolves engine bottom-left draw regions against the bound target into WebGPU top-left pixels.</summary>
    internal (BoundingRectangle? Viewport, BoundingRectangle? Scissor) ResolveEngineDrawArea(bool validateViewport = true)
    {
        WebGpuFrameBuffer? framebuffer = GetBoundEngineFrameBuffer();
        uint width, height;
        if (framebuffer is not null)
        {
            width = framebuffer.Width;
            height = framebuffer.Height;
        }
        else if (_target.TryDescribeFrameOutput(out RenderFrameOutputDescription output))
        {
            width = output.Properties.Width;
            height = output.Properties.Height;
        }
        else
            throw UnsupportedEngineOperation(nameof(SetRenderArea), "no drawable output is available");
        int targetWidth = checked((int)width);
        int targetHeight = checked((int)height);
        BoundingRectangle? viewport = null;
        BoundingRectangle? scissor = null;
        int viewportLeft = 0, viewportBottom = 0, viewportRight = targetWidth, viewportTop = targetHeight;
        if (validateViewport && _engineRenderArea is { } render)
        {
            long right = (long)render.X + render.Width;
            long top = (long)render.Y + render.Height;
            if (render.X < 0 || render.Y < 0 || render.Width <= 0 || render.Height <= 0 ||
                right > targetWidth || top > targetHeight)
                throw UnsupportedEngineOperation(nameof(SetRenderArea), "the viewport must fit inside the bound attachment");
            viewportLeft = render.X;
            viewportBottom = render.Y;
            viewportRight = (int)right;
            viewportTop = (int)top;
            if (viewportLeft != 0 || viewportBottom != 0 || viewportRight != targetWidth || viewportTop != targetHeight)
                viewport = new BoundingRectangle(viewportLeft, targetHeight - viewportTop, render.Width, render.Height);
        }
        if (_engineCroppingEnabled)
        {
            if (_engineCropArea is not { } crop)
                throw UnsupportedEngineOperation(nameof(CropRenderArea), "cropping is enabled without a crop region");
            // Use 64-bit edges before clamping so off-target and empty UI clips cannot wrap.
            int left = (int)Math.Clamp((long)crop.MinX, viewportLeft, viewportRight);
            int bottom = (int)Math.Clamp((long)crop.MinY, viewportBottom, viewportTop);
            int right = (int)Math.Clamp((long)crop.MinX + crop.Width, viewportLeft, viewportRight);
            int top = (int)Math.Clamp((long)crop.MinY + crop.Height, viewportBottom, viewportTop);
            if (left != 0 || bottom != 0 || right != targetWidth || top != targetHeight)
                scissor = new BoundingRectangle(left, targetHeight - top, right - left, top - bottom);
        }
        return (viewport, scissor);
    }

    protected override AbstractRenderAPIObject CreateAPIRenderObject(GenericRenderObject renderObject)
        => renderObject switch
        {
            XRDataBuffer buffer => new WebGpuDataBuffer(this, buffer),
            XRDataBufferView view => new WebGpuDataBufferView(this, view),
            XRTexture2D texture => new WebGpuTexture2D(this, texture),
            XRTextureViewBase view => new WebGpuTextureView(this, view),
            XRTexture2DArray array => new WebGpuTexture2DArray(this, array),
            XRTextureCube cube => new WebGpuTextureCube(this, cube),
            XRRenderBuffer renderbuffer => new WebGpuRenderBuffer(this, renderbuffer),
            XRFrameBuffer framebuffer => new WebGpuFrameBuffer(this, framebuffer),
            XRRenderProgram program => new WebGpuRenderProgram(this, program),
            XRMaterial material => new WebGpuMaterial(this, material),
            XRMeshRenderer.BaseVersion mesh => new WebGpuMeshRenderer(this, mesh),
            _ => throw UnsupportedEngineOperation(nameof(CreateAPIRenderObject),
                $"engine resource type '{renderObject.GetType().FullName}' has no WebGPU wrapper"),
        };

    public override ScreenshotReadbackStatus GetScreenshotReadbackStatus()
        => new() { Backend = "WebGPU", Supported = TryDescribeFrameOutput(out _),
            NonBlockingGpuWait = true, QueueCapacity = 16 };

    public override void PrepareForApiObjectTeardown()
    {
        DestroyVertexlessIndirectDraws();
        // Commands retain buffers, programs and layouts. Release those dependency
        // edges before the unordered shared wrapper cache retires its objects.
        foreach (AbstractRenderAPIObject api in RenderObjectCache.Values)
            if (api is WebGpuMeshRenderer mesh)
                mesh.DestroyDraws();
            else if (api is WebGpuFrameBuffer framebuffer)
                framebuffer.ReleaseColorResolvesUsing(framebuffer);
    }

    public override void WaitForGpu()
        => throw UnsupportedEngineOperation(nameof(WaitForGpu),
            "browser owner threads cannot block; use CompleteSubmittedWorkAsync");

    internal void RetireEngineResource(int handle)
    {
        if (!_resources.Contains(handle))
            return;
        if (State == BrowserRendererState.Ready && (HasPendingEngineTextureCopySource(handle) ||
            _engineRecording && (HasPendingEngineBufferUpload(handle) || HasPendingEnginePreparation(handle))))
        {
            if (!_engineDeferredReleases.Contains(handle)) _engineDeferredReleases.Add(handle);
            return;
        }
        CancelEngineResourceDependents(handle);
        CompactEnginePreparation(handle);
        if (!_engineRecording) DiscardRetiredBufferUploads(handle);
        if (State == BrowserRendererState.Ready)
            WebGpuImports.RetireResource(_session, handle);
        _resources.Remove(handle);
        ForgetEngineResourceHandle(handle);
    }

    private void RetireEngineDeferredResources()
    {
        int retained = 0;
        for (int index = 0; index < _engineDeferredReleases.Count; index++)
        {
            int handle = _engineDeferredReleases[index];
            if (State == BrowserRendererState.Ready && HasPendingEngineTextureCopySource(handle))
                _engineDeferredReleases[retained++] = handle;
            else RetireEngineResource(handle);
        }
        if (retained < _engineDeferredReleases.Count)
            _engineDeferredReleases.RemoveRange(retained, _engineDeferredReleases.Count - retained);
    }

    /// <summary>Keeps handles referenced by an already recorded frame alive through submission.</summary>
    internal void RetireEngineResourceAfterFrame(int handle)
    {
        if (handle == 0) return;
        if (_engineRecording) _engineDeferredReleases.Add(handle);
        else RetireEngineResource(handle);
    }

    internal void ReleaseEngineDrawDependencies(AbstractRenderAPIObject resource)
    {
        ReleaseVertexlessIndirectDrawsUsing(resource);
        if (_authoredIndexedSlots is not null)
            foreach (WebGpuAuthoredIndexedFrameSlot slot in _authoredIndexedSlots) slot.ReleaseDrawUsing(resource);
        // Authored aliases must not retain a retired physical generation merely
        // because that alias is no longer sampled. Nested views are not admitted.
        foreach (AbstractRenderAPIObject api in RenderObjectCache.Values)
            if (api is WebGpuTextureView view && view.DependsOn(resource))
                view.Destroy();
        // Release recorded commands before retiring the groups they retain. Both
        // remain alive through submission if the current frame already used them.
        foreach (AbstractRenderAPIObject api in RenderObjectCache.Values)
            if (api is WebGpuMeshRenderer mesh)
                mesh.ReleaseDrawsUsing(resource);
            else if (api is WebGpuFrameBuffer framebuffer)
                framebuffer.ReleaseColorResolvesUsing(resource);
        foreach (AbstractRenderAPIObject api in RenderObjectCache.Values)
            if (api is WebGpuRenderProgram program)
            {
                program.ReleaseIndirectComputeCommandsUsing(resource);
                program.ReleaseBindingSetsUsing(resource);
            }
    }

    /// <summary>Invalidates physical storage descriptor users without discarding immutable pipelines.</summary>
    internal void ReleaseEngineStorageGeneration(AbstractRenderAPIObject resource, int handle)
    {
        if (_authoredIndexedSlots is not null)
            foreach (WebGpuAuthoredIndexedFrameSlot slot in _authoredIndexedSlots) slot.ReleaseCommandsUsingHandle(resource, handle);
        foreach (WebGpuMeshDraw draw in _vertexlessIndirectDraws.Values)
            draw.ReleaseCommandsUsingHandle(resource, handle);
        foreach (AbstractRenderAPIObject api in RenderObjectCache.Values)
            if (api is WebGpuMeshRenderer mesh)
                mesh.ReleaseStorageCommandsUsingHandle(resource, handle);
        foreach (AbstractRenderAPIObject api in RenderObjectCache.Values)
            if (api is WebGpuRenderProgram program)
            {
                program.ReleaseIndirectComputeCommandsUsing(resource, handle);
                program.ReleaseBindingSetsUsingHandle(resource, handle);
            }
    }

    /// <summary>Retires raster commands for one descriptor generation before its groups retire.</summary>
    internal void ReleaseEngineMeshCommandsUsingBindingSet(WebGpuRenderProgram program, WebGpuBindingSet bindings)
    {
        if (_authoredIndexedSlots is not null)
            foreach (WebGpuAuthoredIndexedFrameSlot slot in _authoredIndexedSlots) slot.ReleaseCommandUsing(program, bindings);
        foreach (WebGpuMeshDraw draw in _vertexlessIndirectDraws.Values)
            draw.ReleaseCommandUsing(program, bindings);
        foreach (AbstractRenderAPIObject api in RenderObjectCache.Values)
            if (api is WebGpuMeshRenderer mesh)
                mesh.ReleaseCommandUsing(program, bindings);
    }

    private static NotSupportedException UnsupportedEngineOperation(string operation,
        string reason = "the selected engine renderer profile does not implement this operation")
        => new($"WebGPU.Renderer.OperationUnsupported: {operation}: {reason}.");
}
