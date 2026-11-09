using XREngine.Execution;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// One desktop consumer's asynchronous recording, owned by the frame loop and
/// reused for every desktop frame. It holds exact identities for the lane arena,
/// scheduler slot and frame-data slot, and it keeps a pending use on the lane arena
/// from preparation until the output is accepted by a queue submission or
/// discarded. It never refers to the stack-local frame attempt. A CPU join does not
/// end the pending use: recorded output is still unprotected until submission.
/// </summary>
internal sealed class VulkanPendingDesktopRecord
{
    private static long s_nextOwnerGeneration;

    private VulkanLaneCommandFamilyArena? _arena;
    private RenderWorkBatchLease _lease;

    internal EVulkanPendingDesktopRecordPhase Phase { get; private set; }

    /// <summary>Exact owner identity for the arena pending use; 0 when idle.</summary>
    internal long OwnerGeneration { get; private set; }

    internal int LaneId { get; private set; } = -1;
    internal int SchedulerFrameSlot { get; private set; } = -1;
    internal uint FrameDataSlot { get; private set; }
    internal RenderWorkBatchResult JoinResult { get; private set; }

    /// <summary>True while the record holds CPU work or unsubmitted output.</summary>
    internal bool HasUnresolvedWork => Phase != EVulkanPendingDesktopRecordPhase.Idle;

    internal ref RenderWorkBatchLease Lease => ref _lease;

    /// <summary>
    /// Starts a record for one lane and slot, and takes the arena's pending use
    /// before any backend preparation touches it.
    /// </summary>
    internal void BeginPreparation(
        VulkanLaneCommandFamilyArena arena,
        int laneId,
        int schedulerFrameSlot,
        uint frameDataSlot)
    {
        ArgumentNullException.ThrowIfNull(arena);
        RequirePhase(EVulkanPendingDesktopRecordPhase.Idle);
        if (arena.LaneId != laneId || arena.FrameSlot != schedulerFrameSlot)
        {
            throw new ArgumentException(
                $"Arena lane {arena.LaneId}, slot {arena.FrameSlot} does not match record lane {laneId}, slot {schedulerFrameSlot}.",
                nameof(arena));
        }

        long generation = Interlocked.Increment(ref s_nextOwnerGeneration);
        arena.AcquirePendingUse(generation);
        _arena = arena;
        OwnerGeneration = generation;
        LaneId = laneId;
        SchedulerFrameSlot = schedulerFrameSlot;
        FrameDataSlot = frameDataSlot;
        JoinResult = default;
        Phase = EVulkanPendingDesktopRecordPhase.Preparing;
    }

    /// <summary>Records that the CPU batch for this record was published.</summary>
    internal void MarkSubmitted(in RenderWorkBatchLease lease)
    {
        RequirePhase(EVulkanPendingDesktopRecordPhase.Preparing);
        if (!lease.IsValid)
            throw new ArgumentException("A valid submitted lease is required.", nameof(lease));

        _lease = lease;
        Phase = EVulkanPendingDesktopRecordPhase.Submitted;
    }

    /// <summary>
    /// Records the join result. The pending use stays: recorded output is not yet
    /// protected by a queue submission.
    /// </summary>
    internal void MarkJoined(in RenderWorkBatchResult result)
    {
        RequirePhase(EVulkanPendingDesktopRecordPhase.Submitted);
        JoinResult = result;
        Phase = EVulkanPendingDesktopRecordPhase.Recorded;
    }

    /// <summary>
    /// Ends the record after the queue accepted its output. The submission now
    /// protects the commands, so the arena pending use is released without a gap.
    /// </summary>
    internal void CompleteAfterAcceptedSubmission()
    {
        RequirePhase(EVulkanPendingDesktopRecordPhase.Recorded);
        if (!JoinResult.Succeeded)
            throw new InvalidOperationException("Only successfully recorded output can be submitted.");

        Release();
    }

    /// <summary>
    /// Ends a record whose output will not be submitted: a preparation failure, or
    /// joined output that is stale, canceled or faulted. A submitted batch must be
    /// joined first, because its worker can still use the arena.
    /// </summary>
    internal void Discard()
    {
        if (Phase == EVulkanPendingDesktopRecordPhase.Submitted)
            throw new InvalidOperationException("A submitted desktop record must be joined before it is discarded.");
        if (Phase == EVulkanPendingDesktopRecordPhase.Idle)
            return;

        Release();
    }

    private void Release()
    {
        _arena?.ReleasePendingUse(OwnerGeneration);
        _arena = null;
        _lease = default;
        OwnerGeneration = 0L;
        LaneId = -1;
        SchedulerFrameSlot = -1;
        FrameDataSlot = 0;
        Phase = EVulkanPendingDesktopRecordPhase.Idle;
    }

    private void RequirePhase(EVulkanPendingDesktopRecordPhase expected)
    {
        if (Phase != expected)
            throw new InvalidOperationException($"Desktop record phase is {Phase}, not {expected}.");
    }
}
