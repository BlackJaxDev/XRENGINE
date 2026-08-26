namespace XREngine.Components;

/// <summary>
/// Bounded per-world request registry. GPU gather and staging passes consume
/// pending entries later; request submission never waits for the renderer.
/// </summary>
internal sealed partial class PhysicsChainReadbackService
{
    private readonly PhysicsChainSlotArena<PhysicsChainReadbackRequestInfo> _requests = new();
    private readonly Dictionary<RequestKey, PhysicsChainReadbackHandle> _coalescedRequests = [];
    private readonly List<PhysicsChainReadbackHandle> _liveHandles = [];
    private readonly PhysicsChainReadbackLimits _limits;
    private long _budgetFrame = -1L;
    private long _currentFrame = -1L;
    private int _requestedElementsThisFrame;
    private int _requestedBytesThisFrame;
    private long _requestedCount;
    private long _coalescedCount;
    private long _rejectedCount;
    private long _cancelledCount;
    private long _expiredCount;
    private long _releasedCount;

    public PhysicsChainReadbackService(PhysicsChainReadbackLimits limits)
    {
        limits.Validate();
        _limits = limits;
    }

    public bool TryRequest(
        PhysicsChainRuntimeHandle instanceHandle,
        PhysicsChainReadbackFields fields,
        ReadOnlySpan<int> selectedElementIndices,
        int expectedByteCount,
        long submissionFrame,
        out PhysicsChainReadbackHandle handle,
        out PhysicsChainReadbackRejection rejection)
    {
        ++_requestedCount;
        handle = PhysicsChainReadbackHandle.Invalid;
        rejection = ValidateRequest(instanceHandle, fields, selectedElementIndices, expectedByteCount, submissionFrame);
        if (rejection != PhysicsChainReadbackRejection.None)
        {
            ++_rejectedCount;
            return false;
        }

        _currentFrame = Math.Max(_currentFrame, submissionFrame);

        ResetBudgetIfNeeded(submissionFrame);
        var key = new RequestKey(instanceHandle, fields, selectedElementIndices);
        if (_coalescedRequests.TryGetValue(key, out PhysicsChainReadbackHandle existing)
            && TryGet(existing, out PhysicsChainReadbackRequestInfo? existingInfo)
            && existingInfo is not null
            && existingInfo.SubmissionFrame == submissionFrame
            && existingInfo.Status == PhysicsChainReadbackStatus.Pending)
        {
            handle = existing;
            ++_coalescedCount;
            return true;
        }

        PhysicsChainReadbackLayout.TryCalculate(
            fields,
            selectedElementIndices.Length,
            out int expectedElementCount,
            out _);
        if (expectedElementCount > _limits.MaximumElementsPerFrame - _requestedElementsThisFrame)
        {
            rejection = PhysicsChainReadbackRejection.ElementBudgetExceeded;
            ++_rejectedCount;
            return false;
        }
        if (expectedByteCount > _limits.MaximumBytesPerFrame - _requestedBytesThisFrame)
        {
            rejection = PhysicsChainReadbackRejection.ByteBudgetExceeded;
            ++_rejectedCount;
            return false;
        }

        var info = new PhysicsChainReadbackRequestInfo
        {
            InstanceHandle = instanceHandle,
            Fields = fields,
            SelectedElementIndices = key.CopySelection(),
            SubmissionFrame = submissionFrame,
            EarliestCompletionFrame = submissionFrame + 1L,
            ExpiryFrame = checked(submissionFrame + _limits.MaximumLifetimeFrames),
            ExpectedByteCount = expectedByteCount,
            ExpectedElementCount = expectedElementCount,
            Status = PhysicsChainReadbackStatus.Pending,
        };
        PhysicsChainArenaHandle arenaHandle = _requests.Allocate(info);
        handle = new PhysicsChainReadbackHandle(arenaHandle.Slot, arenaHandle.Generation);
        _liveHandles.Add(handle);
        _coalescedRequests[key] = handle;
        _requestedElementsThisFrame += expectedElementCount;
        _requestedBytesThisFrame += expectedByteCount;
        RecordAcceptedTransferRequest(expectedElementCount, expectedByteCount);
        return true;
    }

    public bool Cancel(PhysicsChainReadbackHandle handle)
    {
        if (!TryGet(handle, out PhysicsChainReadbackRequestInfo? info)
            || info is null
            || info.Status is PhysicsChainReadbackStatus.Available
                or PhysicsChainReadbackStatus.Cancelled
                or PhysicsChainReadbackStatus.Expired
                or PhysicsChainReadbackStatus.DiscardedStale
                or PhysicsChainReadbackStatus.Failed)
            return false;

        if (info.Status == PhysicsChainReadbackStatus.Pending)
            info.ActiveGatherPlan = null;
        else if (info.Status == PhysicsChainReadbackStatus.InFlight && TryCancelUncommittedTransfer(handle))
            info.ActiveGatherPlan = null;
        info.Status = PhysicsChainReadbackStatus.Cancelled;
        info.CompletionFrame = Math.Max(_currentFrame, info.SubmissionFrame);
        ++_cancelledCount;
        return true;
    }

    public bool Release(PhysicsChainReadbackHandle handle)
    {
        if (!TryGet(handle, out PhysicsChainReadbackRequestInfo? info)
            || info is null
            || !IsTerminal(info.Status))
            return false;

        if (!_requests.Free(new PhysicsChainArenaHandle(handle.Slot, handle.Generation)))
            return false;

        RemoveLiveHandle(handle);
        ++_releasedCount;
        return true;
    }

    public PhysicsChainReadbackCounters GetCounters()
        => new(
            _requestedCount,
            _coalescedCount,
            _rejectedCount,
            _cancelledCount,
            _expiredCount,
            _releasedCount,
            _requests.LiveCount);

    public void RecordRejectedRequest()
    {
        ++_requestedCount;
        ++_rejectedCount;
    }

    public bool TryGet(PhysicsChainReadbackHandle handle, out PhysicsChainReadbackRequestInfo? info)
    {
        bool found = _requests.TryGet(new PhysicsChainArenaHandle(handle.Slot, handle.Generation), out PhysicsChainReadbackRequestInfo? value);
        info = found ? value : null;
        return found;
    }

    public void AdvanceFrame(long currentFrame)
    {
        if (currentFrame < 0L)
            throw new ArgumentOutOfRangeException(nameof(currentFrame));

        if (currentFrame < _currentFrame)
            throw new ArgumentOutOfRangeException(nameof(currentFrame), "Readback frames must advance monotonically.");

        _currentFrame = currentFrame;
        ResetBudgetIfNeeded(currentFrame);
        for (int i = _liveHandles.Count - 1; i >= 0; --i)
        {
            PhysicsChainReadbackHandle handle = _liveHandles[i];
            if (!TryGet(handle, out PhysicsChainReadbackRequestInfo? info) || info is null)
            {
                _liveHandles.RemoveAt(i);
                continue;
            }

            if (info.Status is not (PhysicsChainReadbackStatus.Pending or PhysicsChainReadbackStatus.InFlight)
                || currentFrame < info.ExpiryFrame)
                continue;

            if (info.Status == PhysicsChainReadbackStatus.Pending)
                info.ActiveGatherPlan = null;
            info.Status = PhysicsChainReadbackStatus.Expired;
            info.CompletionFrame = currentFrame;
            ++_expiredCount;
        }
    }

    private PhysicsChainReadbackRejection ValidateRequest(
        PhysicsChainRuntimeHandle instanceHandle,
        PhysicsChainReadbackFields fields,
        ReadOnlySpan<int> selectedElementIndices,
        int expectedByteCount,
        long submissionFrame)
    {
        const PhysicsChainReadbackFields allFields =
            PhysicsChainReadbackFields.Particles
            | PhysicsChainReadbackFields.Bones
            | PhysicsChainReadbackFields.Sockets
            | PhysicsChainReadbackFields.Bounds
            | PhysicsChainReadbackFields.CollisionEvents
            | PhysicsChainReadbackFields.FullTransformMirror;

        if (!instanceHandle.IsValid)
            return PhysicsChainReadbackRejection.InvalidInstance;
        if (fields == PhysicsChainReadbackFields.None)
            return PhysicsChainReadbackRejection.NoFields;
        if ((fields & ~allFields) != 0)
            return PhysicsChainReadbackRejection.InvalidFields;
        if (submissionFrame < 0L
            || (_currentFrame >= 0L && submissionFrame < _currentFrame)
            || submissionFrame > long.MaxValue - _limits.MaximumLifetimeFrames)
            return PhysicsChainReadbackRejection.InvalidFrame;
        if (expectedByteCount <= 0)
            return PhysicsChainReadbackRejection.ByteBudgetExceeded;
        if (selectedElementIndices.IsEmpty)
            return PhysicsChainReadbackRejection.InvalidSelection;
        if (!PhysicsChainReadbackLayout.TryCalculate(fields, selectedElementIndices.Length, out _, out int packedByteCount))
            return PhysicsChainReadbackRejection.LayoutOverflow;
        if (expectedByteCount != packedByteCount)
            return PhysicsChainReadbackRejection.ByteCountMismatch;
        for (int i = 0; i < selectedElementIndices.Length; ++i)
            if (selectedElementIndices[i] < 0)
                return PhysicsChainReadbackRejection.InvalidSelection;
        return PhysicsChainReadbackRejection.None;
    }

    private void RemoveLiveHandle(PhysicsChainReadbackHandle handle)
    {
        for (int i = _liveHandles.Count - 1; i >= 0; --i)
        {
            if (_liveHandles[i] != handle)
                continue;

            _liveHandles.RemoveAt(i);
            return;
        }
    }

    private static bool IsTerminal(PhysicsChainReadbackStatus status)
        => status is PhysicsChainReadbackStatus.Available
            or PhysicsChainReadbackStatus.Cancelled
            or PhysicsChainReadbackStatus.Expired
            or PhysicsChainReadbackStatus.DiscardedStale
            or PhysicsChainReadbackStatus.Failed;

    private void ResetBudgetIfNeeded(long submissionFrame)
    {
        if (_budgetFrame == submissionFrame)
            return;

        _budgetFrame = submissionFrame;
        _requestedElementsThisFrame = 0;
        _requestedBytesThisFrame = 0;
        _coalescedRequests.Clear();
    }

    private sealed class RequestKey : IEquatable<RequestKey>
    {
        private readonly PhysicsChainRuntimeHandle _instanceHandle;
        private readonly PhysicsChainReadbackFields _fields;
        private readonly int[] _selection;

        public RequestKey(
            PhysicsChainRuntimeHandle instanceHandle,
            PhysicsChainReadbackFields fields,
            ReadOnlySpan<int> selectedElementIndices)
        {
            _instanceHandle = instanceHandle;
            _fields = fields;
            _selection = selectedElementIndices.ToArray();
        }

        public int[] CopySelection()
            => [.. _selection];

        public bool Equals(RequestKey? other)
            => other is not null
                && _instanceHandle == other._instanceHandle
                && _fields == other._fields
                && _selection.AsSpan().SequenceEqual(other._selection);

        public override bool Equals(object? obj)
            => obj is RequestKey other && Equals(other);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(_instanceHandle);
            hash.Add(_fields);
            for (int i = 0; i < _selection.Length; ++i)
                hash.Add(_selection[i]);
            return hash.ToHashCode();
        }
    }
}
