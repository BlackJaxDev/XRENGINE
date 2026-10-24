using System.Threading;
using Silk.NET.Vulkan;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
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
    private int _nextWorldCommand;
    private int _nextWorldIntent;
    private ulong _nextDrawSerial;
    private ulong _pendingDrawSerial;
    private UIPublishedWorldMeshCommand? _pendingDrawCommand;
    private int _pendingStageIndex = -1;
    private int _nextNativeStage;
    private bool _depthReset;
    private bool _stencilReset;
    private bool _hdrReset;
    private bool _postReset;
    private bool _finalReset;
    private bool _worldDrawIssued;

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

    internal bool IsBoundToPlan(VulkanAcceptedFramePlan plan,
        ulong nativeToken)
    {
        lock (_sync)
            return _state == Bound && nativeToken != 0UL &&
                NativeToken == nativeToken && ReferenceEquals(_plan, plan) &&
                BackendGeneration == owner.BackendGeneration;
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
            _nextWorldCommand = 0;
            _nextWorldIntent = 0;
            _depthReset = _stencilReset = _hdrReset = false;
            _postReset = _finalReset = false;
            _worldDrawIssued = false;
            _pendingStageIndex = -1;
            _nextNativeStage = 0;
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
        XRRenderPipelineInstance? pipeline = _pipeline;
        if (pipeline?.AssignedPipeline is not DefaultRenderPipeline factory)
            return false;
        string? quadName = stage switch
        {
            EUIWorldOutputStage.LightCombine =>
                DefaultRenderPipeline.LightCombineFBOName,
            EUIWorldOutputStage.PostProcess =>
                DefaultRenderPipeline.PostProcessFBOName,
            EUIWorldOutputStage.FinalPostProcess =>
                DefaultRenderPipeline.FinalPostProcessFBOName,
            _ => null,
        };
        if (quadName is not null)
        {
            XRQuadFrameBuffer? quad =
                pipeline.GetFBO<XRQuadFrameBuffer>(quadName);
            if (quad is null ||
                !factory.IsCurrentWorldUiFactoryQuad(quad) ||
                !ReferenceEquals(quad.FullScreenMesh, mesh) ||
                !ReferenceEquals(quad.Material, material))
                return false;
        }
        else if (stage == EUIWorldOutputStage.WindowPresent &&
            !factory.IsCurrentWorldUiFactoryPresentQuad(mesh, material))
            return false;
        XRTexture? expectedSource = pipeline.GetTexture<XRTexture>(
            stage switch
            {
                EUIWorldOutputStage.LightCombine =>
                    DefaultRenderPipeline.AlbedoOpacityTextureName,
                EUIWorldOutputStage.PostProcess =>
                    DefaultRenderPipeline.HDRSceneTextureName,
                EUIWorldOutputStage.FinalPostProcess =>
                    DefaultRenderPipeline.PostProcessOutputTextureName,
                EUIWorldOutputStage.WindowPresent =>
                    DefaultRenderPipeline.FinalPostProcessOutputTextureName,
                _ => string.Empty,
            });
        XRFrameBuffer? expectedDestination = stage switch
        {
            EUIWorldOutputStage.LightCombine => pipeline.GetFBO<XRFrameBuffer>(
                DefaultRenderPipeline.ForwardPassFBOName),
            EUIWorldOutputStage.PostProcess => pipeline.GetFBO<XRFrameBuffer>(
                DefaultRenderPipeline.PostProcessOutputFBOName),
            EUIWorldOutputStage.FinalPostProcess => pipeline.GetFBO<XRFrameBuffer>(
                DefaultRenderPipeline.FinalPostProcessOutputFBOName),
            _ => null,
        };
        XRQuadFrameBuffer? factoryQuad = quadName is null ? null :
            pipeline.GetFBO<XRQuadFrameBuffer>(quadName);
        lock (_sync)
        {
            if (_state != Reserved || !ReferenceEquals(_pipeline, pipeline) ||
                (int)stage != _stepCount ||
                _stepCount >= _steps.Length || stepToken == 0UL ||
                _stepCount > 0 && !_steps[_stepCount - 1].Authored ||
                _region.X != region.X || _region.Y != region.Y ||
                _region.Width != region.Width ||
                _region.Height != region.Height ||
                source is null || mesh is null || material is null ||
                !ReferenceEquals(source, expectedSource) ||
                !ReferenceEquals(destination, expectedDestination))
                return false;
            _steps[_stepCount++] = new VulkanWorldUiStepExpectation
            {
                Stage = stage,
                Source = source,
                Destination = destination,
                Mesh = mesh,
                Material = material,
                FactoryQuad = factoryQuad,
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

    /// <summary>Admits only the next frozen indexed intent on the pinned HDR target.</summary>
    internal bool TryBeginWorldIndexedDraw(
        VulkanAcceptedFramePlan expectedPlan, ulong expectedNativeToken,
        UIPublishedWorldMeshCommand command, XRFrameBuffer? target,
        XRMeshRenderer renderer, EPrimitiveType topology,
        IndexSize elementType, uint indexCount, uint instances,
        out VulkanWorldUiDrawToken token)
    {
        token = default;
        lock (_sync)
        {
            if (_state != Bound || NativeToken != expectedNativeToken ||
                !ReferenceEquals(_plan, expectedPlan) ||
                !_depthReset || !_stencilReset || !_hdrReset ||
                _nextNativeStage != 1 ||
                !ReferenceEquals(target, _forwardTarget) ||
                _pendingDrawSerial != 0UL ||
                Publication is null ||
                _nextWorldCommand >= Publication.WorldCommands.Length ||
                !ReferenceEquals(Publication.WorldCommands[_nextWorldCommand], command) ||
                !ReferenceEquals(command.Publication, Publication) ||
                _nextWorldIntent >= command.IndexedDrawIntents.Length)
                return false;
            UIIndexedDrawIntent expected = command.IndexedDrawIntents[_nextWorldIntent];
            if (!ReferenceEquals(expected.Renderer, renderer) ||
                renderer.Mesh?.GeometryRevision != expected.GeometryRevision ||
                expected.Topology != topology ||
                expected.ElementType != elementType ||
                expected.IndexCount != indexCount ||
                expected.Instances != instances || expected.IndexOffset != 0)
                return false;
            if (_nextDrawSerial == ulong.MaxValue)
                return false;
            _pendingDrawSerial = ++_nextDrawSerial;
            _pendingDrawCommand = command;
            token = new VulkanWorldUiDrawToken(NativeToken,
                _pendingDrawSerial);
            return true;
        }
    }

    internal bool CompleteWorldIndexedDraw(
        UIPublishedWorldMeshCommand command, VulkanWorldUiDrawToken token)
    {
        lock (_sync)
        {
            if (_state != Bound || Publication is null ||
                token.NativeToken != NativeToken ||
                token.Serial == 0UL || token.Serial != _pendingDrawSerial ||
                !ReferenceEquals(_pendingDrawCommand, command) ||
                _nextWorldCommand >= Publication.WorldCommands.Length ||
                !ReferenceEquals(Publication.WorldCommands[_nextWorldCommand], command))
                return false;
            _pendingDrawSerial = 0UL;
            _pendingDrawCommand = null;
            _worldDrawIssued = true;
            _nextWorldIntent++;
            if (_nextWorldIntent == command.IndexedDrawIntents.Length)
            {
                _nextWorldIntent = 0;
                _nextWorldCommand++;
            }
            return true;
        }
    }

    internal bool RequiresNativeDrawProof(XRFrameBuffer? target)
    {
        lock (_sync)
            return _state is not (Idle or Quarantined) &&
                (target is null || ReferenceEquals(target, _deferredTarget) ||
                 ReferenceEquals(target, _forwardTarget) ||
                 ReferenceEquals(target, _postTarget) ||
                 ReferenceEquals(target, _finalTarget));
    }

    internal bool TryBeginStageIndexedDraw(
        VulkanAcceptedFramePlan expectedPlan, ulong expectedNativeToken,
        XRFrameBuffer? target, XRMeshRenderer renderer,
        XRMaterial material, PrimitiveTopology topology,
        uint indexCount, uint instances, out VulkanWorldUiDrawToken token)
    {
        token = default;
        lock (_sync)
        {
            if (_state != Bound || NativeToken != expectedNativeToken ||
                !ReferenceEquals(_plan, expectedPlan) ||
                _pendingDrawSerial != 0UL ||
                _nextNativeStage >= _steps.Length ||
                topology != PrimitiveTopology.TriangleList ||
                indexCount is not (3u or 6u) || instances != 1u ||
                _nextDrawSerial == ulong.MaxValue)
                return false;
            ref VulkanWorldUiStepExpectation step =
                ref _steps[_nextNativeStage];
            XRFrameBuffer? expectedTarget = _nextNativeStage switch
            {
                0 => _forwardTarget,
                1 => _postTarget,
                2 => _finalTarget,
                _ => null,
            };
            if (!step.Authored ||
                !ReferenceEquals(target, expectedTarget) ||
                !ReferenceEquals(renderer, step.Mesh) ||
                !ReferenceEquals(material, step.Material) ||
                (_nextNativeStage == 0 &&
                    (!_depthReset || !_stencilReset || !_hdrReset ||
                     _worldDrawIssued)) ||
                (_nextNativeStage == 1 &&
                    (Publication is null ||
                     _nextWorldCommand != Publication.WorldCommands.Length ||
                     _nextWorldIntent != 0 || !_postReset)) ||
                (_nextNativeStage == 2 && !_finalReset))
                return false;
            if (step.FactoryQuad is { } quad &&
                (_pipeline?.AssignedPipeline is not DefaultRenderPipeline factory ||
                 !factory.IsCurrentWorldUiFactoryQuad(quad) ||
                 !ReferenceEquals(quad.FullScreenMesh, renderer) ||
                 !ReferenceEquals(quad.Material, material)))
                return false;
            if (_nextNativeStage == 3 &&
                (_pipeline?.AssignedPipeline is not DefaultRenderPipeline presentFactory ||
                 !presentFactory.IsCurrentWorldUiFactoryPresentQuad(
                     renderer, material)))
                return false;
            _pendingDrawSerial = ++_nextDrawSerial;
            _pendingStageIndex = _nextNativeStage;
            token = new VulkanWorldUiDrawToken(NativeToken,
                _pendingDrawSerial);
            return true;
        }
    }

    internal bool CompleteStageIndexedDraw(VulkanWorldUiDrawToken token)
    {
        lock (_sync)
        {
            if (_state != Bound || token.NativeToken != NativeToken ||
                token.Serial == 0UL || token.Serial != _pendingDrawSerial ||
                _pendingStageIndex != _nextNativeStage)
                return false;
            _pendingDrawSerial = 0UL;
            _pendingStageIndex = -1;
            _nextNativeStage++;
            return true;
        }
    }

    internal void FailWorldIndexedDraw(VulkanWorldUiDrawToken token)
    {
        lock (_sync)
            if (_state == Bound && token.NativeToken == NativeToken &&
                token.Serial != 0UL && token.Serial == _pendingDrawSerial)
                Volatile.Write(ref _state, Failed);
    }

    internal bool ObserveNativeClear(VulkanAcceptedFramePlan expectedPlan,
        ulong expectedNativeToken, XRFrameBuffer? target, Rect2D area,
        bool color, bool depth, bool stencil, in ColorF4 colorValue,
        float depthValue, uint stencilValue)
    {
        lock (_sync)
        {
            if (_state != Bound || NativeToken != expectedNativeToken ||
                !ReferenceEquals(_plan, expectedPlan))
                return false;
            if (target is null)
                return _pendingDrawSerial == 0UL &&
                    (!_worldDrawIssued || _nextNativeStage == 3);
            if (_pendingDrawSerial != 0UL ||
                !owner.TryCaptureWorldUiTarget(target, out var current) ||
                area.Offset.X != 0 || area.Offset.Y != 0 ||
                area.Extent.Width != current.Width ||
                area.Extent.Height != current.Height ||
                current.ViewMask != 0u)
                return false;

            if (ReferenceEquals(target, _deferredTarget) &&
                current.Equals(_deferredNative))
            {
                if (_worldDrawIssued || _nextNativeStage > 0)
                    return false;
                if (depth && depthValue != 1.0f ||
                    stencil && stencilValue != 0u)
                    return false;
                _depthReset |= depth;
                _stencilReset |= stencil;
                return true;
            }
            if (ReferenceEquals(target, _forwardTarget) &&
                current.Equals(_forwardNative))
            {
                if (_worldDrawIssued || _nextNativeStage > 0)
                    return false;
                if (depth || stencil ||
                    color && (colorValue.R != 0.0f ||
                        colorValue.G != 0.0f || colorValue.B != 0.0f ||
                        colorValue.A != 0.0f) ||
                    color && (!_depthReset || !_stencilReset))
                    return false;
                _hdrReset |= color;
                return true;
            }
            if (ReferenceEquals(target, _postTarget) &&
                current.Equals(_postNative))
            {
                if (_nextNativeStage != 1 || Publication is null ||
                    _nextWorldCommand != Publication.WorldCommands.Length ||
                    _nextWorldIntent != 0 || !color || depth || stencil ||
                    colorValue.R != 0.0f || colorValue.G != 0.0f ||
                    colorValue.B != 0.0f || colorValue.A != 0.0f)
                    return false;
                _postReset = true;
                return true;
            }
            if (ReferenceEquals(target, _finalTarget) &&
                current.Equals(_finalNative))
            {
                if (_nextNativeStage != 2 || !color || depth || stencil ||
                    colorValue.R != 0.0f || colorValue.G != 0.0f ||
                    colorValue.B != 0.0f || colorValue.A != 0.0f)
                    return false;
                _finalReset = true;
                return true;
            }
            // An unclassified clear to a tracked output is never evidence.
            return !ReferenceEquals(target, _postTarget) &&
                !ReferenceEquals(target, _finalTarget);
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
            !owner.TryCaptureWorldUiTarget(final, out var finalNative) ||
            deferredNative.AttachmentCount != 6 ||
            forwardNative.AttachmentCount != 2 ||
            postNative.AttachmentCount != 1 ||
            finalNative.AttachmentCount != 1 ||
            deferredNative.ViewMask != 0u || forwardNative.ViewMask != 0u ||
            postNative.ViewMask != 0u || finalNative.ViewMask != 0u ||
            deferredNative.GetAttachment(5).ImageHandle !=
                forwardNative.GetAttachment(1).ImageHandle ||
            deferredNative.GetAttachment(5).ImageGeneration !=
                forwardNative.GetAttachment(1).ImageGeneration ||
            deferredNative.GetAttachment(5).ImageViewHandle !=
                forwardNative.GetAttachment(1).ImageViewHandle ||
            deferredNative.GetAttachment(5).ImageViewGeneration !=
                forwardNative.GetAttachment(1).ImageViewGeneration)
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
                    _nextWorldCommand = 0;
                    _nextWorldIntent = 0;
                    _pendingDrawSerial = 0UL;
                    _pendingDrawCommand = null;
                    _pendingStageIndex = -1;
                    _nextNativeStage = 0;
                    _depthReset = _stencilReset = _hdrReset = false;
                    _postReset = _finalReset = false;
                    _worldDrawIssued = false;
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
