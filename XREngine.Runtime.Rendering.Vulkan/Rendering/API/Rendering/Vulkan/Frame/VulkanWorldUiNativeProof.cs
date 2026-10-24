using System.Threading;
using XREngine.Data.Geometry;
using XREngine.Rendering.UI;

namespace XREngine.Rendering.Vulkan;

/// <summary>Tracks one world UI image from managed authoring to native recording.</summary>
internal sealed class VulkanWorldUiNativeProof(VulkanRenderer owner)
{
    private const int Idle = 0;
    private const int Reserved = 1;
    private const int Authored = 2;
    private const int Claimed = 3;
    private const int Bound = 4;
    private const int Recorded = 5;
    private const int Failed = 6;
    private const int Retiring = 7;
    private const int Quarantined = 8;

    private readonly object _sync = new();
    private int _state;
    private bool _ownedByPlan;
    private VulkanAcceptedFramePlan? _plan;
    private ulong _logicalPlanGeneration;
    private ulong _vulkanAttemptFrameId;
    private XRWindow? _window;
    private XRRenderPipelineInstance? _pipeline;
    private RenderFrameViewDescriptor _view;
    private BoundingRectangle _region;
    private XRFrameBuffer? _deferredTarget, _forwardTarget,
        _postTarget, _finalTarget;
    private VulkanRecordedRenderTargetSnapshot _deferredNative,
        _forwardNative, _postNative, _finalNative;
    private readonly VulkanWorldUiStepExpectation[] _steps = new VulkanWorldUiStepExpectation[4];
    private int _stepCount;

    internal UIPublication? Publication { get; private set; }
    internal ulong FrameId { get; private set; }
    internal ulong ReceiptId { get; private set; }
    internal ulong DispatchToken { get; private set; }
    internal long BackendGeneration { get; private set; }
    internal ulong NativeToken { get; private set; }

    internal bool IsRecordingComplete => Volatile.Read(ref _state) == Recorded;
    internal bool IsOwnedByPlan
    {
        get
        {
            lock (_sync)
                return _ownedByPlan;
        }
    }
    internal RenderFrameViewDescriptor FrozenView => _view;
    internal BoundingRectangle Region => _region;

    internal bool IsStillValid
    {
        get
        {
            lock (_sync)
                return _state == Recorded && _plan is not null &&
                    _plan.LogicalPlanGeneration == _logicalPlanGeneration &&
                    _plan.FrameId == _vulkanAttemptFrameId &&
                    owner.BackendGeneration == BackendGeneration &&
                    Publication is not null && _window is not null &&
                    _pipeline is not null && PinnedNativeTargetsCurrent();
        }
    }

    private bool PinnedNativeTargetsCurrent()
        => _deferredTarget is not null &&
            _forwardTarget is not null &&
            _postTarget is not null &&
            _finalTarget is not null &&
            owner.TryCaptureWorldUiTarget(_deferredTarget, out var deferred) &&
            owner.TryCaptureWorldUiTarget(_forwardTarget, out var forward) &&
            owner.TryCaptureWorldUiTarget(_postTarget, out var post) &&
            owner.TryCaptureWorldUiTarget(_finalTarget, out var final) &&
            deferred.Equals(_deferredNative) &&
            forward.Equals(_forwardNative) &&
            post.Equals(_postNative) &&
            final.Equals(_finalNative);

    internal bool TryReserve(XRWindow window, XRRenderPipelineInstance pipeline,
        UIPublication publication, BoundingRectangle region,
        in RenderFrameViewDescriptor view, ulong frameId, ulong dispatchToken,
        ulong receiptId, ulong nativeToken)
    {
        lock (_sync)
        {
            if (_state != Idle || frameId == 0UL || dispatchToken == 0UL ||
                receiptId == 0UL || nativeToken == 0UL ||
                publication.WorldCommands.Length == 0 ||
                region.Width <= 0 || region.Height <= 0)
                return false;
            publication.Retain();
            Publication = publication;
            _window = window;
            _pipeline = pipeline;
            _view = view;
            _region = region;
            _stepCount = 0;
            Array.Clear(_steps);
            FrameId = frameId;
            ReceiptId = receiptId;
            DispatchToken = dispatchToken;
            BackendGeneration = owner.BackendGeneration;
            NativeToken = nativeToken;
            Volatile.Write(ref _state, Reserved);
            return true;
        }
    }

    internal bool TryMarkAuthored(ulong nativeToken)
    {
        lock (_sync)
        {
            if (_state != Reserved || NativeToken != nativeToken ||
                _stepCount != _steps.Length)
                return false;
            for (int index = 0; index < _steps.Length; index++)
                if (!_steps[index].Authored)
                    return false;
            Volatile.Write(ref _state, Authored);
            return true;
        }
    }

    internal bool TryReserveStep(EUIWorldOutputStage stage, XRTexture source,
        XRFrameBuffer? destination, XRMeshRenderer mesh, XRMaterial material,
        BoundingRectangle region, ulong stepToken)
    {
        lock (_sync)
        {
            if (_state != Reserved || (int)stage != _stepCount ||
                _stepCount >= _steps.Length || stepToken == 0UL ||
                _stepCount > 0 && !_steps[_stepCount - 1].Authored ||
                _region.X != region.X || _region.Y != region.Y ||
                _region.Width != region.Width ||
                _region.Height != region.Height ||
                source is null || mesh is null || material is null)
                return false;
            _steps[_stepCount++] = new VulkanWorldUiStepExpectation
            {
                Stage = stage,
                Source = source,
                Destination = destination,
                Mesh = mesh,
                Material = material,
                Region = region,
                Token = stepToken,
            };
            return true;
        }
    }

    internal bool TryMarkStepAuthored(ulong stepToken)
    {
        lock (_sync)
        {
            if (_state != Reserved || _stepCount == 0)
                return false;
            ref VulkanWorldUiStepExpectation step = ref _steps[_stepCount - 1];
            if (step.Token != stepToken || step.Authored)
                return false;
            step.Authored = true;
            return true;
        }
    }

    internal bool OwnsStep(ulong stepToken)
    {
        lock (_sync)
        {
            for (int index = 0; index < _stepCount; index++)
                if (_steps[index].Token == stepToken)
                    return true;
            return false;
        }
    }

    internal bool TryClaim(ulong frameId, ulong receiptId)
    {
        lock (_sync)
        {
            if (_state != Authored || FrameId != frameId ||
                ReceiptId != receiptId ||
                owner.BackendGeneration != BackendGeneration)
                return false;
            _ownedByPlan = true;
            Volatile.Write(ref _state, Claimed);
            return true;
        }
    }

    internal void ReleasePlanOwnership(ulong nativeToken)
        => Retire(nativeToken, planRelease: true);

    internal void ReleaseUnclaimed(ulong nativeToken)
        => Retire(nativeToken, planRelease: false);

    internal bool TryBindAcceptedPlan(VulkanAcceptedFramePlan plan,
        ulong logicalPlanGeneration, long backendGeneration)
    {
        XRRenderPipelineInstance? pipeline = _pipeline;
        XRFrameBuffer? deferred = pipeline?.GetFBO<XRFrameBuffer>(
            DefaultRenderPipeline.DeferredGBufferFBOName);
        XRFrameBuffer? forward = pipeline?.GetFBO<XRFrameBuffer>(
            DefaultRenderPipeline.ForwardPassFBOName);
        XRFrameBuffer? post = pipeline?.GetFBO<XRFrameBuffer>(
            DefaultRenderPipeline.PostProcessOutputFBOName);
        XRFrameBuffer? final = pipeline?.GetFBO<XRFrameBuffer>(
            DefaultRenderPipeline.FinalPostProcessOutputFBOName);
        if (deferred is null || forward is null || post is null || final is null ||
            !owner.TryCaptureWorldUiTarget(deferred, out var deferredNative) ||
            !owner.TryCaptureWorldUiTarget(forward, out var forwardNative) ||
            !owner.TryCaptureWorldUiTarget(post, out var postNative) ||
            !owner.TryCaptureWorldUiTarget(final, out var finalNative))
            return false;
        lock (_sync)
        {
            if (_state != Claimed || backendGeneration != BackendGeneration ||
                owner.BackendGeneration != BackendGeneration ||
                !ReferenceEquals(_pipeline, pipeline) ||
                plan.FrameId == 0UL ||
                logicalPlanGeneration == 0UL ||
                !plan.IsUiPublicationReceiptBound(ReceiptId))
                return false;
            _plan = plan;
            _logicalPlanGeneration = logicalPlanGeneration;
            _vulkanAttemptFrameId = plan.FrameId;
            _deferredTarget = deferred;
            _forwardTarget = forward;
            _postTarget = post;
            _finalTarget = final;
            _deferredNative = deferredNative;
            _forwardNative = forwardNative;
            _postNative = postNative;
            _finalNative = finalNative;
            Volatile.Write(ref _state, Bound);
            return true;
        }
    }

    internal void Fail()
    {
        lock (_sync)
            if (_state is not (Idle or Retiring or Quarantined))
                Volatile.Write(ref _state, Failed);
    }

    private void Retire(ulong nativeToken, bool planRelease)
    {
        UIPublication? publication;
        lock (_sync)
        {
            if (_state is Idle or Retiring or Quarantined ||
                planRelease != _ownedByPlan ||
                nativeToken == 0UL || NativeToken != nativeToken)
                return;
            Volatile.Write(ref _state, Retiring);
            publication = Publication;
        }
        bool released = false;
        try
        {
            publication?.Release();
            released = true;
        }
        finally
        {
            lock (_sync)
            {
                if (!released)
                {
                    // Release has an uncertain outcome. Keep this slot and its
                    // publication out of circulation instead of reusing it.
                    Volatile.Write(ref _state, Quarantined);
                }
                else
                {
                    Publication = null;
                    _plan = null;
                    _logicalPlanGeneration = 0UL;
                    _vulkanAttemptFrameId = 0UL;
                    _window = null;
                    _pipeline = null;
                    _view = default;
                    _region = default;
                    _deferredTarget = _forwardTarget = _postTarget = _finalTarget = null;
                    _deferredNative = _forwardNative = _postNative = _finalNative = default;
                    _stepCount = 0;
                    _ownedByPlan = false;
                    Array.Clear(_steps);
                    FrameId = ReceiptId = DispatchToken = NativeToken = 0UL;
                    BackendGeneration = 0L;
                    Volatile.Write(ref _state, Idle);
                }
            }
        }
    }
}
