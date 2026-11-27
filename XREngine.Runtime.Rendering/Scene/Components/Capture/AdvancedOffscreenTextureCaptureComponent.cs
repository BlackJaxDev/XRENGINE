using System.Numerics;
using System.Threading;
using System.Diagnostics.CodeAnalysis;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Scene.Transforms;
using YamlDotNet.Serialization;

namespace XREngine.Components.Lights;

/// <summary>
/// Completion-gated owner for one standalone Advanced offscreen texture.
/// Derived types declare a fixed intent so callers cannot accidentally request
/// main-view temporal or post-processing work for a capture product.
/// </summary>
public abstract partial class AdvancedOffscreenTextureCaptureComponent : XRComponent
{
    private static long s_nextOutputIdentity;
    private readonly ulong _outputIdentity = AllocateOutputIdentity();
    private readonly DrivenWorldTransform _captureTransform = new();
    private XRCamera? _captureCamera;
    private XRViewport? _viewport;
    private XRTexture2D? _outputTexture;
    private XRRenderBuffer? _depthBuffer;
    private XRFrameBuffer? _frameBuffer;
    private XRGpuFence? _writerFence;
    private AbstractRenderer? _writerRenderer;
    private bool _writerAuthored;
    private RenderCommandCollection? _writerCommands;
    private XRRenderPipelineInstance? _writerPipeline;
    private long _writerPackageGeneration;
    private bool _quarantined;
    private bool _resourcesDirty = true;
    private bool _hasCompletedCapture;
    private bool _publishedBeforeWriterCompletion;
    private XRCamera? _sourceCamera;
    private readonly object _publicationSync = new();
    private int _publicationReferences;
    private bool _writeInProgress;
    private bool _retirementRequested = true;
    private bool _releaseQueued;
    private uint _allocatedWidth;
    private uint _allocatedHeight;
    private Func<bool>? _standaloneOffscreenEmitCallback;
    private RenderOutputRequest _standaloneOutputRequest;
    private SharedOffscreenCapturePipeline? _sharedPipeline;

    /// <summary>
    /// Optional shared viewport and pipeline owned by a parent capture bank (such as
    /// <see cref="MirrorCaptureComponent"/>). When set, this slot owns only its output texture,
    /// framebuffer, and completion fences while sharing the internal render pipeline targets.
    /// </summary>
    internal SharedOffscreenCapturePipeline? SharedPipeline
    {
        get => _sharedPipeline;
        set => _sharedPipeline = value;
    }

    private ulong EffectiveOutputIdentity => _sharedPipeline?.OutputIdentity ?? _outputIdentity;

    protected abstract RenderPipelineOffscreenIntent OffscreenIntent { get; }
    protected abstract EFrameOutputKind CaptureOutputKind { get; }

    /// <summary>Reflected slots may reuse the same ownership protocol with the Default pipeline.</summary>
    protected virtual bool UsesAdvancedPipeline => true;

    /// <summary>Configures a newly allocated private viewport before its first capture.</summary>
    protected virtual void ConfigureCaptureViewport(XRViewport viewport) { }

    internal bool HasPendingCapture => HasPendingWriter;
    internal bool IsCaptureQuarantined
    {
        get
        {
            lock (_publicationSync)
                return _quarantined;
        }
    }
    internal bool ResourcesRetired
    {
        get
        {
            lock (_publicationSync)
                return _retirementRequested && _viewport is null && !_writeInProgress && !HasPendingWriter;
        }
    }

    /// <summary>Acquires a completed output generation for a consumer that will release after its GPU read completes.</summary>
    public bool TryAcquireCompletedOutput([NotNullWhen(true)] out AdvancedOffscreenTextureCaptureLease? lease)
    {
        lease = null;
        lock (_publicationSync)
        {
            XRTexture2D? texture = _outputTexture;
            if (_retirementRequested || _writeInProgress || HasPendingWriter ||
                !_hasCompletedCapture || _quarantined || texture is null)
                return false;
            if (_publicationReferences == int.MaxValue)
                throw new InvalidOperationException("Advanced offscreen publication reference overflow.");
            ++_publicationReferences;
            lease = new AdvancedOffscreenTextureCaptureLease(this, texture);
            return true;
        }
    }

    [RuntimeOnly]
    [YamlIgnore]
    public XRViewport? CaptureViewport
    {
        get
        {
            lock (_publicationSync)
                return _viewport;
        }
    }

    /// <summary>
    /// Optional source camera used for this refresh. Its render transform and
    /// projection are copied into the private capture camera; it is never used
    /// as the rendering viewport, so the capture cannot write into its source.
    /// </summary>
    [RuntimeOnly]
    [YamlIgnore]
    public XRCamera? SourceCamera
    {
        get => _sourceCamera;
        set => SetField(ref _sourceCamera, value);
    }

    private uint _width = 512u;
    public uint Width
    {
        get => _width;
        set
        {
            lock (_publicationSync)
            {
                if (!SetField(ref _width, Math.Max(1u, value)))
                    return;
                _resourcesDirty = true;
            }
        }
    }

    private uint _height = 512u;
    public uint Height
    {
        get => _height;
        set
        {
            lock (_publicationSync)
            {
                if (!SetField(ref _height, Math.Max(1u, value)))
                    return;
                _resourcesDirty = true;
            }
        }
    }

    /// <summary>
    /// Captures this component's world from its transform. Returns false when
    /// the exact output was deferred or its previous writer has not completed.
    /// </summary>
    public bool TryCapture()
    {
        if (!RuntimeEngine.IsRenderThread)
            throw new InvalidOperationException("Synchronous offscreen capture requires the render thread. Use QueueCapture from other threads.");
        lock (_publicationSync)
        {
            if (_retirementRequested || _quarantined || _writeInProgress || HasPendingWriter ||
                _publicationReferences != 0 || _canonicalLifetime is { CanWrite: false } || World is null)
                return false;
            _writeInProgress = true;
        }

        bool previousCapturePass = RuntimeEngine.Rendering.State.IsSceneCapturePass;
        bool publicationWriteStarted = false;
        RuntimeEngine.Rendering.State.IsSceneCapturePass = true;
        bool mirror = OffscreenIntent.ViewIntent == ERenderPipelineOffscreenViewIntent.Mirror;
        if (mirror)
            RuntimeEngine.Rendering.State.PushMirrorPass();
        try
        {
            EnsureResourcesCore();
            if (_resourcesDirty || _viewport is null || _frameBuffer is null || _outputTexture is null)
                return false;

            if (_canonicalLifetime is null || !_canonicalLifetime.TryBeginWrite())
                return false;
            publicationWriteStarted = true;
            // An attempted writer invalidates the previous generation even if
            // initialization throws or authoring is cleanly rejected without a fence.
            _hasCompletedCapture = false;
            _publishedBeforeWriterCompletion = false;

            UpdateCaptureCamera();
            RenderOutputRequest output = CreateOutputRequest();
            FrameOutputPacingDecision pacing = FrameOutputPacingDecision.Due(
                output.ViewKind, output.OutputKind, output.FrameId) with { Request = output };
            CollectVisibleWithStableCanonicalPublication(pacing);
            _viewport.SwapBuffers(allowScreenSpaceUISwap: false);
            bool authored;
            ERenderOutputCompletionAuthoringDisposition disposition = default;
            if (mirror &&
                RuntimeRenderingHostServices.Presentation.IsInVR &&
                AbstractRenderer.Current is { SupportsStandaloneOffscreenCapture: true } standaloneRenderer)
            {
                // Submit VR mirror captures on the standalone graphics queue path so held desktop
                // frames never delay or throttle in-world mirror updates.
                _standaloneOutputRequest = output;
                _standaloneOffscreenEmitCallback ??= EmitStandaloneOffscreenMirrorRender;
                authored = standaloneRenderer.TryExecuteStandaloneOffscreenCapture(
                    _frameBuffer,
                    _allocatedWidth,
                    _allocatedHeight,
                    _standaloneOffscreenEmitCallback,
                    out _writerFence);
            }
            else
            {
                authored = _viewport.TryRenderWithCompletion(
                    _frameBuffer,
                    in output,
                    out _writerFence,
                    out disposition);
            }
            _writerAuthored = authored;
            _writerPipeline = _viewport.RenderPipelineInstance;
            _writerCommands = _writerPipeline.ActiveMeshRenderCommands;
            _writerPackageGeneration = _writerCommands.RenderingBackendReadyPackage.PackageGeneration;
            _writerRenderer = AbstractRenderer.Current;
            if (_writerFence is null &&
                disposition == ERenderOutputCompletionAuthoringDisposition.UnfencedAfterAuthoring)
                QuarantineResources();
            else if (_writerFence is null)
                ReleaseCompletedWriterPackage();
            if (_writerFence is not null)
                _hasCompletedCapture = false;
            return authored && _writerFence is not null;
        }
        finally
        {
            _standaloneOutputRequest = default;
            if (publicationWriteStarted && _writerFence is null && !_quarantined)
                _canonicalLifetime!.EndWrite(0);
            if (mirror)
                RuntimeEngine.Rendering.State.PopMirrorPass();
            RuntimeEngine.Rendering.State.IsSceneCapturePass = previousCapturePass;
            lock (_publicationSync)
                _writeInProgress = false;
        }
    }

    private void CollectVisibleWithStableCanonicalPublication(in FrameOutputPacingDecision pacing)
    {
        if (_viewport is null)
            return;

        GPUScene? gpuScene = UsesAdvancedPipeline ? _viewport.World?.VisualScene?.GPUCommands : null;
        if (gpuScene is null)
        {
            _viewport.CollectVisible(collectMirrors: false, frameOutputPacing: pacing);
            return;
        }

        // Offscreen mirrors collect on the render thread while the update thread can publish a
        // new canonical GPU scene snapshot. Wait briefly for an active publish to finish and
        // recollect once if a publish crossed the collection window so the package never drops
        // with canonicalFrame=0 or mismatched draw identities.
        WaitBrieflyForCanonicalPublication(gpuScene);
        long epochBefore = gpuScene.AdvancedPublisherEpoch;
        _viewport.CollectVisible(collectMirrors: false, frameOutputPacing: pacing);
        RenderCommandCollection commands = _viewport.RenderPipelineInstance.ActiveMeshRenderCommands;
        bool canonicalMissing = commands.UpdatingBackendReadyPackage.CanonicalFrame.FrameId == 0UL;
        bool epochChanged = gpuScene.IsAdvancedPublicationInProgress ||
            gpuScene.AdvancedPublisherEpoch != epochBefore;
        if (!canonicalMissing && !epochChanged)
            return;

        WaitBrieflyForCanonicalPublication(gpuScene);
        if (!epochChanged && _viewport.TryFinalizePreparedCanonicalFramePackageAfterWorldSwap())
            return;

        commands.ResetUnsubmittedCapture();
        _viewport.CollectVisible(collectMirrors: false, frameOutputPacing: pacing);
    }

    private static void WaitBrieflyForCanonicalPublication(GPUScene gpuScene)
    {
        if (!gpuScene.IsAdvancedPublicationInProgress)
            return;

        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() +
            System.Diagnostics.Stopwatch.Frequency / 500;
        SpinWait spin = default;
        while (gpuScene.IsAdvancedPublicationInProgress &&
               System.Diagnostics.Stopwatch.GetTimestamp() < deadline)
            spin.SpinOnce();
    }

    private bool EmitStandaloneOffscreenMirrorRender()
    {
        if (_viewport is null || _frameBuffer is null)
            return false;

        XRFrameBuffer renderTarget = _sharedPipeline?.ScratchFrameBuffer ?? _frameBuffer;
        if (!_viewport.TryRenderStandaloneExactOutput(renderTarget, in _standaloneOutputRequest))
            return false;

        if (!ReferenceEquals(renderTarget, _frameBuffer))
        {
            AbstractRenderer.Current?.BlitFBOToFBO(
                renderTarget,
                _frameBuffer,
                EReadBufferMode.ColorAttachment0,
                colorBit: true,
                depthBit: false,
                stencilBit: false,
                linearFilter: false);
        }

        return true;
    }

    /// <summary>Polls the exact GPU writer before consumers read or resize the output.</summary>
    public bool TryCompleteCapture()
    {
        if (!RuntimeEngine.IsRenderThread)
            throw new InvalidOperationException("Offscreen writer completion must be polled on the render thread.");
        // Poll is nonblocking. Serialize disposal and the completed publication
        // with cross-thread consumers and cold diagnostic snapshots.
        lock (_publicationSync)
            return TryCompleteCaptureCore();
    }

    private bool TryCompleteCaptureCore()
    {
        if (_writerFence is not { } fence)
            return _hasCompletedCapture && !_quarantined;
        EGpuFenceSubmissionStatus submission = fence.SubmissionStatus;
        if (submission == EGpuFenceSubmissionStatus.AwaitingSubmission)
            return false;
        EGpuFenceStatus status = submission == EGpuFenceSubmissionStatus.Submitted
            ? fence.Poll()
            : EGpuFenceStatus.Failed;
        if (status == EGpuFenceStatus.Pending)
            return false;
        if (submission == EGpuFenceSubmissionStatus.Submitted && status == EGpuFenceStatus.Failed)
        {
            QuarantineResources();
            return false;
        }

        bool alreadyPublished = _publishedBeforeWriterCompletion;
        _publishedBeforeWriterCompletion = false;
        fence.Dispose();
        ReleaseCompletedWriterPackage();
        _writerFence = null;
        _writerRenderer = null;
        // A rejected command stream has no GPU writer and can be retried. It is
        // distinct from a submitted writer whose completion became unknowable.
        bool succeeded = _writerAuthored && status == EGpuFenceStatus.Signaled;
        _hasCompletedCapture = succeeded;
        _writerAuthored = false;
        if (alreadyPublished)
        {
            if (!succeeded)
                _canonicalLifetime?.Withdraw();
            return succeeded;
        }
        if (succeeded)
        {
            CaptureVersion++;
            _canonicalLifetime?.EndWrite(_outputTexture!.AdvanceCanonicalGpuContentGeneration());
        }
        else
            _canonicalLifetime?.EndWrite(0);
        return succeeded;
    }

    private void ReleaseCompletedWriterPackage()
    {
        _writerCommands?.ReleaseCompletedCanonicalFramePackage(_writerPackageGeneration);
        _writerPipeline?.ReleaseCompletedCapturePickingSource(_writerPackageGeneration);
        _writerCommands = null;
        _writerPipeline = null;
        _writerPackageGeneration = 0;
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        lock (_publicationSync)
            _retirementRequested = false;
        EnsureResources();
    }

    protected override void OnComponentDeactivated()
    {
        RequestResourceRetirement();
        base.OnComponentDeactivated();
    }

    protected override void OnDestroying()
    {
        RequestResourceRetirement();
        base.OnDestroying();
    }

    private void RequestResourceRetirement()
    {
        lock (_publicationSync)
        {
            _retirementRequested = true;
            _canonicalLifetime?.Withdraw();
            CancelStagedCapture();
            // Invalidate the old request atomically with retirement. A later
            // activation must not lose its new request to stale cancellation.
            CancelQueuedCapture();
        }
        // Native resources remain owned until the last recorded writer signals.
        // This component has no alternate renderer teardown proof, so failure is quarantined.
        WaitForWriterBeforeRelease();
    }

    private bool HasPendingWriter => _writerFence is not null;

    private void EnsureResources()
    {
        if (!RuntimeEngine.IsRenderThread)
        {
            RuntimeEngine.EnqueueMainThreadTask(EnsureResources,
                $"{GetType().Name}.EnsureResources", RenderThreadJobKind.RenderPipelineResource);
            return;
        }

        lock (_publicationSync)
        {
            if (_retirementRequested || _writeInProgress)
                return;
            _writeInProgress = true;
        }
        try
        {
            EnsureResourcesCore();
        }
        finally
        {
            lock (_publicationSync)
                _writeInProgress = false;
        }
    }

    private void EnsureResourcesCore()
    {
        // Creation and dimension mutation are serialized. Once authoring starts,
        // requests use allocated dimensions even if a later resize is requested.
        lock (_publicationSync)
            CreateCaptureResources();
    }

    private void CreateCaptureResources()
    {
        if (_retirementRequested || !_resourcesDirty || HasPendingWriter ||
            _quarantined || _publicationReferences != 0 || _canonicalLifetime is { CanWrite: false } || World is null)
            return;
        uint width = Width;
        uint height = Height;

        if (_outputTexture is not null)
            ReleaseCaptureResources();
        _depthBuffer?.Destroy();
        _frameBuffer?.Destroy();
        CreateOutputTarget(width, height);

        if (_sharedPipeline is { } shared)
        {
            _viewport = shared.EnsureResources(
                World,
                width,
                height,
                UsesAdvancedPipeline,
                OffscreenIntent,
                ConfigureCaptureViewport);
            _captureCamera = shared.CaptureCamera;
        }
        else
        {
            _captureCamera ??= new XRCamera(_captureTransform,
                new XRPerspectiveCameraParameters(90.0f, 1.0f, 0.1f, 10000.0f));
            if (UsesAdvancedPipeline)
                RuntimeEngine.RegisterAdvancedOffscreenOwner(_outputIdentity, OffscreenIntent);
            RenderPipelineRequest request = UsesAdvancedPipeline
                ? RenderPipelineRequest.AdvancedOffscreenCapture(OffscreenIntent, outputId: _outputIdentity)
                : RenderPipelineRequest.OffscreenCapture();
            _viewport ??= new XRViewport(null, width, height);
            // The output request below declares an internal extent equal to the
            // allocated texture size. Turn off the automatic internal-resolution
            // policy before the pipeline and camera are assigned: assigning a
            // pipeline applies its anti-aliasing scale (for example the TSR render
            // scale) to the viewport, and a scaled internal extent makes Vulkan
            // reject every writer receipt of this capture.
            _viewport.AllowAutomaticInternalResolution = false;
            // Capture resolution controls sampling density, not the source camera's
            // projection. Its shared lens must remain read-only on this viewport.
            _viewport.AllowCameraAspectRatioUpdates = false;
            // Assigning a camera normally adopts its main-view pipeline. This owner
            // must keep its explicit offscreen family before any camera assignment.
            _viewport.SetRenderPipelineFromCamera = false;
            if (_viewport.Width != width || _viewport.Height != height ||
                _viewport.InternalWidth != (int)width || _viewport.InternalHeight != (int)height)
                _viewport.Resize(width, height, setInternalResolution: true,
                    internalResolutionWidth: checked((int)width),
                    internalResolutionHeight: checked((int)height));
            _viewport.PipelineRequest = request;
            _viewport.RenderPipeline = RuntimeEngine.Rendering.NewRenderPipeline(request);
            _viewport.WorldInstanceOverride = World.GetRenderWorld();
            _viewport.Camera = _captureCamera;
            _viewport.AutomaticallyCollectVisible = false;
            _viewport.AutomaticallySwapBuffers = false;
            _viewport.AllowUIRender = false;
            _viewport.CullWithFrustum = true;
            ConfigureCaptureViewport(_viewport);
        }
        _allocatedWidth = width;
        _allocatedHeight = height;
        _resourcesDirty = Width != width || Height != height;
        _hasCompletedCapture = false;
    }

    private void UpdateCaptureCamera()
    {
        if (_captureCamera is not null)
            ConfigureCaptureCamera(SourceCamera, _captureCamera, _sharedPipeline?.CaptureTransform ?? _captureTransform);
    }

    /// <summary>Configures the private camera immediately before authoring an exact capture.</summary>
    protected virtual void ConfigureCaptureCamera(
        XRCamera? sourceCamera, XRCamera captureCamera, DrivenWorldTransform captureTransform)
    {
        TransformBase? sourceTransform = sourceCamera?.Transform ?? Transform;
        if (sourceTransform is null)
            return;
        if (sourceCamera is not null)
            captureCamera.Parameters = sourceCamera.Parameters;
        Matrix4x4 captureWorld = Matrix4x4.CreateFromQuaternion(sourceTransform.GetWorldRotation());
        captureWorld.Translation = sourceTransform.GetWorldTranslation();
        captureTransform.SetWorldMatrix(captureWorld, setRenderMatrixNow: true,
            childRecalcType: ELoopType.Sequential);
    }

    private RenderOutputRequest CreateOutputRequest()
    {
        RenderOutputRequest request = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.Secondary, CaptureOutputKind,
            RuntimeEngine.Rendering.State.RenderFrameId);
        ulong outputId = EffectiveOutputIdentity;
        return ConfigureOutputRequest(request with
        {
            OutputId = outputId,
            ViewFamilyId = outputId,
            // Consumers require this exact texture's writer receipt. Background
            // captures are deferrable scheduling hints and cannot reserve it.
            OutputClass = ERenderOutputClass.RequiredDependency,
            ReadinessPolicy = ERenderOutputReadinessPolicy.BlockForExact,
            WorkClass = ERenderOutputWorkClass.PresentNow,
            FallbackPolicy = ERenderOutputFallbackPolicy.None,
            CompletionRequirement = ERenderOutputCompletionRequirement.BeforeConsumer,
            ExpectedWriteAspect = OffscreenIntent.Output == ERenderPipelineOffscreenOutput.Depth
                ? ERenderOutputWriteAspect.Depth
                : ERenderOutputWriteAspect.Color,
            Target = request.Target with
            {
                StableTargetId = outputId,
                TargetGeneration = unchecked((ulong)(_viewport?.RenderPipelineInstance.ResourceGeneration ?? 0)),
                DisplayWidth = _allocatedWidth,
                DisplayHeight = _allocatedHeight,
                InternalWidth = _allocatedWidth,
                InternalHeight = _allocatedHeight,
                SampleCount = 1u,
                ViewMask = 1u,
                ExternalImageSlot = -1,
            },
        });
    }

    /// <summary>Allows optional outputs to request budget deferral without weakening writer-completion ownership.</summary>
    protected virtual RenderOutputRequest ConfigureOutputRequest(RenderOutputRequest request) => request;

    private void QuarantineResources()
    {
        lock (_publicationSync)
        {
            if (_quarantined)
                return;
            _quarantined = true;
        }
        Debug.LogWarning($"{GetType().Name} has no trustworthy GPU completion. Its capture closure is retained and refresh is disabled.");
    }

    private void WaitForWriterBeforeRelease()
    {
        lock (_publicationSync)
        {
            if (_releaseQueued || !_retirementRequested)
                return;
            _releaseQueued = true;
        }
        RuntimeEngine.AddRenderThreadCoroutine(AdvanceResourceRetirement,
            $"{GetType().Name}.WaitForWriter", RenderThreadJobKind.RenderPipelineResource);
    }

    private bool AdvanceResourceRetirement()
    {
        lock (_publicationSync)
        {
            if (!_retirementRequested || _quarantined)
            {
                _releaseQueued = false;
                return true;
            }
            if (_writeInProgress)
                return false;
            if (HasPendingWriter)
                TryCompleteCaptureCore();
            if (_quarantined)
            {
                _releaseQueued = false;
                return true;
            }
            if (HasPendingWriter || _publicationReferences != 0 || _canonicalLifetime is { CanWrite: false })
                return false;
            ReleaseCaptureResources();
            _releaseQueued = false;
            return true;
        }
    }

    internal void ReleaseOutputPublication()
    {
        bool shouldRelease;
        lock (_publicationSync)
        {
            if (_publicationReferences == 0)
                throw new InvalidOperationException("Advanced offscreen publication reference underflow.");
            --_publicationReferences;
            shouldRelease = _retirementRequested && _publicationReferences == 0;
        }
        if (shouldRelease)
            WaitForWriterBeforeRelease();
    }

    private void ReleaseCaptureResources()
    {
        // Callers hold the publication gate on the render thread. Activation
        // and lease acquisition cannot interleave the last check and teardown.
        if (_quarantined || HasPendingWriter || Volatile.Read(ref _publicationReferences) != 0 ||
            _canonicalLifetime is { CanWrite: false })
            return;
        if (_canonicalLifetime is not null && !_canonicalLifetime.TryRetire())
            throw new InvalidOperationException("Capture publication ownership changed during resource retirement.");
        if (_sharedPipeline is null)
        {
            _viewport?.Destroy();
            RuntimeEngine.UnregisterAdvancedOffscreenOwner(_outputIdentity, OffscreenIntent);
        }
        _viewport = null;
        if (_sharedPipeline is not null)
            _captureCamera = null;
        _frameBuffer?.Destroy();
        _frameBuffer = null;
        _depthBuffer?.Destroy();
        _depthBuffer = null;
        _outputTexture?.Destroy();
        _outputTexture = null;
        _canonicalLifetime = null;
        _resourcesDirty = true;
        _hasCompletedCapture = false;
    }

    internal static ulong AllocateOutputIdentity()
    {
        long identity = Interlocked.Increment(ref s_nextOutputIdentity);
        if (identity <= 0 || identity >= (1L << 44))
            throw new InvalidOperationException("Advanced offscreen output identities exhausted.");
        return 0x4F00000000000000UL | ((ulong)identity << 4);
    }
}
