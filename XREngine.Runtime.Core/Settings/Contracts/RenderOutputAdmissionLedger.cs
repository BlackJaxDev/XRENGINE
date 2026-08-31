namespace XREngine;

/// <summary>
/// Allocation-free persistent admission history for engine output requests.
/// It publishes cadence/reuse decisions; the Vulkan frame planner remains the
/// sole executable output DAG and ordering authority.
/// </summary>
public sealed class RenderOutputAdmissionLedger
{
    private struct Entry
    {
        internal ulong OutputId;
        internal ulong ProductCompatibilityKey;
        internal ulong LastFrameId;
        internal RenderOutputDagNodeStatus Status;
    }

    private readonly Entry[] _entries;
    private long _compatibilityResetCount;
    private long _obsoleteCompletionCount;

    public RenderOutputAdmissionLedger(int capacity = 512)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _entries = new Entry[capacity];
    }

    public long CompatibilityResetCount => Interlocked.Read(ref _compatibilityResetCount);
    public long ObsoleteCompletionCount => Interlocked.Read(ref _obsoleteCompletionCount);

    public RenderOutputSchedulingDecision Plan(
        in RenderOutputRequest request,
        bool isDue,
        ERenderOutputPolicyReason deferralReason)
    {
        int index = FindOrCreateForPlanning(request);
        ref Entry entry = ref _entries[index];
        BeginFrame(ref entry, request.FrameId);
        bool xrCritical = request.OutputClass == ERenderOutputClass.XrCritical;
        if (isDue)
            return RenderOutputSchedulingDecision.Fresh(xrCritical);

        uint maximumDeferrals = request.Schedule.MaxContentAgeFrames;
        RenderOutputDagNodeStatus status = entry.Status;
        bool forceRefresh = !status.HasCompletedResult ||
            maximumDeferrals != uint.MaxValue &&
            status.ConsecutiveDeferrals >= maximumDeferrals;
        if (forceRefresh)
        {
            return new(
                Execute: true,
                ERenderOutputWorkDisposition.FreshRender,
                ERenderOutputPolicyReason.MaximumDeferralReached,
                status.ContentAgeFrames,
                xrCritical,
                ForcedRefresh: true);
        }

        if ((request.FallbackPolicy & ERenderOutputFallbackPolicy.AllowStaleReuse) != 0 &&
            status.ContentAgeFrames <= maximumDeferrals)
        {
            entry.Status = status with
            {
                State = ERenderOutputNodeState.Reused,
                AuthorizedReuse = true,
                Disposition = ERenderOutputWorkDisposition.ReusedStale,
                PolicyReason = ERenderOutputPolicyReason.HeldLastImage,
                ConsecutiveDeferrals = Increment(status.ConsecutiveDeferrals),
            };
            return new(
                Execute: false,
                ERenderOutputWorkDisposition.ReusedStale,
                ERenderOutputPolicyReason.HeldLastImage,
                status.ContentAgeFrames,
                xrCritical,
                ForcedRefresh: false);
        }

        ERenderOutputPolicyReason reason = deferralReason == ERenderOutputPolicyReason.None
            ? ERenderOutputPolicyReason.Cadence
            : deferralReason;
        ERenderOutputWorkDisposition disposition =
            (request.FallbackPolicy &
             (ERenderOutputFallbackPolicy.AllowCadenceReduction |
              ERenderOutputFallbackPolicy.AllowBudgetDeferral)) != 0
                ? ERenderOutputWorkDisposition.Deferred
                : ERenderOutputWorkDisposition.Skipped;
        entry.Status = status with
        {
            State = disposition == ERenderOutputWorkDisposition.Deferred
                ? ERenderOutputNodeState.Deferred
                : ERenderOutputNodeState.Skipped,
            AuthorizedReuse = false,
            Disposition = disposition,
            PolicyReason = reason,
            ConsecutiveDeferrals = disposition == ERenderOutputWorkDisposition.Deferred
                ? Increment(status.ConsecutiveDeferrals)
                : status.ConsecutiveDeferrals,
        };
        return new(
            Execute: false,
            disposition,
            reason,
            status.ContentAgeFrames,
            xrCritical,
            ForcedRefresh: false);
    }

    /// <summary>
    /// Completes only the currently installed product generation. A delayed
    /// completion from an older target or frame is ignored rather than making
    /// incompatible content eligible for stale reuse.
    /// </summary>
    public bool Complete(in RenderOutputRequest request)
    {
        int index = Find(request.OutputId);
        if (index < 0)
            index = Create(request.OutputId, request.ProductCompatibilityKey);

        ref Entry entry = ref _entries[index];
        if (entry.ProductCompatibilityKey != request.ProductCompatibilityKey ||
            request.FrameId < entry.LastFrameId)
        {
            Interlocked.Increment(ref _obsoleteCompletionCount);
            return false;
        }

        BeginFrame(ref entry, request.FrameId);
        entry.Status = entry.Status with
        {
            State = ERenderOutputNodeState.Complete,
            Progress = 1.0f,
            ContentAgeFrames = 0u,
            LastCompletedFrame = unchecked((uint)request.FrameId),
            HasCompletedResult = true,
            AuthorizedReuse = false,
            Disposition = ERenderOutputWorkDisposition.FreshRender,
            PolicyReason = ERenderOutputPolicyReason.None,
            ConsecutiveDeferrals = 0u,
        };
        return true;
    }

    public bool TryGetStatus(
        in RenderOutputRequest request,
        out RenderOutputDagNodeStatus status)
    {
        int index = Find(request.OutputId);
        if (index < 0 ||
            _entries[index].ProductCompatibilityKey != request.ProductCompatibilityKey ||
            request.FrameId < _entries[index].LastFrameId)
        {
            status = default;
            return false;
        }

        ref Entry entry = ref _entries[index];
        BeginFrame(ref entry, request.FrameId);
        status = entry.Status;
        return true;
    }

    private void BeginFrame(ref Entry entry, ulong frameId)
    {
        if (entry.LastFrameId == frameId)
            return;

        if (entry.LastFrameId != 0UL && frameId < entry.LastFrameId)
            throw new InvalidOperationException(
                "Render output admission cannot move backward to an older frame.");

        ulong delta = entry.LastFrameId == 0UL || frameId <= entry.LastFrameId
            ? 1UL
            : frameId - entry.LastFrameId;
        RenderOutputDagNodeStatus previous = entry.Status;
        uint age = previous.HasCompletedResult
            ? (uint)Math.Min(uint.MaxValue, previous.ContentAgeFrames + delta)
            : previous.ContentAgeFrames;
        entry.LastFrameId = frameId;
        entry.Status = previous with
        {
            State = ERenderOutputNodeState.Pending,
            Progress = 0.0f,
            ContentAgeFrames = age,
            AuthorizedReuse = false,
            Disposition = ERenderOutputWorkDisposition.FreshRender,
            PolicyReason = ERenderOutputPolicyReason.None,
        };
    }

    private int FindOrCreateForPlanning(in RenderOutputRequest request)
    {
        int existing = Find(request.OutputId);
        if (existing >= 0)
        {
            ref Entry entry = ref _entries[existing];
            if (entry.ProductCompatibilityKey != request.ProductCompatibilityKey)
            {
                entry = new Entry
                {
                    OutputId = request.OutputId,
                    ProductCompatibilityKey = request.ProductCompatibilityKey,
                };
                Interlocked.Increment(ref _compatibilityResetCount);
            }
            return existing;

        }

        return Create(request.OutputId, request.ProductCompatibilityKey);
    }

    private int Create(ulong outputId, ulong productCompatibilityKey)
    {
        for (int index = 0; index < _entries.Length; index++)
        {
            if (_entries[index].OutputId != 0UL)
                continue;
            _entries[index] = new Entry
            {
                OutputId = outputId,
                ProductCompatibilityKey = productCompatibilityKey,
            };
            return index;
        }

        throw new InvalidOperationException(
            $"Render output admission capacity {_entries.Length} was exceeded; output history is never evicted implicitly.");
    }

    private int Find(ulong outputId)
    {
        for (int index = 0; index < _entries.Length; index++)
            if (_entries[index].OutputId == outputId)
                return index;
        return -1;
    }

    private static uint Increment(uint value)
        => value == uint.MaxValue ? uint.MaxValue : value + 1u;
}
