using System.Collections.Concurrent;
using System.Threading;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Components.Lights;

public partial class SceneCaptureComponent
{
    /// <summary>
    /// Identifies the scheduler-visible product authored by this capture type.
    /// </summary>
    protected virtual EFrameOutputKind CaptureOutputKind =>
        EFrameOutputKind.SceneCapture;

    private static long s_nextCaptureIdentity;
    private static long s_sharedCaptureAuthoringFrameId = long.MinValue;
    private static readonly ConcurrentDictionary<AbstractRenderer, byte> s_quarantinedCaptureRenderers = new();
    private static readonly ConcurrentDictionary<SceneCaptureComponent, AbstractRenderer> s_quarantinedCaptures = new();
    private readonly ulong _captureIdentity = AllocateCaptureIdentity();
    private XRGpuFence? _captureFaceFence;
    private AbstractRenderer? _captureFaceRenderer;
    private int _pendingCaptureFace = -1;
    private bool _pendingCaptureFaceAuthored;
    private RenderCommandCollection? _captureFaceCommands;
    private XRRenderPipelineInstance? _captureFacePipeline;
    private long _captureFacePackageGeneration;
    private uint _pendingCaptureSourceGeneration;
    private uint _captureSourceGeneration;
    private int _completedCaptureFaceCount;
    private bool _captureResourcesQuarantined;
    private bool _captureCubemapMipmapsGenerated;
    private XRGpuFence? _captureEncodingFence;
    private AbstractRenderer? _captureEncodingRenderer;
    private bool _captureEncodingAuthored;
    private XRRenderPipelineInstance? _captureEncodingPipeline;
    private Func<bool>? _captureEncodingProducer;
    private bool _captureReleaseQueued;
    private bool _releaseCaptureCubemapPending;
    private bool _releaseCaptureOctahedralPending;
    private uint _releaseCaptureSourceGeneration;

    /// <summary>In-flight source writers prevent resizing, retargeting, or releasing the capture closure.</summary>
    protected bool HasPendingCaptureWriter => _captureFaceFence is not null || _captureEncodingFence is not null || HasPendingCaptureConsumer;

    /// <summary>A derived capture may retain the source while a convolution reads it.</summary>
    protected virtual bool HasPendingCaptureConsumer => false;

    private bool DrainPendingCaptureRelease()
    {
        SettleCaptureFencesForRelease();
        if (_captureResourcesQuarantined)
            return true;
        if (HasPendingCaptureWriter)
            return false;
        bool releaseCube = _releaseCaptureCubemapPending;
        bool releaseOctahedral = _releaseCaptureOctahedralPending;
        _releaseCaptureCubemapPending = false;
        _releaseCaptureOctahedralPending = false;
        _captureReleaseQueued = false;
        if (_releaseCaptureSourceGeneration == _captureSourceGeneration)
            ReleaseCapturedEnvironmentTextures(releaseCube, releaseOctahedral);
        return true;
    }

    private void SettleCaptureFencesForRelease()
    {
        if (_captureFaceFence is { } faceFence &&
            TrySettleReleaseFence(faceFence, _captureFaceRenderer))
        {
            ReleaseCompletedCaptureFacePackage();
            _captureFaceFence = null;
            _captureFaceRenderer = null;
            _pendingCaptureFace = -1;
            _pendingCaptureFaceAuthored = false;
        }

        if (_captureEncodingFence is { } encodingFence &&
            TrySettleReleaseFence(encodingFence, _captureEncodingRenderer))
        {
            _captureEncodingFence = null;
            _captureEncodingRenderer = null;
            _captureEncodingAuthored = false;
        }
    }

    private bool TrySettleReleaseFence(
        XRGpuFence fence,
        AbstractRenderer? renderer)
    {
        EGpuFenceSubmissionStatus submission = fence.SubmissionStatus;
        if (submission == EGpuFenceSubmissionStatus.AwaitingSubmission)
            return false;
        EGpuFenceStatus status = submission == EGpuFenceSubmissionStatus.Submitted
            ? fence.Poll()
            : EGpuFenceStatus.Failed;
        if (status == EGpuFenceStatus.Pending)
            return false;
        if (submission == EGpuFenceSubmissionStatus.Submitted &&
            status == EGpuFenceStatus.Failed)
        {
            QuarantineCaptureResources(renderer);
            return false;
        }
        fence.Dispose();
        return true;
    }

    private static bool TryClaimSharedCaptureAuthoringFrame(ulong renderFrameId)
    {
        long frameKey = unchecked((long)renderFrameId);
        while (true)
        {
            long observed = Volatile.Read(ref s_sharedCaptureAuthoringFrameId);
            if (observed == frameKey)
                return false;
            if (Interlocked.CompareExchange(
                    ref s_sharedCaptureAuthoringFrameId,
                    frameKey,
                    observed) == observed)
                return true;
        }
    }

    private ECaptureStepResult CompleteCaptureEncoding()
    {
        if (_captureEncodingFence is { } fence)
        {
            EGpuFenceSubmissionStatus submission = fence.SubmissionStatus;
            if (submission == EGpuFenceSubmissionStatus.AwaitingSubmission)
                return ECaptureStepResult.Pending;
            EGpuFenceStatus status = submission == EGpuFenceSubmissionStatus.Submitted
                ? fence.Poll() : EGpuFenceStatus.Failed;
            if (status == EGpuFenceStatus.Pending)
                return ECaptureStepResult.Pending;
            if (submission == EGpuFenceSubmissionStatus.Submitted && status == EGpuFenceStatus.Failed)
            {
                QuarantineCaptureResources(_captureEncodingRenderer);
                return ECaptureStepResult.Cancelled;
            }
            bool completed = _captureEncodingAuthored && status == EGpuFenceStatus.Signaled;
            fence.Dispose();
            _captureEncodingFence = null;
            _captureEncodingRenderer = null;
            _captureEncodingAuthored = false;
            if (!IsActiveInHierarchy)
                return ECaptureStepResult.Cancelled;
            if (_captureResourcesDirty)
                return ECaptureStepResult.RestartRequired;
            if (completed)
            {
                _environmentTextureOctahedral!.GenerateMipmapsGPU();
                return ECaptureStepResult.Completed;
            }
        }

        AbstractRenderer renderer = AbstractRenderer.Current
            ?? throw new InvalidOperationException("Capture encoding requires an active renderer.");
        XRRenderPipelineInstance pipeline = _captureEncodingPipeline ??= new(
            RuntimeEngine.Rendering.NewOffscreenCaptureRenderPipeline());
        using IDisposable? pipelineScope = RuntimeEngine.Rendering.State.PushRenderingPipeline(pipeline);
        using IDisposable passScope = RuntimeEngine.Rendering.State.PushRenderGraphPassIndex((int)EDefaultRenderPass.PreRender);
        using IDisposable? resourceScope = renderer.EnterRenderPipelineFrameResourceScope(pipeline, viewport: null);
        _captureEncodingRenderer = renderer;
        _captureEncodingAuthored = renderer.TryExecuteRequiredGpuProducerBatch(
            _captureEncodingProducer ??= EncodeEnvironmentToOctahedralMap,
            out _captureEncodingFence, out Exception? failure);
        if (failure is not null && Debug.ShouldLogEvery("SceneCapture.EncodingRejected", TimeSpan.FromSeconds(1)))
            Debug.LogWarning($"Scene capture encoding deferred: {failure.Message}");
        if (_captureEncodingFence is null &&
            RuntimeRenderingHostServices.FrameTiming.CurrentRenderBackend != RuntimeGraphicsApiKind.Vulkan)
        {
            QuarantineCaptureResources(renderer);
            return ECaptureStepResult.Cancelled;
        }
        return ECaptureStepResult.Pending;
    }

    private static ulong AllocateCaptureIdentity()
    {
        long identity = Interlocked.Increment(ref s_nextCaptureIdentity);
        if (identity <= 0 || identity >= (1L << 45))
            throw new InvalidOperationException("Scene capture output identities exhausted.");
        return 0xCA00000000000000UL | ((ulong)identity << 3);
    }

    private RenderOutputRequest CreateCaptureFaceRequest(int face)
    {
        ulong outputId = _captureIdentity | (uint)face;
        RenderOutputRequest request = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.Secondary, CaptureOutputKind,
            RuntimeEngine.Rendering.State.RenderFrameId);
        return request with
        {
            OutputId = outputId,
            ViewFamilyId = _captureIdentity,
            OutputClass = ERenderOutputClass.RequiredDependency,
            ReadinessPolicy = ERenderOutputReadinessPolicy.BlockForExact,
            WorkClass = ERenderOutputWorkClass.PresentNow,
            FallbackPolicy = ERenderOutputFallbackPolicy.None,
            CompletionRequirement = ERenderOutputCompletionRequirement.BeforeConsumer,
            Target = request.Target with
            {
                StableTargetId = outputId,
                TargetGeneration = unchecked((ulong)(SharedCaptureViewport?.RenderPipelineInstance.ResourceGeneration ?? 0)),
                DisplayWidth = Resolution,
                DisplayHeight = Resolution,
                InternalWidth = Resolution,
                InternalHeight = Resolution,
                SampleCount = 1,
                ViewMask = 1,
                ExternalImageSlot = -1,
            },
        };
    }

    private bool TryCompleteCaptureFace(int face, out bool hasPendingWriter)
    {
        hasPendingWriter = _captureFaceFence is not null;
        if (_captureFaceFence is not { } fence)
            return false;
        if (_pendingCaptureFace != face)
            throw new InvalidOperationException("A capture face cannot advance before its exact writer completes.");
        if (_pendingCaptureSourceGeneration != _captureSourceGeneration)
            throw new InvalidOperationException("A capture source epoch changed while its writer was live.");
        EGpuFenceSubmissionStatus submission = fence.SubmissionStatus;
        if (submission == EGpuFenceSubmissionStatus.AwaitingSubmission)
            return false;
        EGpuFenceStatus status = submission == EGpuFenceSubmissionStatus.Submitted
            ? fence.Poll() : EGpuFenceStatus.Failed;
        if (status == EGpuFenceStatus.Pending)
            return false;
        if (submission == EGpuFenceSubmissionStatus.Submitted && status == EGpuFenceStatus.Failed)
        {
            QuarantineCaptureResources(_captureFaceRenderer);
            return false;
        }

        bool completed = _pendingCaptureFaceAuthored && status == EGpuFenceStatus.Signaled;
        fence.Dispose();
        ReleaseCompletedCaptureFacePackage();
        _captureFaceFence = null;
        _captureFaceRenderer = null;
        _pendingCaptureFace = -1;
        _pendingCaptureFaceAuthored = false;
        hasPendingWriter = false;
        if (completed)
            _completedCaptureFaceCount = face + 1;
        return completed;
    }

    private void ReleaseCompletedCaptureFacePackage()
    {
        _captureFaceCommands?.ReleaseCompletedCanonicalFramePackage(_captureFacePackageGeneration);
        _captureFacePipeline?.ReleaseCompletedCapturePickingSource(_captureFacePackageGeneration);
        _captureFaceCommands = null;
        _captureFacePipeline = null;
        _captureFacePackageGeneration = 0;
    }

    private void QuarantineCaptureResources(AbstractRenderer? renderer)
    {
        if (_captureResourcesQuarantined)
            return;
        renderer ??= _captureFaceRenderer ?? _captureEncodingRenderer ??
            AbstractRenderer.Current;
        if (renderer is null)
            throw new InvalidOperationException(
                "Unfenced capture resources lost their renderer owner.");
        _captureResourcesQuarantined = true;
        s_quarantinedCaptureRenderers.TryAdd(renderer, 0);
        // Exceptional failure retains the complete source/FBO/material closure.
        // A failed submitted fence is not permission to destroy native resources.
        s_quarantinedCaptures.TryAdd(this, renderer);
        Debug.LogWarning("Scene capture has no trustworthy GPU completion fence. Capture is disabled for this renderer and its unresolved resources are retained.");
    }
}
