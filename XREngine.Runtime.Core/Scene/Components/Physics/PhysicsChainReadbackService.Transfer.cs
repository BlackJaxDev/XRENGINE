namespace XREngine.Components;

internal sealed partial class PhysicsChainReadbackService
{
    private enum StagingSlotState : byte
    {
        Free,
        Filling,
        AwaitingFence,
        Ready,
    }

    private sealed class StagingSlot
    {
        public StagingSlotState State;
        public uint Generation;
        public PhysicsChainReadbackGatherPlan? Plan;
        public IPhysicsChainReadbackFence? Fence;
        public IPhysicsChainReadbackStagingSource? Source;
        public byte[] DeliveryScratch = [];
    }

    private readonly StagingSlot[] _stagingSlots =
    [
        new(),
        new(),
        new(),
    ];
    private int _nextStagingSlot;

    private long _requestedTransferElements;
    private long _requestedTransferBytes;
    private long _gatheredElements;
    private long _gatheredBytes;
    private long _transferredElements;
    private long _transferredBytes;
    private long _discardedStaleElements;
    private long _discardedStaleBytes;
    private long _deliveredElements;
    private long _deliveredBytes;
    private long _latencyOneFrame;
    private long _latencyTwoFrames;
    private long _latencyThreeFrames;
    private long _latencyFourFrames;
    private long _latencyFiveToEightFrames;
    private long _latencyNineOrMoreFrames;

    public bool TryBuildGatherPlan(
        PhysicsChainReadbackHandle handle,
        PhysicsChainReadbackSourceEpoch sourceEpoch,
        long gatherFrame,
        out PhysicsChainReadbackGatherPlan? plan,
        out PhysicsChainReadbackTransferFailure failure)
    {
        plan = null;
        failure = PhysicsChainReadbackTransferFailure.None;
        if (!sourceEpoch.IsValid)
        {
            failure = PhysicsChainReadbackTransferFailure.InvalidEpoch;
            return false;
        }
        if (gatherFrame < 0L)
        {
            failure = PhysicsChainReadbackTransferFailure.InvalidFrame;
            return false;
        }
        if (!TryGet(handle, out PhysicsChainReadbackRequestInfo? info) || info is null)
        {
            failure = PhysicsChainReadbackTransferFailure.InvalidRequest;
            return false;
        }
        if (info.Status != PhysicsChainReadbackStatus.Pending || info.ActiveGatherPlan is not null)
        {
            failure = PhysicsChainReadbackTransferFailure.RequestNotPending;
            return false;
        }
        if (gatherFrame < info.SubmissionFrame || gatherFrame >= info.ExpiryFrame)
        {
            failure = PhysicsChainReadbackTransferFailure.InvalidFrame;
            return false;
        }
        if (GetAvailablePlanCapacity() <= 0)
        {
            failure = PhysicsChainReadbackTransferFailure.NoStagingSlot;
            return false;
        }
        if (!TryCreateGatherItems(info, out PhysicsChainReadbackGatherItem[] items, out int byteCount))
        {
            failure = PhysicsChainReadbackTransferFailure.LayoutOverflow;
            return false;
        }
        if (items.Length != info.ExpectedElementCount || byteCount != info.ExpectedByteCount)
        {
            failure = PhysicsChainReadbackTransferFailure.ByteCountMismatch;
            return false;
        }

        plan = new PhysicsChainReadbackGatherPlan
        {
            RequestHandle = handle,
            InstanceHandle = info.InstanceHandle,
            SourceEpoch = sourceEpoch,
            GatherFrame = gatherFrame,
            ElementCount = items.Length,
            ByteCount = byteCount,
            Items = items,
        };
        info.ActiveGatherPlan = plan;
        return true;
    }

    public int BuildPendingGatherPlans(
        PhysicsChainReadbackSourceEpoch sourceEpoch,
        long gatherFrame,
        Span<PhysicsChainReadbackGatherPlan?> destination)
    {
        if (!sourceEpoch.IsValid)
            throw new ArgumentOutOfRangeException(nameof(sourceEpoch));
        if (gatherFrame < 0L)
            throw new ArgumentOutOfRangeException(nameof(gatherFrame));

        int planCount = 0;
        int availableSlotCount = GetAvailablePlanCapacity();
        for (int i = 0; i < _liveHandles.Count && planCount < destination.Length && planCount < availableSlotCount; ++i)
        {
            PhysicsChainReadbackHandle handle = _liveHandles[i];
            if (!TryGet(handle, out PhysicsChainReadbackRequestInfo? info)
                || info is null
                || info.Status != PhysicsChainReadbackStatus.Pending
                || info.ActiveGatherPlan is not null
                || gatherFrame < info.SubmissionFrame
                || gatherFrame >= info.ExpiryFrame)
                continue;

            if (TryBuildGatherPlan(handle, sourceEpoch, gatherFrame, out PhysicsChainReadbackGatherPlan? plan, out _))
                destination[planCount++] = plan;
        }
        return planCount;
    }

    public bool TryAcquireStagingSlot(
        PhysicsChainReadbackGatherPlan plan,
        out PhysicsChainReadbackStagingLease lease,
        out PhysicsChainReadbackTransferFailure failure)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lease = default;
        failure = PhysicsChainReadbackTransferFailure.None;
        if (!TryGet(plan.RequestHandle, out PhysicsChainReadbackRequestInfo? info)
            || info is null
            || !ReferenceEquals(info.ActiveGatherPlan, plan))
        {
            failure = PhysicsChainReadbackTransferFailure.InvalidRequest;
            return false;
        }
        if (info.Status != PhysicsChainReadbackStatus.Pending)
        {
            failure = PhysicsChainReadbackTransferFailure.RequestNotPending;
            return false;
        }

        for (int offset = 0; offset < _stagingSlots.Length; ++offset)
        {
            int slotIndex = (_nextStagingSlot + offset) % _stagingSlots.Length;
            StagingSlot slot = _stagingSlots[slotIndex];
            if (slot.State != StagingSlotState.Free)
                continue;

            slot.Generation = NextStagingGeneration(slot.Generation);
            slot.Plan = plan;
            slot.Fence = null;
            slot.State = StagingSlotState.Filling;
            _nextStagingSlot = (slotIndex + 1) % _stagingSlots.Length;
            lease = new PhysicsChainReadbackStagingLease(
                slotIndex,
                slot.Generation,
                plan.RequestHandle,
                plan.ByteCount);
            info.Status = PhysicsChainReadbackStatus.InFlight;
            return true;
        }

        failure = PhysicsChainReadbackTransferFailure.NoStagingSlot;
        return false;
    }

    /// <summary>
    /// Managed CPU/testing convenience path. GPU backends must use the
    /// staging-source overload so mapping occurs only after fence completion.
    /// </summary>
    public bool CommitStagingSlot(
        PhysicsChainReadbackStagingLease lease,
        ReadOnlySpan<byte> packedData,
        IPhysicsChainReadbackFence fence,
        long transferFrame,
        out PhysicsChainReadbackTransferFailure failure)
    {
        var source = new PhysicsChainReadbackMemorySource(packedData.ToArray());
        if (CommitStagingSlot(lease, source, fence, transferFrame, out failure))
            return true;

        source.Dispose();
        return false;
    }

    public bool CommitStagingSlot(
        PhysicsChainReadbackStagingLease lease,
        IPhysicsChainReadbackStagingSource source,
        IPhysicsChainReadbackFence fence,
        long transferFrame,
        out PhysicsChainReadbackTransferFailure failure)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fence);
        failure = PhysicsChainReadbackTransferFailure.None;
        if (!TryGetStagingSlot(lease, StagingSlotState.Filling, out StagingSlot? slot)
            || slot?.Plan is not PhysicsChainReadbackGatherPlan plan)
        {
            failure = PhysicsChainReadbackTransferFailure.InvalidLease;
            return false;
        }
        if (transferFrame < plan.GatherFrame)
        {
            failure = PhysicsChainReadbackTransferFailure.InvalidFrame;
            return false;
        }
        if (source.ByteCount != plan.ByteCount || source.ByteCount != lease.ByteCount)
        {
            failure = PhysicsChainReadbackTransferFailure.ByteCountMismatch;
            return false;
        }

        slot.Source = source;
        slot.Fence = fence;
        slot.State = StagingSlotState.AwaitingFence;
        _gatheredElements += plan.ElementCount;
        _gatheredBytes += plan.ByteCount;
        return true;
    }

    public bool AbandonStagingSlot(PhysicsChainReadbackStagingLease lease)
    {
        if (!TryGetStagingSlot(lease, StagingSlotState.Filling, out StagingSlot? slot)
            || slot?.Plan is not PhysicsChainReadbackGatherPlan plan)
            return false;

        if (TryGet(plan.RequestHandle, out PhysicsChainReadbackRequestInfo? info)
            && info is not null
            && ReferenceEquals(info.ActiveGatherPlan, plan)
            && info.Status == PhysicsChainReadbackStatus.InFlight)
        {
            info.ActiveGatherPlan = null;
            info.Status = PhysicsChainReadbackStatus.Pending;
        }
        ReleaseStagingSlot(slot);
        return true;
    }

    public bool FailStagingSlot(PhysicsChainReadbackStagingLease lease, long completionFrame)
    {
        if (completionFrame < 0L
            || !TryGetStagingSlot(lease, StagingSlotState.Filling, out StagingSlot? slot)
            || slot?.Plan is not PhysicsChainReadbackGatherPlan plan)
            return false;

        if (TryGet(plan.RequestHandle, out PhysicsChainReadbackRequestInfo? info)
            && info is not null
            && ReferenceEquals(info.ActiveGatherPlan, plan))
        {
            info.Status = PhysicsChainReadbackStatus.Failed;
            info.CompletionFrame = completionFrame;
            info.ActiveGatherPlan = null;
        }
        ReleaseStagingSlot(slot);
        return true;
    }

    private bool TryCancelUncommittedTransfer(PhysicsChainReadbackHandle handle)
    {
        for (int i = 0; i < _stagingSlots.Length; ++i)
        {
            StagingSlot slot = _stagingSlots[i];
            if (slot.State != StagingSlotState.Filling || slot.Plan?.RequestHandle != handle)
                continue;

            ReleaseStagingSlot(slot);
            return true;
        }
        return false;
    }

    public void PollTransfers(
        PhysicsChainWorld world,
        long currentFrame,
        PhysicsChainReadbackSourceEpoch currentEpoch)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (currentFrame < 0L)
            throw new ArgumentOutOfRangeException(nameof(currentFrame));

        for (int i = 0; i < _stagingSlots.Length; ++i)
        {
            StagingSlot slot = _stagingSlots[i];
            if (slot.State == StagingSlotState.AwaitingFence)
            {
                PhysicsChainReadbackFenceStatus status = slot.Fence!.Poll();
                if (status == PhysicsChainReadbackFenceStatus.Pending)
                    continue;
                if (status == PhysicsChainReadbackFenceStatus.Failed)
                {
                    FailTransfer(slot, currentFrame);
                    continue;
                }

                PhysicsChainReadbackGatherPlan plan = slot.Plan!;
                _transferredElements += plan.ElementCount;
                _transferredBytes += plan.ByteCount;
                slot.State = StagingSlotState.Ready;
            }

            if (slot.State == StagingSlotState.Ready)
                TryDeliverReadySlot(world, slot, currentFrame, currentEpoch);
        }
    }

    public bool TryGetResult(
        PhysicsChainReadbackHandle handle,
        out PhysicsChainReadbackResult? result)
    {
        result = null;
        if (!TryGet(handle, out PhysicsChainReadbackRequestInfo? info)
            || info is null
            || info.Status != PhysicsChainReadbackStatus.Available)
            return false;

        result = info.Result;
        return result is not null;
    }

    public PhysicsChainReadbackTransferCounters GetTransferCounters()
        => new(
            _requestedTransferElements,
            _requestedTransferBytes,
            _gatheredElements,
            _gatheredBytes,
            _transferredElements,
            _transferredBytes,
            _discardedStaleElements,
            _discardedStaleBytes,
            _deliveredElements,
            _deliveredBytes,
            new PhysicsChainReadbackLatencyHistogram(
                _latencyOneFrame,
                _latencyTwoFrames,
                _latencyThreeFrames,
                _latencyFourFrames,
                _latencyFiveToEightFrames,
                _latencyNineOrMoreFrames));

    private void RecordAcceptedTransferRequest(int elementCount, int byteCount)
    {
        _requestedTransferElements += elementCount;
        _requestedTransferBytes += byteCount;
    }

    private void TryDeliverReadySlot(
        PhysicsChainWorld world,
        StagingSlot slot,
        long currentFrame,
        PhysicsChainReadbackSourceEpoch currentEpoch)
    {
        PhysicsChainReadbackGatherPlan plan = slot.Plan!;
        if (!TryGet(plan.RequestHandle, out PhysicsChainReadbackRequestInfo? info)
            || info is null
            || !ReferenceEquals(info.ActiveGatherPlan, plan)
            || info.Status != PhysicsChainReadbackStatus.InFlight)
        {
            RecordDiscarded(plan);
            ReleaseStagingSlot(slot);
            return;
        }

        bool validSource = currentEpoch.IsValid
            && currentEpoch == plan.SourceEpoch
            && info.InstanceHandle == plan.InstanceHandle
            && world.TryResolveRuntimeHandle(plan.InstanceHandle, out _);
        if (!validSource)
        {
            info.Status = PhysicsChainReadbackStatus.DiscardedStale;
            info.CompletionFrame = currentFrame;
            info.ActiveGatherPlan = null;
            RecordDiscarded(plan);
            ReleaseStagingSlot(slot);
            return;
        }

        if (currentFrame < info.EarliestCompletionFrame)
            return;

        if (slot.DeliveryScratch.Length < plan.ByteCount)
            Array.Resize(ref slot.DeliveryScratch, plan.ByteCount);
        if (slot.Source is null || !slot.Source.TryCopyTo(slot.DeliveryScratch.AsSpan(0, plan.ByteCount)))
            return;
        byte[] delivered = new byte[plan.ByteCount];
        slot.DeliveryScratch.AsSpan(0, plan.ByteCount).CopyTo(delivered);
        long latency = Math.Max(1L, currentFrame - info.SubmissionFrame);
        info.Result = new PhysicsChainReadbackResult
        {
            Plan = plan,
            PackedData = delivered,
            DeliveryFrame = currentFrame,
            LatencyFrames = latency,
        };
        info.Status = PhysicsChainReadbackStatus.Available;
        info.CompletionFrame = currentFrame;
        info.ActiveGatherPlan = null;
        _deliveredElements += plan.ElementCount;
        _deliveredBytes += plan.ByteCount;
        RecordLatency(latency);
        ReleaseStagingSlot(slot);
    }

    private void FailTransfer(StagingSlot slot, long currentFrame)
    {
        PhysicsChainReadbackGatherPlan plan = slot.Plan!;
        if (TryGet(plan.RequestHandle, out PhysicsChainReadbackRequestInfo? info)
            && info is not null
            && ReferenceEquals(info.ActiveGatherPlan, plan)
            && info.Status == PhysicsChainReadbackStatus.InFlight)
        {
            info.Status = PhysicsChainReadbackStatus.Failed;
            info.CompletionFrame = currentFrame;
            info.ActiveGatherPlan = null;
        }
        ReleaseStagingSlot(slot);
    }

    private static bool TryCreateGatherItems(
        PhysicsChainReadbackRequestInfo info,
        out PhysicsChainReadbackGatherItem[] items,
        out int byteCount)
    {
        items = [];
        byteCount = 0;
        ReadOnlySpan<PhysicsChainReadbackElementKind> kinds = PhysicsChainReadbackLayout.GetKinds(info.Fields);
        ReadOnlySpan<int> selectedIndices = info.SelectedElementIndices.Span;
        try
        {
            items = new PhysicsChainReadbackGatherItem[checked(kinds.Length * selectedIndices.Length)];
            int itemIndex = 0;
            int destinationOffset = 0;
            for (int kindIndex = 0; kindIndex < kinds.Length; ++kindIndex)
            {
                PhysicsChainReadbackElementKind kind = kinds[kindIndex];
                int elementByteCount = PhysicsChainReadbackLayout.GetElementByteCount(kind);
                for (int selectionIndex = 0; selectionIndex < selectedIndices.Length; ++selectionIndex)
                {
                    items[itemIndex++] = new PhysicsChainReadbackGatherItem(
                        kind,
                        selectedIndices[selectionIndex],
                        destinationOffset,
                        elementByteCount);
                    destinationOffset = checked(destinationOffset + elementByteCount);
                }
            }
            byteCount = destinationOffset;
            return true;
        }
        catch (OverflowException)
        {
            items = [];
            byteCount = 0;
            return false;
        }
    }

    private bool TryGetStagingSlot(
        PhysicsChainReadbackStagingLease lease,
        StagingSlotState requiredState,
        out StagingSlot? slot)
    {
        slot = null;
        if (!lease.IsValid || (uint)lease.Slot >= (uint)_stagingSlots.Length)
            return false;

        StagingSlot candidate = _stagingSlots[lease.Slot];
        if (candidate.Generation != lease.Generation
            || candidate.State != requiredState
            || candidate.Plan?.RequestHandle != lease.RequestHandle)
            return false;

        slot = candidate;
        return true;
    }

    private int GetAvailablePlanCapacity()
    {
        int freeSlotCount = 0;
        for (int i = 0; i < _stagingSlots.Length; ++i)
            if (_stagingSlots[i].State == StagingSlotState.Free)
                ++freeSlotCount;

        int reservedPlanCount = 0;
        for (int i = 0; i < _liveHandles.Count; ++i)
        {
            if (TryGet(_liveHandles[i], out PhysicsChainReadbackRequestInfo? info)
                && info is not null
                && info.Status == PhysicsChainReadbackStatus.Pending
                && info.ActiveGatherPlan is not null)
                ++reservedPlanCount;
        }
        return Math.Max(0, freeSlotCount - reservedPlanCount);
    }

    private void RecordDiscarded(PhysicsChainReadbackGatherPlan plan)
    {
        _discardedStaleElements += plan.ElementCount;
        _discardedStaleBytes += plan.ByteCount;
    }

    private void RecordLatency(long latencyFrames)
    {
        switch (latencyFrames)
        {
            case 1L:
                ++_latencyOneFrame;
                break;
            case 2L:
                ++_latencyTwoFrames;
                break;
            case 3L:
                ++_latencyThreeFrames;
                break;
            case 4L:
                ++_latencyFourFrames;
                break;
            case <= 8L:
                ++_latencyFiveToEightFrames;
                break;
            default:
                ++_latencyNineOrMoreFrames;
                break;
        }
    }

    private static void ReleaseStagingSlot(StagingSlot slot)
    {
        slot.Source?.Dispose();
        if (slot.Fence is IDisposable disposableFence)
            disposableFence.Dispose();
        slot.Source = null;
        slot.Fence = null;
        slot.State = StagingSlotState.Free;
        slot.Plan = null;
    }

    private static uint NextStagingGeneration(uint generation)
    {
        unchecked
        {
            ++generation;
        }
        return generation == 0u ? 1u : generation;
    }
}
