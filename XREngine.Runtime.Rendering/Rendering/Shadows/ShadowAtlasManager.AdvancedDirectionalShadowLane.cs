using System.Diagnostics;
using XREngine.Components.Lights;

namespace XREngine.Rendering.Shadows;

/// <summary>
/// Hand-off of scheduled directional cascade groups to the Advanced directional
/// shadow raster stage. The atlas manager keeps allocation, dirty tracking,
/// completion receipts and the generic per-renderer path; the lane only
/// replaces the recording of a group's casters when a consumer stage reported
/// itself ready in the previous render frame. Everything here runs on the
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
    /// reporting itself ready; a frame without a consumer renders generically.
    /// </summary>
    public void NotifyAdvancedDirectionalShadowLaneConsumer(ulong renderFrameId, bool ready, string? reason)
    {
        AssertRenderThread();
        _advancedLaneConsumerFrameId = renderFrameId;
        _advancedLaneConsumerReady = ready;
        _advancedLaneConsumerReason = ready ? null : reason;
    }

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
    /// grouped render does; a rejected group stays dirty, renders generically
    /// from the next frame, and holds the lane closed for a bounded number of
    /// frames so a persistent backend failure cannot starve the tiles.
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
        ReleasePendingAdvancedLaneGroup(ref pending);

        if (!accepted || plan is null || light is null)
        {
            _advancedLaneRejectedGroups++;
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
        if (!_advancedLaneConsumerReady || _advancedLaneConsumerFrameId + 1UL < renderFrameId)
        {
            declineReason = _advancedLaneConsumerReason ??
                "No Advanced directional shadow stage reported itself ready in the previous frame.";
            return false;
        }

        if (renderFrameId < _advancedLaneHoldUntilFrameId)
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
    /// consumed (the stage did not execute, or faulted before completing them).
    /// They stay dirty and render generically; the lane is held closed briefly.
    /// </summary>
    private void FailUnconsumedAdvancedLaneGroups()
    {
        if (_pendingAdvancedLaneGroupCount == 0)
            return;

        int unconsumed = 0;
        for (int slot = 0; slot < _pendingAdvancedLaneGroups.Length; slot++)
        {
            ref PendingAdvancedLaneGroup pending = ref _pendingAdvancedLaneGroups[slot];
            if (pending.State == PendingAdvancedLaneGroupState.Free)
                continue;

            unconsumed++;
            ReleasePendingAdvancedLaneGroup(ref pending);
        }

        if (unconsumed == 0)
            return;

        _advancedLaneUnconsumedGroups += unconsumed;
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
