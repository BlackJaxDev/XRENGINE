using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Components.Lights;

public partial class MirrorCaptureComponent
{
    private static long s_nextMirrorOutputIdentity;
    private readonly ulong _mirrorOutputIdentity = AllocateMirrorOutputIdentity();
    private XRGpuFence? _mirrorWriterFence;
    // A bounded set covers overlapping view consumers. Exhaustion retains the
    // whole closure as quarantined instead of growing or losing a GPU reader.
    private readonly XRGpuFence?[] _mirrorConsumerFences = new XRGpuFence?[16];
    private int _mirrorConsumerCount;
    private AbstractRenderer? _mirrorWriterRenderer;
    private bool _mirrorWriterAuthored;
    private bool _mirrorResourcesQuarantined;
    private volatile bool _captureResourcesDirty;
    private bool _mirrorReleaseQueued;
    private readonly object _mirrorLifetimeSync = new();
    private volatile bool _mirrorRetirementRequested = true;
    private bool _advancedMirrorOwnerRegistered;
    private RenderCommandCollection? _mirrorWriterCommands;
    private XRRenderPipelineInstance? _mirrorWriterPipeline;
    private long _mirrorWriterPackageGeneration;
    private long _completedMirrorCaptureVersion;
    private long _mirrorPreRenderCount;
    private long _mirrorAuthoringAttemptCount;
    private ERenderOutputCompletionAuthoringDisposition? _lastMirrorAuthoringDisposition;

    private bool HasPendingMirrorWriter => _mirrorWriterFence is not null;

    /// <summary>Reports ownership without polling a native fence from a diagnostic thread.</summary>
    public object GetMirrorCaptureDiagnostics()
        => new
        {
            captureVersion = Interlocked.Read(ref _completedMirrorCaptureVersion),
            preRenderCount = Interlocked.Read(ref _mirrorPreRenderCount),
            authoringAttempts = Interlocked.Read(ref _mirrorAuthoringAttemptCount),
            lastAuthoringDisposition = _lastMirrorAuthoringDisposition?.ToString(),
            writerSubmission = _mirrorWriterFence?.SubmissionStatus.ToString(),
            writerPackageGeneration = _mirrorWriterPackageGeneration,
            consumers = _mirrorConsumerCount,
            quarantined = _mirrorResourcesQuarantined,
            retirementRequested = _mirrorRetirementRequested,
            releaseQueued = _mirrorReleaseQueued,
            registeredAdvancedOwner = _advancedMirrorOwnerRegistered,
            viewportIdentity = Viewport?.FrameOutputIdentity,
            outputTexture = _environmentTexture?.ID,
            outputWidth = _environmentTexture?.Width,
            outputHeight = _environmentTexture?.Height,
        };

    /// <summary>
    /// The post-render fence covers each scene pass that sampled the material's current mirror texture.
    /// A new capture never writes that texture until every such consumer has completed.
    /// </summary>
    private bool SettleMirrorConsumerFences()
    {
        for (int index = _mirrorConsumerCount - 1; index >= 0; --index)
        {
            XRGpuFence fence = _mirrorConsumerFences[index]!;
            EGpuFenceSubmissionStatus submission = fence.SubmissionStatus;
            if (submission == EGpuFenceSubmissionStatus.AwaitingSubmission)
                return false;
            EGpuFenceStatus status = submission == EGpuFenceSubmissionStatus.Submitted
                ? fence.Poll()
                : EGpuFenceStatus.Failed;
            if (status == EGpuFenceStatus.Pending)
                return false;
            if (submission == EGpuFenceSubmissionStatus.Submitted && status != EGpuFenceStatus.Signaled)
            {
                QuarantineMirrorResources(AbstractRenderer.Current);
                return false;
            }

            fence.Dispose();
            _mirrorConsumerFences[index] = _mirrorConsumerFences[--_mirrorConsumerCount];
            _mirrorConsumerFences[_mirrorConsumerCount] = null;
        }
        return true;
    }

    private void RecordMirrorConsumerCompletion()
    {
        if (_environmentTexture is null || _mirrorResourcesQuarantined)
            return;
        if (_mirrorConsumerCount == _mirrorConsumerFences.Length)
        {
            QuarantineMirrorResources(AbstractRenderer.Current);
            return;
        }
        XRGpuFence? fence = AbstractRenderer.Current?.InsertGpuFence();
        if (fence is null)
        {
            QuarantineMirrorResources(AbstractRenderer.Current);
            return;
        }
        _mirrorConsumerFences[_mirrorConsumerCount++] = fence;
    }

    private static ulong AllocateMirrorOutputIdentity()
    {
        long identity = Interlocked.Increment(ref s_nextMirrorOutputIdentity);
        if (identity <= 0 || identity >= (1L << 45))
            throw new InvalidOperationException("Mirror capture output identities exhausted.");
        return 0x4D00000000000000UL | ((ulong)identity << 3);
    }

    private RenderPipelineRequest CreatePipelineRequest()
    {
        if (!UseAdvancedCapturePipeline)
            return RenderPipelineRequest.OffscreenCapture();
        RenderPipelineOffscreenIntent intent = new(
            ERenderPipelineOffscreenViewIntent.Mirror,
            ERenderPipelineOffscreenOutput.HdrColor,
            EnableLateTransparency: true);
        RuntimeEngine.RegisterAdvancedOffscreenOwner(_mirrorOutputIdentity, intent);
        _advancedMirrorOwnerRegistered = true;
        return RenderPipelineRequest.AdvancedOffscreenCapture(intent, outputId: _mirrorOutputIdentity);
    }

    private void UnregisterAdvancedMirrorOwner()
    {
        if (!_advancedMirrorOwnerRegistered)
            return;
        RuntimeEngine.UnregisterAdvancedOffscreenOwner(
            _mirrorOutputIdentity,
            new(ERenderPipelineOffscreenViewIntent.Mirror,
                ERenderPipelineOffscreenOutput.HdrColor,
                EnableLateTransparency: true));
        _advancedMirrorOwnerRegistered = false;
    }

    private RenderOutputRequest CreateMirrorOutputRequest()
    {
        uint width = _environmentTexture?.Width ?? 0u;
        uint height = _environmentTexture?.Height ?? 0u;
        RenderOutputRequest request = RenderOutputRequest.CreateDefault(
            EVrOutputViewKind.Secondary,
            EFrameOutputKind.InWorldMirror,
            RuntimeEngine.Rendering.State.RenderFrameId);
        return request with
        {
            OutputId = _mirrorOutputIdentity,
            ViewFamilyId = _mirrorOutputIdentity,
            // This pass writes the exact texture consumed by the display draw.
            OutputClass = ERenderOutputClass.RequiredDependency,
            ReadinessPolicy = ERenderOutputReadinessPolicy.BlockForExact,
            WorkClass = ERenderOutputWorkClass.PresentNow,
            FallbackPolicy = ERenderOutputFallbackPolicy.None,
            CompletionRequirement = ERenderOutputCompletionRequirement.BeforeConsumer,
            Target = request.Target with
            {
                StableTargetId = _mirrorOutputIdentity,
                TargetGeneration = unchecked((ulong)(Viewport?.RenderPipelineInstance.ResourceGeneration ?? 0)),
                DisplayWidth = width,
                DisplayHeight = height,
                InternalWidth = width,
                InternalHeight = height,
                SampleCount = 1u,
                ViewMask = 1u,
                ExternalImageSlot = -1,
            },
        };
    }

    private void SettleMirrorWriter()
    {
        if (_mirrorWriterFence is not { } fence)
            return;
        EGpuFenceSubmissionStatus submission = fence.SubmissionStatus;
        if (submission == EGpuFenceSubmissionStatus.AwaitingSubmission)
            return;
        EGpuFenceStatus status = submission == EGpuFenceSubmissionStatus.Submitted
            ? fence.Poll()
            : EGpuFenceStatus.Failed;
        if (status == EGpuFenceStatus.Pending)
            return;
        if (submission == EGpuFenceSubmissionStatus.Submitted && status != EGpuFenceStatus.Signaled)
        {
            QuarantineMirrorResources(_mirrorWriterRenderer);
            return;
        }

        fence.Dispose();
        ReleaseMirrorWriterPackage();
        if (_mirrorWriterAuthored && status == EGpuFenceStatus.Signaled)
            Interlocked.Increment(ref _completedMirrorCaptureVersion);
        _mirrorWriterFence = null;
        _mirrorWriterRenderer = null;
        _mirrorWriterAuthored = false;
    }

    private void QuarantineMirrorResources(AbstractRenderer? renderer)
    {
        if (_mirrorResourcesQuarantined)
            return;
        _mirrorResourcesQuarantined = true;
        Debug.LogWarning("Mirror capture writer has no trustworthy GPU completion. The current capture closure is retained and mirror refresh is disabled.");
    }

    private void QueueCaptureResourceRelease()
    {
        lock (_mirrorLifetimeSync)
        {
            _mirrorRetirementRequested = true;
            if (_mirrorReleaseQueued)
                return;
            _mirrorReleaseQueued = true;
        }
        RuntimeEngine.AddRenderThreadCoroutine(AdvanceMirrorRetirement,
            "MirrorCapture.WaitForWriter", RenderThreadJobKind.RenderPipelineResource);
    }

    private bool AdvanceMirrorRetirement()
    {
        lock (_mirrorLifetimeSync)
        {
            if (!_mirrorRetirementRequested)
            {
                _mirrorReleaseQueued = false;
                return true;
            }
            SettleMirrorWriter();
            if (_mirrorResourcesQuarantined)
            {
                _mirrorReleaseQueued = false;
                return true;
            }
            if (HasPendingMirrorWriter || !SettleMirrorConsumerFences())
                return false;
            ReleaseMirrorResources();
            _mirrorReleaseQueued = false;
            return true;
        }
    }

    private void ReleaseMirrorWriterPackage()
    {
        _mirrorWriterCommands?.ReleaseCompletedCanonicalFramePackage(_mirrorWriterPackageGeneration);
        _mirrorWriterPipeline?.ReleaseCompletedCapturePickingSource(_mirrorWriterPackageGeneration);
        _mirrorWriterCommands = null;
        _mirrorWriterPipeline = null;
        _mirrorWriterPackageGeneration = 0;
    }

    private void ReleaseMirrorResources()
    {
        Viewport?.Destroy();
        Viewport = null;
        _renderFBO.Destroy();
        _material.Textures.Clear();
        _environmentTexture?.Destroy();
        EnvironmentTexture = null;
        _environmentDepthTexture?.Destroy();
        _environmentDepthTexture = null;
        _tempDepth?.Destroy();
        _tempDepth = null;
        _mirrorCamera = null;
        _captureResourcesDirty = true;
        UnregisterAdvancedMirrorOwner();
    }
}
