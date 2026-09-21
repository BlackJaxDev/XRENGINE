using System.Runtime.CompilerServices;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Owns the split-sum BRDF producer and its submission receipt for one pipeline.
/// Resource factories allocate the lookup; this producer initializes it only
/// from an explicit graphics pass and retries rejected or unprepared work.
/// </summary>
internal sealed class BrdfIntegrationResources
{
    internal const string TextureName = "BRDF";
    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, BrdfIntegrationResources> Resources = new();

    private readonly XRRenderPipelineInstance _owner;
    private readonly Func<bool> _renderProducer;
    private XRTexture2D? _target;
    private XRQuadFrameBuffer? _quad;
    private XRMaterial? _material;
    private XRGpuFence? _receipt;
    private IRenderApiWrapperOwner? _targetApiWrapperOwner;
    private uint _width;
    private uint _height;
    private ulong _targetDescriptorResourceEpoch;
    private bool _hasTargetDescriptorResourceEpoch;
    private ulong _authoredFrame = ulong.MaxValue;
    private bool _producerComplete;
    private bool _available;
    private bool _quarantined;

    private BrdfIntegrationResources(XRRenderPipelineInstance owner)
    {
        _owner = owner;
        _renderProducer = RenderProducer;
        owner.CacheClearing += ReleaseResourcesForCacheClear;
    }

    internal static void Prepare(XRRenderPipelineInstance instance)
        => Resources.GetValue(instance, static owner => new BrdfIntegrationResources(owner)).PrepareCore();

    internal static bool IsAvailable(XRRenderPipelineInstance? instance, XRTexture? texture)
        => instance is not null && texture is not null &&
            Resources.TryGetValue(instance, out BrdfIntegrationResources? resources) &&
            ReferenceEquals(resources._target, texture) &&
            !resources._quarantined &&
            resources.HasCurrentTargetResource(AbstractRenderer.Current, texture) &&
            (resources._available ||
                (resources._producerComplete &&
                 resources._authoredFrame == RuntimeEngine.Rendering.State.RenderFrameId &&
                 resources._receipt?.SubmissionStatus is EGpuFenceSubmissionStatus.AwaitingSubmission or EGpuFenceSubmissionStatus.Submitted));

    private void PrepareCore()
    {
        XRTexture2D? target = _owner.GetTexture<XRTexture2D>(TextureName);
        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (target is null || renderer is null || target.Width == 0 || target.Height == 0)
            return;

        bool targetIdentityChanged = !ReferenceEquals(_target, target) ||
            _width != target.Width || _height != target.Height;
        if (targetIdentityChanged)
        {
            TraceReset("TargetIdentity", renderer, target);
            ResetTargetResources(renderer, target);
        }
        else
        {
            // Wrapper ownership is a hard backend-generation boundary even
            // while the producer receipt is pending. Physical descriptor
            // publication, by contrast, is allowed to settle with that receipt.
            if (!HasCurrentTargetOwner(renderer, target))
            {
                TraceReset("RendererOwner", renderer, target);
                ResetTargetResources(renderer, target);
            }
            else
            {
                // The first native descriptor publication may occur while the
                // producer receipt is pending. It is not a replacement of an
                // already authored target and must not recreate the producer.
                if (_quarantined)
                {
                    // Fence polling left the prior writer's acceptance unknown. A
                    // merely unready descriptor is not ownership evidence, but a
                    // new nonzero physical epoch establishes a safe replacement.
                    if (!HasProvenTargetResourceReplacement(renderer, target))
                        return;

                    TraceReset("QuarantineDescriptorEpoch", renderer, target);
                    ResetTargetResources(renderer, target);
                }
                else if (!ResolveReceipt(renderer))
                    return;

                if (!_quarantined && !HasCurrentTargetResource(renderer, target))
                {
                    TraceReset("DescriptorEpochCompletion", renderer, target);
                    ResetCompletionStateForTargetRebind();
                }
            }
        }

        if (_quarantined || _available)
            return;
        if (_quad?.TryPrepareForRendering(forceNoStereo: true) != true)
        {
            Debug.RenderingWarningEvery("BRDF.ProducerNotReady", TimeSpan.FromSeconds(5),
                "BRDF lookup initialization is waiting for its GPU program and framebuffer.");
            return;
        }

        _producerComplete = renderer.TryExecuteRequiredGpuProducerBatch(
            _renderProducer, out _receipt, out Exception? failure);
        _authoredFrame = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_producerComplete)
            CaptureTargetResourceState(renderer, target);
        if (!_producerComplete)
            Debug.RenderingWarningEvery("BRDF.ProducerRejected", TimeSpan.FromSeconds(5),
                "BRDF lookup initialization will retry: {0}", failure?.Message ?? "the complete GPU producer was not accepted");
    }

    /// <summary>
    /// A queued draw is usable by same-frame ordered consumers, but is not a
    /// persistent initialized lookup until the backend accepts its whole cohort.
    /// Keep partial immediate work fenced until completion before retrying it.
    /// </summary>
    private bool ResolveReceipt(AbstractRenderer renderer)
    {
        if (_receipt is null)
            return true;
        EGpuFenceSubmissionStatus submission = _receipt.SubmissionStatus;
        if (submission == EGpuFenceSubmissionStatus.AwaitingSubmission)
            return false;

        if (submission == EGpuFenceSubmissionStatus.Failed)
        {
            _receipt.Dispose();
            _receipt = null;
            _available = false;
            _producerComplete = false;
            Debug.RenderingWarningEvery("BRDF.SubmissionRejected", TimeSpan.FromSeconds(5),
                "BRDF lookup submission failed or was abandoned; initialization will retry.");
            return true;
        }

        EGpuFenceStatus status = _receipt.Poll();
        if (status == EGpuFenceStatus.Pending)
            return false;
        if (status == EGpuFenceStatus.Failed)
        {
            // A submitted command stream may have reached the GPU before its
            // fence became unobservable. Retain its receipt and target until an
            // identity, wrapper, cache, or proven physical-epoch boundary provides
            // a new ownership boundary.
            _available = false;
            _producerComplete = false;
            _quarantined = true;
            Debug.RenderingWarningEvery("BRDF.SubmissionQuarantined", TimeSpan.FromSeconds(5),
                "BRDF lookup submission outcome is unknown; retaining the producer until its target generation resets.");
            return false;
        }

        if (_producerComplete && _target is not null)
            CaptureTargetResourceState(renderer, _target);
        _available = _producerComplete;
        _receipt.Dispose();
        _receipt = null;
        return true;
    }

    private bool HasCurrentTargetResource(AbstractRenderer? renderer, XRTexture? target)
    {
        if (renderer is null || !HasCurrentTargetOwner(renderer, target))
            return false;

        if (!_hasTargetDescriptorResourceEpoch)
            return true;

        RenderTextureSamplingState samplingState =
            renderer.GetTextureShaderSamplingState(target);
        return samplingState.IsReady &&
            samplingState.DescriptorResourceEpoch == _targetDescriptorResourceEpoch;
    }

    private bool HasProvenTargetResourceReplacement(AbstractRenderer renderer, XRTexture target)
    {
        if (!_hasTargetDescriptorResourceEpoch)
            return false;

        RenderTextureSamplingState samplingState = renderer.GetTextureShaderSamplingState(target);
        return samplingState.IsReady &&
            samplingState.DescriptorResourceEpoch != 0 &&
            samplingState.DescriptorResourceEpoch != _targetDescriptorResourceEpoch;
    }

    private bool HasCurrentTargetOwner(AbstractRenderer? renderer, XRTexture? target)
        => renderer is not null &&
           ReferenceEquals(_target, target) &&
           ReferenceEquals(_targetApiWrapperOwner, renderer.ApiWrapperIdentityOwner);

    private void CaptureTargetResourceState(AbstractRenderer renderer, XRTexture target)
    {
        _targetApiWrapperOwner = renderer.ApiWrapperIdentityOwner;
        RenderTextureSamplingState samplingState =
            renderer.GetTextureShaderSamplingState(target);
        if (!samplingState.IsReady || samplingState.DescriptorResourceEpoch == 0)
            return;

        _targetDescriptorResourceEpoch = samplingState.DescriptorResourceEpoch;
        _hasTargetDescriptorResourceEpoch = true;
    }

    private void ResetTargetResources(AbstractRenderer renderer, XRTexture2D target)
    {
        ReleaseResources();
        _target = target;
        _width = target.Width;
        _height = target.Height;
        _targetApiWrapperOwner = renderer.ApiWrapperIdentityOwner;
        CreateProducer();
    }

    /// <summary>
    /// Re-authors a stable logical target after its backend descriptor changes.
    /// This runs only after receipt settlement, so no submitted writer is
    /// discarded; the retained quad resolves the replacement attachment later.
    /// </summary>
    private void ResetCompletionStateForTargetRebind()
    {
        if (_receipt is not null)
            throw new InvalidOperationException(
                "BRDF descriptor rebind requires a settled producer receipt.");

        _available = false;
        _producerComplete = false;
        _authoredFrame = ulong.MaxValue;
        _targetDescriptorResourceEpoch = 0;
        _hasTargetDescriptorResourceEpoch = false;
    }

    private void ReleaseResourcesForCacheClear()
    {
        TraceReset("CacheClearing", AbstractRenderer.Current, _target);
        ReleaseResources();
    }

    private void TraceReset(
        string reason,
        AbstractRenderer? renderer,
        XRTexture2D? target)
    {
        RenderTextureSamplingState samplingState = renderer is null || target is null
            ? default
            : renderer.GetTextureShaderSamplingState(target);
        EGpuFenceSubmissionStatus? receiptSubmission = _receipt?.SubmissionStatus;
        Debug.RenderingWarningEvery(
            "BRDF.Reset",
            TimeSpan.FromSeconds(1),
            "BRDF resources reset: reason={0} pipeline={1} target={2}/{3} same={4} extent={5}x{6}->{7}x{8} ownerSame={9} descriptor={10}/{11} ready={12} receipt={13} quarantined={14}.",
            reason,
            _owner.InstanceId,
            _target is null ? 0 : RuntimeHelpers.GetHashCode(_target),
            target is null ? 0 : RuntimeHelpers.GetHashCode(target),
            ReferenceEquals(_target, target),
            _width,
            _height,
            target?.Width ?? 0,
            target?.Height ?? 0,
            renderer is not null && ReferenceEquals(_targetApiWrapperOwner, renderer.ApiWrapperIdentityOwner),
            _targetDescriptorResourceEpoch,
            samplingState.DescriptorResourceEpoch,
            samplingState.IsReady,
            receiptSubmission?.ToString() ?? "None",
            _quarantined);
    }

    private void CreateProducer()
    {
        XRShader fragment = XRShader.EngineShader(Path.Combine("Scene3D", "BRDF.fs"), EShaderType.Fragment);
        _material = new XRMaterial(fragment)
        {
            Name = "BRDF.Integration",
            RenderOptions = new()
            {
                CullMode = ECullMode.None,
                DepthTest = new() { Enabled = ERenderParamUsage.Disabled, UpdateDepth = false },
                StencilTest = new() { Enabled = ERenderParamUsage.Disabled },
                BlendModeAllDrawBuffers = BlendMode.Disabled(),
                ExcludeFromGpuIndirect = true,
            },
        };
        _quad = new XRQuadFrameBuffer(_material, deriveRenderTargetsFromMaterial: false)
        {
            Name = "BRDF.Integration",
        };
        _quad.SetRenderTargets((_target!, EFrameBufferAttachment.ColorAttachment0, 0, -1));
    }

    private bool RenderProducer()
    {
        AbstractRenderer renderer = AbstractRenderer.Current!;
        BoundingRectangle previousCrop = _owner.RenderState.CurrentCropRegion;
        bool hadCrop = previousCrop.Width > 0 && previousCrop.Height > 0;
        renderer.SetCroppingEnabled(false);
        try
        {
            using var area = _owner.RenderState.PushRenderArea(checked((int)_width), checked((int)_height));
            using var target = _quad!.BindForWritingState();
            if (!_quad.Render(null, forceNoStereo: true))
                return false;
            renderer.PublishFrameBufferAttachmentsForSampling(_quad);
            return true;
        }
        finally
        {
            if (hadCrop)
            {
                renderer.SetCroppingEnabled(true);
                renderer.CropRenderArea(previousCrop);
            }
        }
    }

    private void ReleaseResources()
    {
        _receipt?.Dispose();
        _receipt = null;
        _quad?.FullScreenMesh.Destroy();
        _quad?.Destroy();
        _quad = null;
        _material?.Destroy();
        _material = null;
        _target = null;
        _targetApiWrapperOwner = null;
        _width = _height = 0;
        _targetDescriptorResourceEpoch = 0;
        _hasTargetDescriptorResourceEpoch = false;
        _authoredFrame = ulong.MaxValue;
        _producerComplete = _available = _quarantined = false;
    }
}
