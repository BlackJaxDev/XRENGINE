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
    private const ulong AdvancedLaneFailureHoldFrames = 60UL;

    private static readonly bool s_advancedDirectionalShadowLaneEnabled =
        ReadAdvancedDirectionalShadowLaneFlag();

    private readonly PendingAdvancedLaneGroup[] _pendingAdvancedLaneGroups =
        new PendingAdvancedLaneGroup[MaxPendingAdvancedLaneGroups];
    private readonly AdvancedDirectionalShadowLaneRequest[] _advancedLaneRequests =
        CreateAdvancedLaneRequests();
    private int _pendingAdvancedLaneGroupCount;
    private ulong _advancedLaneConsumerFrameId;
    private ulong _advancedLaneTargetFrameId;
    private XRFrameBuffer? _advancedLaneTargetFrameBuffer;
    private bool _advancedLaneConsumerReady;
    private string? _advancedLaneConsumerReason;
    private AdvancedDirectionalShadowConsumerAuthority _advancedLaneAuthority;
    private ulong _advancedLaneAuthorityFrameId;
    private bool _advancedLaneOwnerExpected;
    private bool _advancedLaneGenericFallbackAllowed = true;
    private bool _advancedLaneAuthorityConflict;
    private string? _advancedLaneAuthorityReason;
    private ulong _advancedLaneHoldUntilFrameId;
    private string? _advancedLaneLastDeclineReason;
    private long _advancedLaneDeferredGroups;
    private long _advancedLaneAcceptedGroups;
    private long _advancedLaneRejectedGroups;
    private long _advancedLaneUnconsumedGroups;
    private long _advancedLaneGenericGroups;

    private enum PendingAdvancedLaneGroupState : byte
    {
        Free = 0,
        Pending,
        Dequeued,
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

    /// <summary>Lane counters and the last decline reason, for diagnostics.</summary>
    public AdvancedDirectionalShadowLaneDiagnostics CaptureAdvancedDirectionalShadowLaneDiagnostics()
        => new(
            s_advancedDirectionalShadowLaneEnabled,
            _advancedLaneConsumerReady,
            _advancedLaneConsumerFrameId,
            _advancedLaneHoldUntilFrameId,
            _advancedLaneDeferredGroups,
            _advancedLaneAcceptedGroups,
            _advancedLaneRejectedGroups,
            _advancedLaneUnconsumedGroups,
            _advancedLaneGenericGroups,
            _advancedLaneLastDeclineReason ?? _advancedLaneConsumerReason);

    /// <summary>
    /// Called by the Advanced directional shadow stage every time it executes.
    /// Groups are deferred in a later frame only while the consumer keeps
    /// reporting itself ready. A strict GPU request stays dirty without it.
    /// </summary>
    public void NotifyAdvancedDirectionalShadowLaneConsumer(
        ulong renderFrameId,
        bool ready,
        string? reason)
    {
        AssertRenderThread();
        _advancedLaneConsumerFrameId = renderFrameId;
        _advancedLaneConsumerReady = ready;
        _advancedLaneConsumerReason = ready ? null : reason;
    }

    /// <summary>Publishes the desktop consumer selected before shadow scheduling.</summary>
    internal void PublishAdvancedDirectionalShadowConsumerAuthority(
        ulong renderFrameId,
        in AdvancedDirectionalShadowConsumerAuthority authority,
        bool ownerExpected,
        bool genericFallbackAllowed,
        string? reason)
    {
        AssertRenderThread();
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
    }

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
        if (!_advancedLaneConsumerReady || _advancedLaneConsumerFrameId + 1UL < renderFrameId)
        {
            declineReason = _advancedLaneConsumerReason ??
                "No Advanced directional shadow stage reported itself ready in the previous frame.";
            return false;
        }

        bool strictGpu = RequiresStrictDirectionalShadowLane();
        if (!strictGpu && renderFrameId < _advancedLaneHoldUntilFrameId)
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

        ShadowRequestSource source = group.Source == ShadowRequestSource.Default
            ? ShadowRequestSource.Desktop
            : group.Source;
        if (source != ShadowRequestSource.Desktop)
        {
            declineReason = "Only desktop cascade groups are consumed by the desktop Advanced family.";
            return false;
        }

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
    /// consumed. They stay dirty. A CPU-direct group can use a bounded hold;
    /// a strict GPU group must retry its GPU lane.
    /// </summary>
    private void FailUnconsumedAdvancedLaneGroups()
    {
        if (_pendingAdvancedLaneGroupCount == 0)
            return;

        int unconsumed = 0;
        bool strictGroup = false;
        for (int slot = 0; slot < _pendingAdvancedLaneGroups.Length; slot++)
        {
            ref PendingAdvancedLaneGroup pending = ref _pendingAdvancedLaneGroups[slot];
            if (pending.State == PendingAdvancedLaneGroupState.Free)
                continue;

            unconsumed++;
            strictGroup |= pending.Request?.RequiresStrictGpu == true;
            ReleasePendingAdvancedLaneGroup(ref pending);
        }

        if (unconsumed == 0)
            return;

        _advancedLaneUnconsumedGroups += unconsumed;
        if (strictGroup || RequiresStrictDirectionalShadowLane())
            _advancedLaneLastDeclineReason =
                "The strict GPU directional shadow stage did not consume its deferred groups.";
        else
            HoldAdvancedLane("The Advanced directional shadow stage did not consume the deferred cascade groups.");
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
        _advancedLaneHoldUntilFrameId = SubmissionTrackingRenderFrameId + AdvancedLaneFailureHoldFrames;
        XREngine.Debug.LightingWarningEvery(
            $"ShadowAtlas.AdvancedDirectionalShadowLane.Hold.{GetHashCode()}",
            TimeSpan.FromSeconds(2.0),
            "[ShadowAtlas] Directional cascade groups return to the generic path for {0} frames: {1}",
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
    ulong HoldUntilFrameId,
    long DeferredGroups,
    long AcceptedGroups,
    long RejectedGroups,
    long UnconsumedGroups,
    long GenericGroups,
    string? LastDeclineReason);
