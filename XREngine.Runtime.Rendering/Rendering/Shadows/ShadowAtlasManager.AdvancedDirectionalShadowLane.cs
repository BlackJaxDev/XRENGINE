using System.Diagnostics;
using XREngine.Components.Lights;

namespace XREngine.Rendering.Shadows;

/// <summary>
/// Hand-off of scheduled directional cascade groups to the Advanced directional
/// shadow raster stage. The atlas manager keeps allocation, dirty tracking,
/// completion receipts and the generic per-renderer path. A strict GPU group
/// stays dirty when the lane cannot accept it. Everything here runs on the
/// render thread.
/// </summary>
public sealed partial class ShadowAtlasManager
{
    private const int MaxPendingAdvancedLaneGroups = 4;
    private const int AdvancedLaneFailureHoldFrames = 60;
    private const int MaxAdvancedLaneConsumers = 8;

    private static readonly bool s_advancedDirectionalShadowLaneEnabled =
        ReadAdvancedDirectionalShadowLaneFlag();

    private readonly PendingAdvancedLaneGroup[] _pendingAdvancedLaneGroups =
        new PendingAdvancedLaneGroup[MaxPendingAdvancedLaneGroups];
    private readonly AdvancedDirectionalShadowLaneRequest[] _advancedLaneRequests =
        CreateAdvancedLaneRequests();
    // Static caster tiles, one per (light, source, cascade) with recent dynamic casters (see
    // DirectionalCascadeStaticTile). A light that leaves keeps its slots until the atlas resets.
    private readonly DirectionalCascadeStaticTile?[] _directionalStaticCascadeTiles =
        new DirectionalCascadeStaticTile?[MaxPendingAdvancedLaneGroups * AdvancedDirectionalShadowLaneRequest.MaxCascadeCount * 2];
    private int _pendingAdvancedLaneGroupCount;
    private ulong _advancedLaneConsumerFrameId;
    private ulong _advancedLaneTargetFrameId;
    private XRFrameBuffer? _advancedLaneTargetFrameBuffer;
    private bool _advancedLaneConsumerReady;
    private string? _advancedLaneConsumerReason;
    private string? _advancedLaneCasterProducer;
    private string? _advancedLaneCasterProducerReason;
    private AdvancedDirectionalShadowConsumerAuthority _advancedLaneAuthority;
    private ulong _advancedLaneAuthorityFrameId;
    private bool _advancedLaneOwnerExpected;
    private bool _advancedLaneGenericFallbackAllowed = true;
    private bool _advancedLaneAuthorityConflict;
    private string? _advancedLaneAuthorityReason;
    // Frames with a lane owner that remain in the failure hold. The hold counts only frames in
    // which an Advanced instance owns the lane, so render frames that draw no Advanced view (for
    // example OpenXR frames without an eye frame) do not end it early.
    private int _advancedLaneHoldOwnerFrames;
    private ulong _advancedLaneHoldCountedFrameId;
    // True when VR eye views render in this frame before the lane owner executes (the eyes do not
    // use an Advanced pipeline). HMD-source groups then use the generic path, which draws them
    // before any view.
    private bool _advancedLaneHmdViewsBeforeOwner;
    // Readiness that each Advanced pipeline instance reported at its last directional shadow
    // stage. The lane defers groups only to an owner whose last report was ready.
    private readonly AdvancedLaneConsumerReadiness[] _advancedLaneConsumerReadiness =
        new AdvancedLaneConsumerReadiness[MaxAdvancedLaneConsumers];
    private string? _advancedLaneLastDeclineReason;
    private long _advancedLaneDeferredGroups;
    private long _advancedLaneAcceptedGroups;
    private long _advancedLaneRejectedGroups;
    private long _advancedLaneUnconsumedGroups;
    // Unconsumed groups whose owner did not run its stage in their frame. They start no hold.
    private long _advancedLaneOwnerAbsentGroups;
    private long _advancedLaneGenericGroups;

    private enum PendingAdvancedLaneGroupState : byte
    {
        Free = 0,
        Pending,
        Dequeued,
    }

    private struct AdvancedLaneConsumerReadiness
    {
        public XRRenderPipelineInstance? Pipeline;
        public ulong FrameId;
        public bool Ready;
        public string? Reason;
    }

    private struct PendingAdvancedLaneGroup
    {
        public PendingAdvancedLaneGroupState State;
        public int PlanIndex;
        public ShadowAtlasRenderPlan? Plan;
        public ShadowAtlasRenderPlanEntry Entry;
        public DirectionalLightComponent? Light;
        public AdvancedDirectionalShadowLaneRequest? Request;
        public bool CriticalBypass;
        public long DeferredTimestamp;
    }

    /// <summary>
    /// Whether the process allows directional cascade groups on the Advanced lane
    /// (<c>XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE=0</c> keeps every group generic).
    /// </summary>
    public static bool AdvancedDirectionalShadowLaneEnabled => s_advancedDirectionalShadowLaneEnabled;

    /// <summary>
    /// True when the directional cascade groups of this render frame can be drawn: the
    /// lane is off (every group uses the generic path), or an Advanced pipeline instance
    /// reported its directional shadow stage ready at its last execution. The
    /// scheduled-tile pass tests the readiness of the frame's owner before it defers a
    /// group to the lane, and a strict lane drops a declined group without a draw. The
    /// Advanced capture reads this at the swap, before the render frame chooses its owner,
    /// so it tests every known consumer.
    /// </summary>
    internal bool CanDrawDirectionalCascadeGroupsThisFrame()
    {
        if (!s_advancedDirectionalShadowLaneEnabled)
            return true;

        for (int i = 0; i < _advancedLaneConsumerReadiness.Length; i++)
        {
            ref readonly AdvancedLaneConsumerReadiness entry = ref _advancedLaneConsumerReadiness[i];
            if (entry.Pipeline is not null && entry.Ready)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Finds the readiness that <paramref name="pipeline"/> reported at its last directional
    /// shadow stage. Returns false when that instance has not run the stage yet.
    /// </summary>
    private bool TryGetAdvancedLaneConsumerReadiness(
        XRRenderPipelineInstance? pipeline,
        out bool ready,
        out string? reason)
    {
        ready = false;
        reason = null;
        if (pipeline is null)
            return false;

        for (int i = 0; i < _advancedLaneConsumerReadiness.Length; i++)
        {
            ref readonly AdvancedLaneConsumerReadiness entry = ref _advancedLaneConsumerReadiness[i];
            if (!ReferenceEquals(entry.Pipeline, pipeline))
                continue;

            ready = entry.Ready;
            reason = entry.Reason;
            return true;
        }
        return false;
    }

    /// <summary>Lane counters and the last decline reason, for diagnostics.</summary>
    public AdvancedDirectionalShadowLaneDiagnostics CaptureAdvancedDirectionalShadowLaneDiagnostics()
        => new(
            s_advancedDirectionalShadowLaneEnabled,
            _advancedLaneConsumerReady,
            _advancedLaneConsumerFrameId,
            _advancedLaneHoldOwnerFrames,
            _advancedLaneDeferredGroups,
            _advancedLaneAcceptedGroups,
            _advancedLaneRejectedGroups,
            _advancedLaneUnconsumedGroups,
            _advancedLaneOwnerAbsentGroups,
            _advancedLaneGenericGroups,
            _advancedLaneLastDeclineReason ?? _advancedLaneConsumerReason,
            _advancedLaneCasterProducer,
            _advancedLaneCasterProducerReason);

    /// <summary>
    /// Called by the Advanced directional shadow stage of <paramref name="consumer"/> every
    /// time that stage owns the frame's groups. The lane defers groups to an owner only
    /// while the last report of that instance was ready. A strict GPU request stays dirty
    /// without it. <paramref name="casterProducer"/> names the producer that draws the
    /// casters of this frame's submission strategy, and
    /// <paramref name="casterProducerReason"/> tells why. Diagnostics show both, so
    /// a reader can see, for example, that a meshlet frame draws indexed casters.
    /// The stage passes constant strings, so this call does not allocate.
    /// </summary>
    public void NotifyAdvancedDirectionalShadowLaneConsumer(
        ulong renderFrameId,
        XRRenderPipelineInstance consumer,
        bool ready,
        string? reason,
        string? casterProducer = null,
        string? casterProducerReason = null)
    {
        AssertRenderThread();
        _advancedLaneConsumerFrameId = renderFrameId;
        _advancedLaneConsumerReady = ready;
        _advancedLaneConsumerReason = ready ? null : reason;
        _advancedLaneCasterProducer = casterProducer;
        _advancedLaneCasterProducerReason = casterProducerReason;

        // Keep one entry per instance. A new instance takes a free entry, or the entry with
        // the oldest report when all entries are in use.
        int slot = -1;
        for (int i = 0; i < _advancedLaneConsumerReadiness.Length; i++)
        {
            ref readonly AdvancedLaneConsumerReadiness entry = ref _advancedLaneConsumerReadiness[i];
            if (ReferenceEquals(entry.Pipeline, consumer))
            {
                slot = i;
                break;
            }
            if (slot < 0)
            {
                slot = i;
                continue;
            }
            ref readonly AdvancedLaneConsumerReadiness chosen = ref _advancedLaneConsumerReadiness[slot];
            if (chosen.Pipeline is not null &&
                (entry.Pipeline is null || entry.FrameId < chosen.FrameId))
            {
                slot = i;
            }
        }

        ref AdvancedLaneConsumerReadiness target = ref _advancedLaneConsumerReadiness[slot];
        target.Pipeline = consumer;
        target.FrameId = renderFrameId;
        target.Ready = ready;
        target.Reason = ready ? null : reason;
    }

    /// <summary>
    /// Publishes the consumer that owns this frame's directional cascade groups, selected before
    /// shadow scheduling. <paramref name="hmdViewsRenderBeforeOwner"/> is true when VR eye views
    /// render before that owner in this frame.
    /// </summary>
    internal void PublishAdvancedDirectionalShadowConsumerAuthority(
        ulong renderFrameId,
        in AdvancedDirectionalShadowConsumerAuthority authority,
        bool ownerExpected,
        bool genericFallbackAllowed,
        string? reason,
        bool hmdViewsRenderBeforeOwner = false)
    {
        AssertRenderThread();
        if (_advancedLaneAuthorityFrameId == renderFrameId)
            _advancedLaneHmdViewsBeforeOwner |= hmdViewsRenderBeforeOwner;
        else
            _advancedLaneHmdViewsBeforeOwner = hmdViewsRenderBeforeOwner;
        if (_advancedLaneAuthorityFrameId == renderFrameId)
        {
            _advancedLaneGenericFallbackAllowed &= genericFallbackAllowed;
            if (!ownerExpected)
                return;
            if (_advancedLaneOwnerExpected &&
                (_advancedLaneAuthorityConflict ||
                 !_advancedLaneAuthority.SameAs(in authority)))
            {
                _advancedLaneAuthority = default;
                _advancedLaneOwnerExpected = true;
                _advancedLaneAuthorityConflict = true;
                _advancedLaneAuthorityReason =
                    "Multiple desktop Advanced consumers published conflicting command packages for this shadow frame.";
                return;
            }
        }
        else
        {
            _advancedLaneGenericFallbackAllowed = genericFallbackAllowed;
            _advancedLaneAuthorityConflict = false;
        }

        if (!ownerExpected)
        {
            _advancedLaneAuthorityFrameId = renderFrameId;
            _advancedLaneAuthority = default;
            _advancedLaneOwnerExpected = false;
            _advancedLaneAuthorityReason = reason;
            return;
        }

        _advancedLaneAuthorityFrameId = renderFrameId;
        _advancedLaneAuthority = authority.IsValid && authority.RenderFrameId == renderFrameId
            ? authority : default;
        _advancedLaneOwnerExpected = ownerExpected;
        _advancedLaneAuthorityReason = _advancedLaneAuthority.IsValid ? null : reason;

        // The failure hold counts frames that have an owner, once per frame.
        if (_advancedLaneAuthority.IsValid && _advancedLaneHoldCountedFrameId != renderFrameId)
        {
            _advancedLaneHoldCountedFrameId = renderFrameId;
            if (_advancedLaneHoldOwnerFrames > 0)
                _advancedLaneHoldOwnerFrames--;
        }
    }

    /// <summary>
    /// True when a valid consumer authority for the frame of <paramref name="authority"/> exists
    /// and names another pipeline instance. Several Advanced pipeline instances can run in one
    /// frame (the desktop view and the VR eyes); only the owner may take the deferred groups and
    /// report readiness. Another instance skips the lane, so it neither rejects the owner's groups
    /// nor marks the lane not ready.
    /// </summary>
    internal bool IsAdvancedDirectionalShadowConsumerOwnedByAnother(
        in AdvancedDirectionalShadowConsumerAuthority authority)
        => _advancedLaneAuthorityFrameId == authority.RenderFrameId &&
            _advancedLaneAuthority.IsValid &&
            !_advancedLaneAuthority.SameAs(in authority);

    /// <summary>Checks that the stage owns this frame's scheduled groups.</summary>
    internal bool MatchesAdvancedDirectionalShadowConsumerAuthority(
        in AdvancedDirectionalShadowConsumerAuthority authority)
        => _advancedLaneAuthorityFrameId == authority.RenderFrameId &&
            _advancedLaneAuthority.SameAs(in authority);

    /// <summary>
    /// Hands out the next cascade group deferred by this frame's scheduled-tile
    /// pass. Every dequeued group must be returned through
    /// <see cref="CompleteAdvancedDirectionalShadowGroup"/>.
    /// </summary>
    public bool TryDequeuePendingAdvancedDirectionalShadowGroup(
        out AdvancedDirectionalShadowLaneRequest request)
    {
        AssertRenderThread();
        for (int slot = 0; slot < _pendingAdvancedLaneGroups.Length; slot++)
        {
            ref PendingAdvancedLaneGroup pending = ref _pendingAdvancedLaneGroups[slot];
            if (pending.State != PendingAdvancedLaneGroupState.Pending || pending.Request is null)
                continue;

            pending.State = PendingAdvancedLaneGroupState.Dequeued;
            request = pending.Request;
            return true;
        }

        request = null!;
        return false;
    }

    /// <summary>
    /// Records the backend's decision for a dequeued group. An accepted group
    /// commits its cascade slots and completion receipts exactly as a generic
    /// grouped render does. A rejected group stays dirty. Only a CPU-direct
    /// group can return to generic rendering after a bounded lane hold.
    /// </summary>
    public void CompleteAdvancedDirectionalShadowGroup(
        AdvancedDirectionalShadowLaneRequest request,
        bool accepted,
        string? reason)
    {
        AssertRenderThread();
        ArgumentNullException.ThrowIfNull(request);
        int slot = request.PendingSlot;
        if ((uint)slot >= (uint)_pendingAdvancedLaneGroups.Length)
            return;

        ref PendingAdvancedLaneGroup pending = ref _pendingAdvancedLaneGroups[slot];
        if (pending.State != PendingAdvancedLaneGroupState.Dequeued ||
            !ReferenceEquals(pending.Request, request))
        {
            return;
        }

        ShadowAtlasRenderPlan? plan = pending.Plan;
        DirectionalLightComponent? light = pending.Light;
        ShadowAtlasRenderPlanEntry entry = pending.Entry;
        bool criticalBypass = pending.CriticalBypass;
        double elapsedMs = ElapsedMilliseconds(pending.DeferredTimestamp);
        int planIndex = pending.PlanIndex;
        bool requiresStrictGpu = request.RequiresStrictGpu;
        ReleasePendingAdvancedLaneGroup(ref pending);

        if (!accepted || plan is null || light is null)
        {
            _advancedLaneRejectedGroups++;
            if (requiresStrictGpu)
                _advancedLaneLastDeclineReason = reason ??
                    "The strict GPU directional shadow group was rejected.";
            else
                HoldAdvancedLane(reason ?? "The backend rejected the deferred cascade group.");
            return;
        }

        lock (_renderPlanLocks[planIndex])
        {
            bool resumed = ResumeSubmissionTrackingForDeferredWork();
            try
            {
                EnqueueDirectionalCascadePlanMemberCompletions(plan, entry, light);
                RecordDirectionalGroupedRenderEvent(
                    entry.Request,
                    entry.DirectionalGroup,
                    elapsedMs,
                    succeeded: true,
                    usedSequentialFallback: false,
                    criticalBudgetBypassUsed: criticalBypass);
            }
            finally
            {
                if (resumed)
                    EndSubmissionTracking();
            }
        }

        _advancedLaneAcceptedGroups++;
    }

    private bool TryDeferDirectionalCascadeGroupToAdvancedLane(
        ShadowAtlasRenderPlan plan,
        int planIndex,
        in ShadowAtlasRenderPlanEntry entry,
        DirectionalLightComponent light,
        in ShadowAtlasGroupedDirectionalCascadeAllocation group,
        bool criticalBypass,
        out string? declineReason)
    {
        declineReason = null;
        if (!s_advancedDirectionalShadowLaneEnabled)
        {
            declineReason = "The Advanced directional shadow lane is disabled by XRE_ADVANCED_DIRECTIONAL_SHADOW_LANE.";
            return false;
        }

        ulong renderFrameId = SubmissionTrackingRenderFrameId;
        if (_advancedLaneAuthorityFrameId != renderFrameId)
        {
            declineReason = "The current render frame has no frozen desktop Advanced shadow consumer authority.";
            return false;
        }
        if (!_advancedLaneAuthority.IsValid)
        {
            declineReason = _advancedLaneAuthorityReason ??
                "The desktop Advanced shadow consumer has no published command package.";
            return false;
        }
        if (_advancedLaneHmdViewsBeforeOwner && group.Source == ShadowRequestSource.Hmd)
        {
            declineReason = "VR eye views render before the lane owner in this frame, so HMD cascade groups use the generic path, which draws before any view.";
            return false;
        }
        if (!TryGetAdvancedLaneConsumerReadiness(_advancedLaneAuthority.Pipeline, out bool ownerReady, out string? ownerReason))
        {
            declineReason = "The Advanced pipeline instance that owns this frame has not run its directional shadow stage yet.";
            return false;
        }
        if (!ownerReady)
        {
            declineReason = ownerReason ??
                "The Advanced directional shadow stage of this frame's owner reported itself not ready at its last execution.";
            return false;
        }

        bool strictGpu = RequiresStrictDirectionalShadowLane();
        if (!strictGpu && _advancedLaneHoldOwnerFrames > 0)
        {
            declineReason = _advancedLaneLastDeclineReason ?? "The lane is held after a rejected group.";
            return false;
        }

        if (entry.Page is not { } page)
        {
            declineReason = "The cascade group has no atlas page.";
            return false;
        }

        if (page.Texture is not null || group.Encoding != EShadowMapEncoding.Depth)
        {
            declineReason = "The lane records depth-only directional pages; moment encodings stay on the generic path.";
            return false;
        }

        // The family that owns the canonical scene draws the groups of every source: casters
        // do not depend on the view, the record masks come from each group's cascade matrices,
        // and the static tiles are kept per source.
        ShadowRequestSource source = group.Source == ShadowRequestSource.Default
            ? ShadowRequestSource.Desktop
            : group.Source;

        if (_advancedLaneTargetFrameBuffer is not null &&
            !ReferenceEquals(_advancedLaneTargetFrameBuffer, page.FrameBuffer))
        {
            declineReason = "The accepted directional shadow family already targets another atlas page this frame.";
            return false;
        }

        int slot = -1;
        for (int index = 0; index < _pendingAdvancedLaneGroups.Length; index++)
        {
            if (_pendingAdvancedLaneGroups[index].State == PendingAdvancedLaneGroupState.Free)
            {
                slot = index;
                break;
            }
        }

        if (slot < 0)
        {
            declineReason = "Every lane slot is already holding a deferred group this frame.";
            return false;
        }

        AdvancedDirectionalShadowLaneRequest request = _advancedLaneRequests[slot];
        if (!light.TryBuildAdvancedDirectionalShadowLaneRequest(
                in group,
                page.FrameBuffer,
                renderFrameId,
                request,
                out declineReason))
        {
            request.Clear();
            return false;
        }

        AttachDirectionalStaticCascadeTiles(request, light, source);
        _advancedLaneTargetFrameBuffer = page.FrameBuffer;
        request.RequiresStrictGpu = strictGpu;
        request.ConsumerAuthority = _advancedLaneAuthority;
        request.PendingSlot = slot;
        ref PendingAdvancedLaneGroup pending = ref _pendingAdvancedLaneGroups[slot];
        pending.State = PendingAdvancedLaneGroupState.Pending;
        pending.PlanIndex = planIndex;
        pending.Plan = plan;
        pending.Entry = entry;
        pending.Light = light;
        pending.Request = request;
        pending.CriticalBypass = criticalBypass;
        pending.DeferredTimestamp = Stopwatch.GetTimestamp();
        _pendingAdvancedLaneGroupCount++;
        _advancedLaneDeferredGroups++;
        return true;
    }

    /// <summary>
    /// Groups still pending when the next scheduled-tile pass starts were never
    /// consumed. They stay dirty. When the owner did not run its directional shadow
    /// stage in the group's frame (for example an eye view that skipped a frame while
    /// its resources were rebuilt), the lane did not fail, so no hold starts. Otherwise
    /// a CPU-direct group can use a bounded hold; a strict GPU group must retry its GPU lane.
    /// </summary>
    private void FailUnconsumedAdvancedLaneGroups()
    {
        if (_pendingAdvancedLaneGroupCount == 0)
            return;

        int unconsumed = 0;
        int ownerAbsent = 0;
        bool strictGroup = false;
        for (int slot = 0; slot < _pendingAdvancedLaneGroups.Length; slot++)
        {
            ref PendingAdvancedLaneGroup pending = ref _pendingAdvancedLaneGroups[slot];
            if (pending.State == PendingAdvancedLaneGroupState.Free)
                continue;

            unconsumed++;
            if (pending.Request is { } request && !AdvancedLaneOwnerRanInFrame(request))
                ownerAbsent++;
            strictGroup |= pending.Request?.RequiresStrictGpu == true;
            ReleasePendingAdvancedLaneGroup(ref pending);
        }

        if (unconsumed == 0)
            return;

        _advancedLaneOwnerAbsentGroups += ownerAbsent;
        if (ownerAbsent == unconsumed)
        {
            _advancedLaneUnconsumedGroups += unconsumed;
            return;
        }

        _advancedLaneUnconsumedGroups += unconsumed;
        if (strictGroup || RequiresStrictDirectionalShadowLane())
            _advancedLaneLastDeclineReason =
                "The strict GPU directional shadow stage did not consume its deferred groups.";
        else
            HoldAdvancedLane("The Advanced directional shadow stage did not consume the deferred cascade groups.");
    }

    /// <summary>
    /// True when the owner named by <paramref name="request"/> ran its directional shadow
    /// stage in the request's render frame.
    /// </summary>
    private bool AdvancedLaneOwnerRanInFrame(AdvancedDirectionalShadowLaneRequest request)
    {
        XRRenderPipelineInstance? owner = request.ConsumerAuthority.Pipeline;
        for (int i = 0; i < _advancedLaneConsumerReadiness.Length; i++)
        {
            ref readonly AdvancedLaneConsumerReadiness entry = ref _advancedLaneConsumerReadiness[i];
            if (ReferenceEquals(entry.Pipeline, owner))
                return entry.FrameId == request.RenderFrameId;
        }
        return false;
    }

    private void ReleasePendingAdvancedLaneGroup(ref PendingAdvancedLaneGroup pending)
    {
        pending.Request?.Clear();
        pending.Request = null;
        pending.Plan = null;
        pending.Light = null;
        pending.Entry = default;
        pending.State = PendingAdvancedLaneGroupState.Free;
        if (_pendingAdvancedLaneGroupCount > 0)
            _pendingAdvancedLaneGroupCount--;
    }

    private void HoldAdvancedLane(string reason)
    {
        _advancedLaneLastDeclineReason = reason;
        _advancedLaneHoldOwnerFrames = AdvancedLaneFailureHoldFrames;
        XREngine.Debug.LightingWarningEvery(
            $"ShadowAtlas.AdvancedDirectionalShadowLane.Hold.{GetHashCode()}",
            TimeSpan.FromSeconds(2.0),
            "[ShadowAtlas] Directional cascade groups return to the generic path for {0} frames with a lane owner: {1}",
            AdvancedLaneFailureHoldFrames,
            reason);
    }

    private bool RequiresStrictDirectionalShadowLane()
    {
        if (_advancedLaneAuthorityFrameId == SubmissionTrackingRenderFrameId)
            return !_advancedLaneGenericFallbackAllowed;
        if (!_advancedLaneOwnerExpected)
            return false;
        return true;
    }

    /// <summary>
    /// Update frames after the last dynamic caster change in a cascade during which the
    /// cascade keeps its static caster tile. After that the tile is released.
    /// </summary>
    private const ulong DirectionalStaticTileKeepFrames = 600UL;

    /// <summary>
    /// Attaches a static caster tile to each cascade of a group (desktop or HMD source) that a dynamic caster
    /// change marked within <see cref="DirectionalStaticTileKeepFrames"/> update frames, or
    /// records why the cache does not apply to a cascade. A tile is created on first use and
    /// created again when the inner tile size changes. The tiles of this light whose cascade
    /// had no dynamic change for that time are released. It allocates only when it creates a
    /// tile.
    /// </summary>
    private void AttachDirectionalStaticCascadeTiles(
        AdvancedDirectionalShadowLaneRequest request,
        DirectionalLightComponent light,
        ShadowRequestSource source)
    {
        bool enabled = RuntimeEngine.Rendering.Settings.DirectionalShadowStaticCacheEnabled;
        ulong frame = RuntimeRenderingHostServices.FrameTiming.UpdateFrameId;
        ReleaseUnusedDirectionalStaticCascadeTiles(request.LightId, light, source, frame, enabled);

        ReadOnlySpan<int> cascades = request.CascadeIndices;
        ReadOnlySpan<BoundingRectangle> rects = request.TileRects;
        AbstractRenderer? renderer = AbstractRenderer.Current;
        for (int entry = 0; entry < cascades.Length; entry++)
        {
            int cascade = cascades[entry];
            if (!enabled)
            {
                request.SetStaticCacheCause(entry, EDirectionalShadowStaticCacheCause.Disabled);
                continue;
            }
            if (!light.HasRecentDirectionalCascadeDynamicChange(source, cascade, frame, DirectionalStaticTileKeepFrames))
            {
                request.SetStaticCacheCause(entry, EDirectionalShadowStaticCacheCause.NoDynamicCasters);
                continue;
            }

            BoundingRectangle rect = rects[entry];
            uint width = (uint)Math.Max(rect.Width, 0);
            uint height = (uint)Math.Max(rect.Height, 0);
            DirectionalCascadeStaticTile? tile = FindDirectionalStaticCascadeTile(request.LightId, source, cascade, out int slot);
            if (tile is not null && (tile.Width != width || tile.Height != height))
            {
                tile.Destroy();
                _directionalStaticCascadeTiles[slot] = null;
                tile = null;
            }
            if (tile is null)
            {
                if (slot < 0)
                {
                    request.SetStaticCacheCause(entry, EDirectionalShadowStaticCacheCause.TileUnavailable);
                    continue;
                }
                tile = new DirectionalCascadeStaticTile(request.LightId, source, cascade, width, height);
                _directionalStaticCascadeTiles[slot] = tile;
                DirectionalShadowStaticCacheDiagnostics.RecordTileCreated();
            }

            // The backend lane can only record a target whose wrappers exist. Wrapper
            // creation happens here, outside command recording; later calls find them.
            renderer?.GetOrCreateAPIRenderObject(tile.DepthTexture, generateNow: true);
            renderer?.GetOrCreateAPIRenderObject(tile.FrameBuffer, generateNow: true);
            request.SetStaticCache(entry, tile, light.GetDirectionalCascadeStaticCasterEpoch(source, cascade));
        }
    }

    /// <summary>
    /// Releases the static tiles of <paramref name="lightId"/> and <paramref name="source"/> whose cascade had no dynamic
    /// caster change for <see cref="DirectionalStaticTileKeepFrames"/> update frames, or all of
    /// them when the cache does not apply (<paramref name="keep"/> false).
    /// </summary>
    private void ReleaseUnusedDirectionalStaticCascadeTiles(
        Guid lightId,
        DirectionalLightComponent light,
        ShadowRequestSource source,
        ulong frame,
        bool keep)
    {
        for (int i = 0; i < _directionalStaticCascadeTiles.Length; i++)
        {
            DirectionalCascadeStaticTile? tile = _directionalStaticCascadeTiles[i];
            if (tile is null || tile.LightId != lightId || tile.Source != source)
                continue;
            if (keep && light.HasRecentDirectionalCascadeDynamicChange(source, tile.CascadeIndex, frame, DirectionalStaticTileKeepFrames))
                continue;

            tile.Destroy();
            _directionalStaticCascadeTiles[i] = null;
            DirectionalShadowStaticCacheDiagnostics.RecordTileReleased();
        }
    }

    /// <summary>
    /// Returns the static tile of one cascade of <paramref name="lightId"/> and <paramref name="source"/>.
    /// When there is none, <paramref name="slot"/> is a free slot for a new tile, or -1 when all
    /// slots are used.
    /// </summary>
    private DirectionalCascadeStaticTile? FindDirectionalStaticCascadeTile(Guid lightId, ShadowRequestSource source, int cascadeIndex, out int slot)
    {
        slot = -1;
        for (int i = 0; i < _directionalStaticCascadeTiles.Length; i++)
        {
            DirectionalCascadeStaticTile? tile = _directionalStaticCascadeTiles[i];
            if (tile is null)
            {
                if (slot < 0)
                    slot = i;
                continue;
            }
            if (tile.LightId == lightId && tile.Source == source && tile.CascadeIndex == cascadeIndex)
            {
                slot = i;
                return tile;
            }
        }
        return null;
    }

    /// <summary>
    /// Invalidates the static tile of a cascade when a submission that held that cascade
    /// failed, so the next refresh draws its static casters again.
    /// </summary>
    private void InvalidateDirectionalStaticCascadeTile(in ShadowRequestKey key)
    {
        if (key.ProjectionType != EShadowProjectionType.DirectionalCascade)
            return;
        ShadowRequestSource source = key.Source == ShadowRequestSource.Default ? ShadowRequestSource.Desktop : key.Source;
        FindDirectionalStaticCascadeTile(key.LightId, source, key.FaceOrCascadeIndex, out _)?.Invalidate();
    }

    private static AdvancedDirectionalShadowLaneRequest[] CreateAdvancedLaneRequests()
    {
        AdvancedDirectionalShadowLaneRequest[] requests =
            new AdvancedDirectionalShadowLaneRequest[MaxPendingAdvancedLaneGroups];
        for (int index = 0; index < requests.Length; index++)
            requests[index] = new AdvancedDirectionalShadowLaneRequest();
        return requests;
    }

    private static bool ReadAdvancedDirectionalShadowLaneFlag()
    {
        string? value = Environment.GetEnvironmentVariable(
            XREngineEnvironmentVariables.AdvancedDirectionalShadowLane);
        return !string.Equals(value, "0", StringComparison.Ordinal) &&
               !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Counters describing the directional cascade hand-off to the Advanced lane.</summary>
public readonly record struct AdvancedDirectionalShadowLaneDiagnostics(
    bool Enabled,
    bool ConsumerReady,
    ulong ConsumerFrameId,
    int HoldOwnerFramesRemaining,
    long DeferredGroups,
    long AcceptedGroups,
    long RejectedGroups,
    long UnconsumedGroups,
    long OwnerAbsentGroups,
    long GenericGroups,
    string? LastDeclineReason,
    string? CasterProducer,
    string? CasterProducerReason);
