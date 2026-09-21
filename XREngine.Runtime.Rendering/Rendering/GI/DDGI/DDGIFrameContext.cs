using System.Runtime.CompilerServices;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.GI.DDGI;

/// <summary>
/// Owns DDGI history and update submission state for one pipeline instance.
/// A volume can be viewed by several cameras without sharing their atlas cursors.
/// </summary>
internal sealed partial class DDGIFrameContext
{
    private static readonly ConditionalWeakTable<XRRenderPipelineInstance, DDGIFrameContext> Contexts = new();
    private static readonly string[] UpdateShaders =
    [
        "ddgi_raygen", "ddgi_trace", "ddgi_hit_shade", "ddgi_relocate",
        "ddgi_update_irradiance", "ddgi_update_visibility", "ddgi_border_copy",
        "ddgi_border_copy_visibility", "ddgi_clear",
    ];
    private readonly Dictionary<string, XRRenderProgram> _programs = new(StringComparer.Ordinal);
    private IRenderApiWrapperOwner? _apiWrapperIdentityOwner;
    private WeakReference<DDGIVolumeComponent>? _volume;
    private ulong _authorRevision;
    private ulong _clearedRevision;
    private ulong _frameId = ulong.MaxValue;
    private ulong _frameOwnerFrameId = ulong.MaxValue;
    private EDDGIUpdateStage _stage;
    private XRDataBuffer? _probes;
    private XRTexture? _irradiance;
    private XRTexture? _visibility;
    private DDGIBakedAsset? _uploadedAsset;
    private DDGIBakedAsset? _configuredAsset;
    private string? _attemptedAssetPath;
    private bool _attemptedAssetLoad;
    private ulong _attemptedAssetRevision;
    private DDGIBakedAsset? _failedUploadAsset;
    private ulong _failedUploadRevision;
    private ulong _diagnosticPresentationFrameId = ulong.MaxValue;
    private DDGIGpuTiming? _timing;
    private XRGpuFence? _completionFence;
    // True only when this receipt may advance the published DDGI history.
    // Abort receipts protect partial GPU writes but must never publish them.
    private bool _completionPublishesUpdate;
    private XRGpuFence? _dynamicUseFence;
    private bool _dynamicUseReceiptFailed;
    // Candidate for a baked host upload. Once backend submission accepts it, the
    // fence moves to _bakedUseFence and protects the destination from a later
    // host overwrite until GPU execution is complete.
    private XRGpuFence? _bakedUploadFence;
    private XRGpuFence? _bakedUseFence;
    private bool _bakedUseReceiptFailed;
    private DDGIBakedAsset? _pendingBakedUploadAsset;
    private ulong _pendingBakedUploadRevision;
    private ulong _pendingBakedUploadFrame = ulong.MaxValue;
    // A submitted composite-use fence protects the probe buffer and atlases from
    // a later host upload until the GPU has finished sampling them. A newer
    // awaiting receipt cannot replace it until the backend accepts that newer
    // command stream, since a rejected command stream does not cover prior use.
    private XRGpuFence? _compositeUseFence;
    private XRGpuFence? _pendingCompositeUseFence;
    private bool _compositeUseReceiptFailed;
    private WeakReference<DDGIVolumeComponent>? _frameOwnerVolume;

    public DDGIVolumeRuntimeState State { get; } = new();
    public bool HasInitializedResources { get; private set; }
    internal EDDGIUpdateStage UpdateStage => _stage;
    internal EGpuFenceSubmissionStatus? PendingSubmission
        => _completionFence?.SubmissionStatus ?? _dynamicUseFence?.SubmissionStatus ?? _bakedUploadFence?.SubmissionStatus ??
            _bakedUseFence?.SubmissionStatus ??
            _pendingCompositeUseFence?.SubmissionStatus ?? _compositeUseFence?.SubmissionStatus;

    private DDGIFrameContext(XRRenderPipelineInstance pipeline)
    {
        pipeline.CacheClearing += Clear;
        pipeline.CommandChainExecutionAborted += AbortInFlightUpdate;
    }

    public static DDGIFrameContext Get(XRRenderPipelineInstance pipeline)
    {
        DDGIFrameContext context = Contexts.GetValue(pipeline, static instance => new DDGIFrameContext(instance));
        context.SynchronizeRendererOwner(pipeline);
        return context;
    }

    private void SynchronizeRendererOwner(XRRenderPipelineInstance pipeline)
    {
        // A renderer restart can retain the logical pipeline and resource objects
        // while replacing every native allocation. Reset before resolving receipts
        // or trusting initialized probe history from the retired owner.
        if (!RuntimeEngine.IsRenderThread ||
            !ReferenceEquals(RuntimeEngine.Rendering.State.CurrentRenderingPipeline, pipeline) ||
            AbstractRenderer.Current is not { AcceptsBackendWork: true } renderer)
            return;

        IRenderApiWrapperOwner owner = renderer.ApiWrapperIdentityOwner;
        if (_apiWrapperIdentityOwner is not null && !ReferenceEquals(_apiWrapperIdentityOwner, owner))
            Clear();
        _apiWrapperIdentityOwner = owner;
    }

    internal static bool TryGet(XRRenderPipelineInstance pipeline, out DDGIFrameContext? context)
        => Contexts.TryGetValue(pipeline, out context);

    internal static bool TryGetState(XRRenderPipelineInstance pipeline, out DDGIVolumeRuntimeState? state)
    {
        state = Contexts.TryGetValue(pipeline, out var context) ? context.State : null;
        return state is not null;
    }

    /// <summary>Returns whether this pipeline presented a DDGI diagnostic view in the current render frame.</summary>
    internal static bool IsDiagnosticPresentationFrame(XRRenderPipelineInstance pipeline)
        => Contexts.TryGetValue(pipeline, out DDGIFrameContext? context) &&
            context._diagnosticPresentationFrameId == RuntimeEngine.Rendering.State.RenderFrameId;

    /// <summary>Marks a successfully rendered DDGI diagnostic composite for this render frame.</summary>
    internal void MarkDiagnosticPresentation()
        => _diagnosticPresentationFrameId = RuntimeEngine.Rendering.State.RenderFrameId;

    public XRRenderProgram Program(string name)
    {
        if (_programs.TryGetValue(name, out XRRenderProgram? program))
            return program;
        XRShader shader = ShaderHelper.LoadEngineShader($"Compute/DDGI/{name}.comp", EShaderType.Compute);
        program = new XRRenderProgram(true, false, shader)
        {
            Name = $"DDGI.{name}",
        };
        _programs.Add(name, program);
        return program;
    }

    public static bool IsReady(XRRenderProgram program)
    {
        if (!program.IsLinked)
            program.Link();
        return program.IsLinked;
    }

    public void Synchronize(DDGIVolumeComponent volume)
    {
        State.Synchronize(volume);
        if (IsCurrentVolume(volume) && _authorRevision == volume.RuntimeState.InvalidationRevision)
            return;
        if (_volume is null)
            _volume = new(volume);
        else
            _volume.SetTarget(volume);
        _authorRevision = volume.RuntimeState.InvalidationRevision;
        State.Invalidate();
        InvalidateBakedUploadPublication(invalidateHistory: false);
    }

    private bool IsCurrentVolume(DDGIVolumeComponent volume)
        => _volume is not null && _volume.TryGetTarget(out var current) && ReferenceEquals(current, volume);

    public bool BindResources(XRRenderPipelineInstance pipeline)
    {
        XRDataBuffer? probes = pipeline.GetBuffer(DefaultRenderPipeline.DDGIProbeStateBufferName);
        XRTexture? irradiance = pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.DDGIIrradianceAtlasTextureName);
        XRTexture? visibility = pipeline.GetTexture<XRTexture>(DefaultRenderPipeline.DDGIVisibilityAtlasTextureName);
        if (probes is null || irradiance is null || visibility is null)
            return false;
        // Authored settings can change while a new generation is being built.
        // Never dispatch with the new dimensions into the previous generation.
        if (probes.ElementCount < (uint)(State.TotalProbeCount * State.CascadeCount) ||
            !AtlasMatches(irradiance, State.IrradianceAtlasWidth, State.IrradianceAtlasHeight, State.CascadeCount) ||
            !AtlasMatches(visibility, State.VisibilityAtlasWidth, State.VisibilityAtlasHeight, State.CascadeCount))
        {
            HasInitializedResources = false;
            return false;
        }
        if (!ReferenceEquals(probes, _probes) || !ReferenceEquals(irradiance, _irradiance) || !ReferenceEquals(visibility, _visibility))
        {
            _probes = probes;
            _irradiance = irradiance;
            _visibility = visibility;
            State.Invalidate();
            InvalidateBakedUploadPublication(invalidateHistory: false);
        }
        return true;
    }

    private static bool AtlasMatches(XRTexture texture, int width, int height, int layers)
        => texture is XRTexture2DArray array && array.Textures.Length == layers &&
            array.Width == (uint)width && array.Height == (uint)height;

    public bool PrepareBakedVolume(DDGIVolumeComponent volume)
    {
        if (!TryClaimFrameOwner(volume))
            return false;

        if (!IsCurrentVolume(volume))
        {
            _configuredAsset = null;
            _attemptedAssetLoad = false;
        }
        else if (_attemptedAssetLoad && _attemptedAssetRevision != volume.RuntimeState.InvalidationRevision)
        {
            _attemptedAssetLoad = false;
        }
        string? path = volume.BakedAssetPath;
        if (volume.BakedAsset is null && !string.IsNullOrWhiteSpace(path))
        {
            if (_attemptedAssetLoad && string.Equals(path, _attemptedAssetPath, StringComparison.Ordinal))
                return false;
            _attemptedAssetLoad = true;
            _attemptedAssetPath = path;
            _attemptedAssetRevision = volume.RuntimeState.InvalidationRevision;
            try
            {
                volume.BakedAsset = DDGIBakedAsset.Load(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidDataException)
            {
                Debug.LightingWarning("Failed to load baked DDGI asset '{0}': {1}", path, ex.Message);
                Synchronize(volume);
                return false;
            }
        }
        if (volume.BakedAsset is not { } asset)
            return false;
        try
        {
            if (!ReferenceEquals(_configuredAsset, asset))
            {
                asset.ApplyTo(volume);
                _configuredAsset = asset;
            }
            else
            {
                asset.ApplyLayoutTo(volume);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or OverflowException)
        {
            Debug.LightingWarning("Failed to configure baked DDGI asset: {0}", ex.Message);
            Synchronize(volume);
            return false;
        }
        Synchronize(volume);
        return true;
    }

    public bool TryBegin(XRRenderPipelineInstance pipeline, DDGIVolumeComponent volume)
    {
        if (!TryClaimFrameOwner(volume) || !ResolveCompletionReceipt())
            return false;
        if (_dynamicUseReceiptFailed)
            return false;

        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_frameId == frameId)
            return false;
        _frameId = frameId;
        if (_timing is not null && _timing.Resolve(out float msPerProbe))
        {
            State.MeasuredMillisecondsPerProbe = msPerProbe;
            State.MeasuredFrameTimeMs = msPerProbe * State.GetActiveCascade().ScheduledProbeCount;
        }
        Synchronize(volume);
        if (State.UpdateMode == EDDGIUpdateMode.Baked)
        {
            if (_stage is not (EDDGIUpdateStage.None or EDDGIUpdateStage.Complete))
                AbortInFlightUpdate();
            _stage = EDDGIUpdateStage.None;
            return false;
        }
        if (_stage is not (EDDGIUpdateStage.None or EDDGIUpdateStage.Complete))
        {
            AbortInFlightUpdate();
            // A partial update can have written host-visible buffers. Its ordered
            // non-publishing receipt must be accepted before another cycle can
            // author or overwrite those resources.
            return false;
        }
        _stage = EDDGIUpdateStage.None;
        var camera = pipeline.RenderState.SceneCamera ?? pipeline.RenderState.RenderingCamera;
        if (camera is not null)
        {
            ulong previousRevision = State.InvalidationRevision;
            State.UpdateCascades(camera.Transform.RenderTranslation);
            if (previousRevision != State.InvalidationRevision)
                HasInitializedResources = false;
        }
        if (!BindResources(pipeline) || !State.ShouldRunUpdatePasses((uint)frameId))
            return false;

        // Preflight every program before writing any part of an atlas update.
        bool ready = true;
        for (int i = 0; i < UpdateShaders.Length; i++)
            ready &= IsReady(Program(UpdateShaders[i]));
        if (!ready || !pipeline.Variables.TryGet("DDGIGeometryReady", out bool geometryReady) || !geometryReady)
            return false;
        uint rayCount = checked((uint)(State.ScheduledProbeCount * State.RaysPerProbe));
        if (pipeline.GetBuffer(DefaultRenderPipeline.DDGIRayBufferName) is not { } rays || rays.ElementCount < rayCount ||
            pipeline.GetBuffer(DefaultRenderPipeline.DDGIHitBufferName) is not { } hits || hits.ElementCount < rayCount ||
            pipeline.GetBuffer(DefaultRenderPipeline.DDGIRayRadianceBufferName) is not { } radiance || radiance.ElementCount < rayCount)
            return false;

        // Render-frame modulo controls SlowUpdate cadence; completed-update modulo
        // controls cascade fairness even when that cadence is a multiple of two.
        State.ActiveCascadeIndex = State.ResolveActiveCascadeIndex(State.FrameIndex);
        if (_clearedRevision != State.InvalidationRevision)
            ClearHistory();
        // The clear initializes physical storage, but it is not a publishable DDGI
        // result until every update stage reaches the border copy.
        HasInitializedResources = false;
        if (State.FixedTimeBudgetMs > 0.0f)
        {
            _timing ??= new DDGIGpuTiming();
            if (!_timing.Begin(State.GetActiveCascade().ScheduledProbeCount) && _timing.Diagnostic is { } diagnostic)
                Debug.RenderingWarningEvery("DDGI.GpuTimingUnavailable", TimeSpan.FromSeconds(5), "{0}", diagnostic);
        }
        _stage = EDDGIUpdateStage.Prepared;
        return true;
    }

    public bool CanRun(EDDGIUpdateStage expected)
    {
        if (_frameId != RuntimeEngine.Rendering.State.RenderFrameId || _stage != expected)
            return false;
#if !XRE_PUBLISHED
        // The interruption diagnostic deliberately leaves Visibility pending so
        // the next TryBegin creates an ordered abort receipt for partial writes.
        if (expected == EDDGIUpdateStage.Visibility && ShouldInterruptAfterVisibility())
            return false;
#endif
        return true;
    }

    public void Advance(EDDGIUpdateStage completed)
        => _stage = completed;

    public void Complete()
    {
        _timing?.End();
        XRGpuFence? fence = AbstractRenderer.Current?.InsertGpuFence();
        if (fence is null)
        {
            Debug.RenderingWarningEvery(
                "DDGI.CompletionFenceUnavailable",
                TimeSpan.FromSeconds(5),
                "DDGI update was not published because the renderer did not provide a submission receipt.");
            AbortInFlightUpdate();
            return;
        }

        _completionFence = fence;
        _completionPublishesUpdate = true;
        // Keep the update abortable until a backend receipt exists. Marking it
        // complete before fence acquisition made AbortInFlightUpdate a no-op
        // when acquisition failed, leaving the partial history valid.
        _stage = EDDGIUpdateStage.Complete;
        // The current command chain may composite this fully authored result. The
        // update cursor remains unchanged until a deferred backend accepts it.
        HasInitializedResources = true;
#if !XRE_PUBLISHED
        RecordInterruptionComplete();
#endif
    }

    public bool UploadBaked(DDGIBakedAsset asset)
        => UploadBaked(asset, out _);

    /// <summary>Uploads baked data, returning a diagnostic instead of throwing for invalid assets or destinations.</summary>
    public bool UploadBaked(DDGIBakedAsset asset, out string? failure)
    {
        failure = null;
        if (_probes is null || _irradiance is null || _visibility is null)
        {
            failure = "DDGI baked upload requires initialized probe and atlas resources.";
            return false;
        }
        ulong revision = State.InvalidationRevision;
        if (IsPublishedBakedAsset(asset, revision))
        {
            HasInitializedResources = true;
            return true;
        }
        if (!ResolveBakedUploadReceipt(out failure))
            return false;
        revision = State.InvalidationRevision;
        if (IsPublishedBakedAsset(asset, revision))
        {
            HasInitializedResources = true;
            return true;
        }
        if (_bakedUploadFence is not null)
        {
            if (ReferenceEquals(_pendingBakedUploadAsset, asset) &&
                _pendingBakedUploadRevision == revision &&
                _pendingBakedUploadFrame == RuntimeEngine.Rendering.State.RenderFrameId)
                return true;

            failure = "DDGI baked upload is waiting for a pending backend submission.";
            return false;
        }
        if (!ResolveCompletionReceipt())
        {
            failure = "DDGI baked upload is waiting for backend submission acceptance of a dynamic update.";
            return false;
        }
        if (!ResolveDynamicUseForOverwrite(out failure))
            return false;
        if (!ResolveBakedUseForOverwrite(out failure))
            return false;
        if (!ResolveCompositeUseForOverwrite(out failure))
            return false;

        if (!IsPublishedBakedAsset(asset, revision))
        {
            try
            {
                asset.UploadToGpu(_irradiance, _visibility, _probes);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException or OverflowException)
            {
                failure = ex.Message;
                if (!ReferenceEquals(_failedUploadAsset, asset) || _failedUploadRevision != State.InvalidationRevision)
                {
                    Debug.LightingWarning("Failed to upload baked DDGI asset: {0}", ex.Message);
                    _failedUploadAsset = asset;
                    _failedUploadRevision = State.InvalidationRevision;
                }
                HasInitializedResources = false;
                return false;
            }
            XRGpuFence? fence = AbstractRenderer.Current?.InsertGpuFence();
            if (fence is null)
            {
                InvalidateBakedUploadPublication(invalidateHistory: true);
                failure = "DDGI baked upload could not acquire a backend submission receipt.";
                Debug.LightingWarningEvery("DDGI.BakedUploadFenceUnavailable", TimeSpan.FromSeconds(5), "{0}", failure);
                return false;
            }

            _bakedUploadFence = fence;
            _pendingBakedUploadAsset = asset;
            _pendingBakedUploadRevision = revision;
            _pendingBakedUploadFrame = RuntimeEngine.Rendering.State.RenderFrameId;
            _failedUploadAsset = null;
            // This command stream can sample its ordered upload immediately. The
            // persistent publication is deferred until the backend accepts it.
            HasInitializedResources = true;
        }
        return true;
    }

    private bool IsPublishedBakedAsset(DDGIBakedAsset asset, ulong revision)
        => ReferenceEquals(_uploadedAsset, asset) && _clearedRevision == revision;

    /// <summary>
    /// Returns whether a new DDGI composite can be authored while preserving a
    /// receipt for every prior GPU read of the probe and atlas resources.
    /// </summary>
    public bool CanCompositeRead()
    {
        if (_compositeUseReceiptFailed)
            return false;
        return ResolvePendingCompositeUseReceipt();
    }

    /// <summary>
    /// Records the composite that just sampled DDGI resources. The receipt is
    /// taken after the draw so it covers both the screen-sample dispatch and
    /// the graphics pass that consumes its result.
    /// </summary>
    public bool RecordCompositeUse()
    {
        if (!CanCompositeRead())
            return false;

        XRGpuFence? fence = AbstractRenderer.Current?.InsertGpuFence();
        if (fence is null)
        {
            _compositeUseReceiptFailed = true;
            HasInitializedResources = false;
            Debug.RenderingWarningEvery(
                "DDGI.CompositeUseFenceUnavailable",
                TimeSpan.FromSeconds(5),
                "DDGI composition was disabled because the renderer did not provide a GPU-read lifetime receipt.");
            return false;
        }

        // CanCompositeRead resolves any candidate first. A second candidate
        // would leave the current draw without a receipt if submission is
        // delayed, so fail closed rather than replacing either fence.
        if (_pendingCompositeUseFence is not null)
        {
            fence.Dispose();
            _compositeUseReceiptFailed = true;
            HasInitializedResources = false;
            Debug.RenderingWarningEvery(
                "DDGI.CompositeUseFenceOverflow",
                TimeSpan.FromSeconds(5),
                "DDGI composition exceeded its bounded GPU-read receipt capacity.");
            return false;
        }

        _pendingCompositeUseFence = fence;
        return true;
    }

    private void ClearHistory()
    {
        XRRenderProgram program = Program("ddgi_clear");
        program.BindBuffer(_probes!, 0);
        program.BindImageTexture(0, _irradiance!, 0, true, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.R11G11B10F);
        program.BindImageTexture(1, _visibility!, 0, true, 0, XRRenderProgram.EImageAccess.WriteOnly, XRRenderProgram.EImageFormat.RG16F);
        program.Uniform("uProbeCount", (uint)(State.TotalProbeCount * State.CascadeCount));
        program.DispatchCompute(((uint)State.VisibilityAtlasWidth + 7u) / 8u,
            ((uint)State.VisibilityAtlasHeight + 7u) / 8u, (uint)State.CascadeCount,
            EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.ShaderImageAccess | EMemoryBarrierMask.TextureFetch);
        _clearedRevision = State.InvalidationRevision;
        HasInitializedResources = true;
    }

    private bool TryClaimFrameOwner(DDGIVolumeComponent volume)
    {
        ulong frameId = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_frameOwnerFrameId != frameId)
        {
            _frameOwnerFrameId = frameId;
            if (_frameOwnerVolume is null)
                _frameOwnerVolume = new(volume);
            else
                _frameOwnerVolume.SetTarget(volume);
            return true;
        }

        if (_frameOwnerVolume is not null && _frameOwnerVolume.TryGetTarget(out var owner) && ReferenceEquals(owner, volume))
            return true;

        // A context owns one set of physical probe/atlas resources. A second
        // volume cannot safely author or publish into those resources this frame.
        InvalidatePublishedHistory();
        Debug.RenderingWarningEvery(
            "DDGI.MultipleVolumesPerPipelineFrame",
            TimeSpan.FromSeconds(5),
            "DDGI pipeline instance was asked to render more than one volume in frame {0}; the conflicting volume was skipped.",
            frameId);
        return false;
    }

    private bool ResolveCompletionReceipt()
    {
        if (_completionFence is null)
            return true;

        switch (_completionFence.SubmissionStatus)
        {
            case EGpuFenceSubmissionStatus.AwaitingSubmission:
                return false;
            case EGpuFenceSubmissionStatus.Submitted when _completionFence.Poll() != EGpuFenceStatus.Failed:
                // Submission order makes this newer fence a valid replacement
                // for the prior dynamic-use proof, even while GPU work is pending.
                ReleaseDynamicUseFence();
                _dynamicUseFence = _completionFence;
                _completionFence = null;
                if (_completionPublishesUpdate)
                {
                    State.OnFrameCompleted();
#if !XRE_PUBLISHED
                    RecordInterruptionPublication();
#endif
                }
#if !XRE_PUBLISHED
                else
                    RecordAcceptedInterruptionReceipt();
#endif
                _completionPublishesUpdate = false;
                return true;
            case EGpuFenceSubmissionStatus.Submitted:
                ReleaseCompletionFence();
                _completionPublishesUpdate = false;
                _dynamicUseReceiptFailed = true;
                HasInitializedResources = false;
#if !XRE_PUBLISHED
                RecordInterruptionReceiptFailure();
#endif
                Debug.RenderingWarningEvery(
                    "DDGI.DynamicUseReceiptFailed",
                    TimeSpan.FromSeconds(5),
                    "DDGI resources were retained because a submitted dynamic update receipt failed.");
                return false;
            case EGpuFenceSubmissionStatus.Failed:
                bool failedPublishingReceipt = _completionPublishesUpdate;
                ReleaseCompletionFence();
                _completionPublishesUpdate = false;
                if (failedPublishingReceipt)
                {
                    InvalidatePublishedHistory();
                    return true;
                }

                _dynamicUseReceiptFailed = true;
                HasInitializedResources = false;
#if !XRE_PUBLISHED
                RecordInterruptionReceiptFailure();
#endif
                Debug.RenderingWarningEvery(
                    "DDGI.AbortReceiptFailed",
                    TimeSpan.FromSeconds(5),
                    "DDGI partial GPU writes were retained because their abort receipt failed.");
                return false;
            default:
                ReleaseCompletionFence();
                _completionPublishesUpdate = false;
                _dynamicUseReceiptFailed = true;
                HasInitializedResources = false;
#if !XRE_PUBLISHED
                RecordInterruptionReceiptFailure();
#endif
                return false;
        }
    }

    private void AbortInFlightUpdate()
    {
        if (_stage is EDDGIUpdateStage.None or EDDGIUpdateStage.Complete)
            return;

        XRGpuFence? fence = AbstractRenderer.Current?.InsertGpuFence();
        if (fence is null || _completionFence is not null)
        {
            fence?.Dispose();
#if !XRE_PUBLISHED
            RecordInterruptionAbort();
            RecordInterruptionReceiptFailure();
#endif
            _dynamicUseReceiptFailed = true;
            HasInitializedResources = false;
            Debug.RenderingWarningEvery(
                "DDGI.AbortReceiptUnavailable",
                TimeSpan.FromSeconds(5),
                "DDGI partial GPU writes were retained because an ordered abort receipt was unavailable.");
            InvalidatePublishedHistory();
            return;
        }

        _completionFence = fence;
        _completionPublishesUpdate = false;
#if !XRE_PUBLISHED
        RecordInterruptionAbort();
#endif
        InvalidatePublishedHistory();
    }

    private void InvalidatePublishedHistory()
    {
        _timing?.Cancel();
        InvalidateBakedUploadPublication(invalidateHistory: false);
        _stage = EDDGIUpdateStage.None;
        HasInitializedResources = false;
        State.Invalidate();
    }

    private bool ResolveDynamicUseForOverwrite(out string? failure)
    {
        failure = null;
        if (_dynamicUseReceiptFailed)
        {
            failure = "DDGI dynamic GPU-use receipt failed; resources remain unavailable until the pipeline cache is cleared.";
            return false;
        }
        if (_dynamicUseFence is null)
            return true;

        if (_dynamicUseFence.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted)
        {
            EGpuFenceStatus status = _dynamicUseFence.Poll();
            if (status == EGpuFenceStatus.Signaled)
            {
                ReleaseDynamicUseFence();
                return true;
            }
            if (status == EGpuFenceStatus.Pending)
            {
                HasInitializedResources = false;
                failure = "DDGI baked upload is waiting for prior dynamic GPU writes to finish.";
                return false;
            }
        }

        _dynamicUseReceiptFailed = true;
        HasInitializedResources = false;
        failure = "DDGI dynamic GPU-use receipt failed; resources remain unavailable until the pipeline cache is cleared.";
        Debug.RenderingWarningEvery(
            "DDGI.DynamicUseReceiptFailed",
            TimeSpan.FromSeconds(5),
            "DDGI resources were retained because a dynamic GPU-use receipt failed.");
        return false;
    }

    private bool ResolveCompositeUseForOverwrite(out string? failure)
    {
        failure = null;
        if (_compositeUseReceiptFailed)
        {
            failure = "DDGI GPU-read lifetime receipt failed; resources remain unavailable until the pipeline cache is cleared.";
            return false;
        }
        if (!ResolvePendingCompositeUseReceipt())
        {
            failure = "DDGI baked upload is waiting for backend submission acceptance of a prior composite.";
            return false;
        }
        if (_compositeUseFence is null)
            return true;

        if (_compositeUseFence.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted)
        {
            EGpuFenceStatus status = _compositeUseFence.Poll();
            if (status == EGpuFenceStatus.Signaled)
            {
                ReleaseCompositeUseFences();
                return true;
            }
            if (status == EGpuFenceStatus.Pending)
            {
                failure = "DDGI baked upload is waiting for prior GPU composite reads to finish.";
                HasInitializedResources = false;
                return false;
            }
        }

        _compositeUseReceiptFailed = true;
        HasInitializedResources = false;
        failure = "DDGI GPU-read lifetime receipt failed; resources remain unavailable until the pipeline cache is cleared.";
        Debug.RenderingWarningEvery(
            "DDGI.CompositeUseReceiptFailed",
            TimeSpan.FromSeconds(5),
            "DDGI resources were retained because a composite GPU-read receipt failed.");
        return false;
    }

    private bool ResolvePendingCompositeUseReceipt()
    {
        if (_pendingCompositeUseFence is null)
            return !_compositeUseReceiptFailed;

        switch (_pendingCompositeUseFence.SubmissionStatus)
        {
            case EGpuFenceSubmissionStatus.AwaitingSubmission:
                return false;
            case EGpuFenceSubmissionStatus.Submitted when _pendingCompositeUseFence.Poll() != EGpuFenceStatus.Failed:
                // Submission order makes this newer fence a proof for every
                // earlier composite use on the same renderer queue.
                _compositeUseFence?.Dispose();
                _compositeUseFence = _pendingCompositeUseFence;
                _pendingCompositeUseFence = null;
                return true;
            case EGpuFenceSubmissionStatus.Failed:
                _pendingCompositeUseFence.Dispose();
                _pendingCompositeUseFence = null;
                return true;
            default:
                _compositeUseReceiptFailed = true;
                HasInitializedResources = false;
                Debug.RenderingWarningEvery(
                    "DDGI.CompositeUseReceiptFailed",
                    TimeSpan.FromSeconds(5),
                    "DDGI resources were retained because a composite GPU-read receipt failed.");
                return false;
        }
    }

    private void ReleaseCompletionFence()
    {
        _completionFence?.Dispose();
        _completionFence = null;
        _completionPublishesUpdate = false;
    }

    private void ReleaseDynamicUseFence()
    {
        _dynamicUseFence?.Dispose();
        _dynamicUseFence = null;
    }

    private bool ResolveBakedUploadReceipt(out string? failure)
    {
        failure = null;
        if (_bakedUploadFence is null)
            return true;

        switch (_bakedUploadFence.SubmissionStatus)
        {
            case EGpuFenceSubmissionStatus.AwaitingSubmission when
                _pendingBakedUploadFrame == RuntimeEngine.Rendering.State.RenderFrameId:
                // Upload and screen sampling are ordered within this frame's
                // command stream, so the current composite may consume it.
                return true;
            case EGpuFenceSubmissionStatus.AwaitingSubmission:
                HasInitializedResources = false;
                failure = "DDGI baked upload is waiting for backend submission acceptance.";
                return false;
            case EGpuFenceSubmissionStatus.Submitted:
                EGpuFenceStatus status = _bakedUploadFence.Poll();
                if (status == EGpuFenceStatus.Failed)
                {
                    _bakedUseReceiptFailed = true;
                    HasInitializedResources = false;
                    failure = "DDGI baked upload GPU-use receipt failed; resources remain unavailable until the pipeline cache is cleared.";
                    Debug.LightingWarningEvery(
                        "DDGI.BakedUseReceiptFailed",
                        TimeSpan.FromSeconds(5),
                        "DDGI resources were retained because a baked upload GPU-use receipt failed.");
                    return false;
                }

                ReleaseBakedUseFence();
                _bakedUseFence = _bakedUploadFence;
                DDGIBakedAsset? uploadedAsset = _pendingBakedUploadAsset;
                ulong uploadedRevision = _pendingBakedUploadRevision;
                _bakedUploadFence = null;
                _pendingBakedUploadAsset = null;
                _pendingBakedUploadRevision = 0;
                _pendingBakedUploadFrame = ulong.MaxValue;
                if (uploadedAsset is not null && uploadedRevision == State.InvalidationRevision)
                {
                    _uploadedAsset = uploadedAsset;
                    _clearedRevision = uploadedRevision;
                    _failedUploadAsset = null;
                    _failedUploadRevision = 0;
                    State.OnBakedDataUploaded();
                    HasInitializedResources = true;
                }
                return true;
            case EGpuFenceSubmissionStatus.Failed:
                ReleaseBakedUploadFence();
                InvalidateBakedUploadPublication(invalidateHistory: true);
                return true;
            default:
                HasInitializedResources = false;
                failure = "DDGI baked upload entered an unknown submission state.";
                return false;
        }
    }

    private bool ResolveBakedUseForOverwrite(out string? failure)
    {
        failure = null;
        if (_bakedUseReceiptFailed)
        {
            failure = "DDGI baked GPU-use receipt failed; resources remain unavailable until the pipeline cache is cleared.";
            return false;
        }
        if (_bakedUseFence is null)
            return true;

        if (_bakedUseFence.SubmissionStatus == EGpuFenceSubmissionStatus.Submitted)
        {
            EGpuFenceStatus status = _bakedUseFence.Poll();
            if (status == EGpuFenceStatus.Signaled)
            {
                ReleaseBakedUseFence();
                return true;
            }
            if (status == EGpuFenceStatus.Pending)
            {
                HasInitializedResources = false;
                failure = "DDGI baked upload is waiting for prior baked GPU writes to finish.";
                return false;
            }
        }

        _bakedUseReceiptFailed = true;
        HasInitializedResources = false;
        failure = "DDGI baked GPU-use receipt failed; resources remain unavailable until the pipeline cache is cleared.";
        Debug.LightingWarningEvery(
            "DDGI.BakedUseReceiptFailed",
            TimeSpan.FromSeconds(5),
            "DDGI resources were retained because a baked GPU-use receipt failed.");
        return false;
    }

    private void InvalidateBakedUploadPublication(bool invalidateHistory, bool releaseReceipt = false)
    {
        if (releaseReceipt)
            ReleaseBakedUploadFence();
        _uploadedAsset = null;
        _clearedRevision = 0;
        HasInitializedResources = false;
        if (invalidateHistory)
            State.Invalidate();
    }

    private void ReleaseBakedUploadFence()
    {
        _bakedUploadFence?.Dispose();
        _bakedUploadFence = null;
        _pendingBakedUploadAsset = null;
        _pendingBakedUploadRevision = 0;
        _pendingBakedUploadFrame = ulong.MaxValue;
    }

    private void ReleaseBakedUseFence()
    {
        _bakedUseFence?.Dispose();
        _bakedUseFence = null;
    }

    private void ReleaseCompositeUseFences()
    {
        _compositeUseFence?.Dispose();
        _compositeUseFence = null;
        _pendingCompositeUseFence?.Dispose();
        _pendingCompositeUseFence = null;
    }

    private void Clear()
    {
#if !XRE_PUBLISHED
        ClearInterruptionDiagnostic();
#endif
        _apiWrapperIdentityOwner = null;
        ReleaseCompletionFence();
        ReleaseDynamicUseFence();
        _dynamicUseReceiptFailed = false;
        InvalidateBakedUploadPublication(invalidateHistory: false, releaseReceipt: true);
        ReleaseBakedUseFence();
        _bakedUseReceiptFailed = false;
        ReleaseCompositeUseFences();
        _compositeUseReceiptFailed = false;
        _timing?.Clear();
        foreach (XRRenderProgram program in _programs.Values)
            program.Destroy();
        _programs.Clear();
        _probes = null;
        _irradiance = null;
        _visibility = null;
        _uploadedAsset = null;
        _configuredAsset = null;
        _attemptedAssetPath = null;
        _attemptedAssetLoad = false;
        _attemptedAssetRevision = 0;
        _failedUploadAsset = null;
        _failedUploadRevision = 0;
        _diagnosticPresentationFrameId = ulong.MaxValue;
        _volume = null;
        _frameOwnerVolume = null;
        _clearedRevision = 0;
        _frameId = ulong.MaxValue;
        _frameOwnerFrameId = ulong.MaxValue;
        _stage = EDDGIUpdateStage.None;
        HasInitializedResources = false;
        State.Invalidate();
    }
}
