namespace XREngine.Rendering;

/// <summary>
/// Completion-aware, allocation-free warmed upload arena shared by desktop and eye
/// render-pipeline consumers. It owns logical frame-slot storage and copy plans, while
/// OpenGL and Vulkan translate storage generations into their native persistent mappings.
/// </summary>
public sealed class AdvancedFrameSlotUploadArena : IDisposable
{
    private readonly AdvancedFrameSlotUploadArenaOptions _options;
    private readonly AdvancedFrameUploadOverflowGeneration[] _overflowGenerations;
    private readonly AdvancedFrameUploadRetiredGenerationEntry[] _retiredGenerations;
    private readonly ulong[] _slotSubmittedCompletionValues;
    private AdvancedFrameUploadStorageGeneration _activeGeneration;
    private AdvancedFrameUploadCapacityProfile _pendingCapacity;
    private AdvancedFrameUploadCapacityProfile _frameRequiredCapacity;
    private AdvancedFrameSlotPair _frameSlots;
    private ulong _nextStorageGeneration = 1UL;
    private ulong _lastCompletedValue;
    private ulong _frameOrdinal;
    private ulong _bytesWritten;
    private ulong _overflowBytes;
    private ulong _capacityGrowthBytes;
    private int _overflowAllocationCount;
    private int _overflowExhaustionCount;
    private int _capacityGrowthCount;
    private int _retiredGenerationCount;
    private int _growthDeferralCount;
    private int _slotReuseDeferralCount;
    private int _dirtyRangeCount;
    private bool _frameOpen;
    private bool _disposed;

    public AdvancedFrameSlotUploadArena(
        AdvancedFrameSlotUploadArenaOptions options)
    {
        ValidateOptions(options);
        _options = options;
        _activeGeneration = CreateStorageGeneration(options.InitialCapacity);
        _pendingCapacity = options.InitialCapacity;
        _slotSubmittedCompletionValues = new ulong[options.SlotCount];
        _retiredGenerations = new AdvancedFrameUploadRetiredGenerationEntry[
            options.RetiredGenerationCapacity];
        _overflowGenerations = new AdvancedFrameUploadOverflowGeneration[
            options.OverflowGenerationCount];
        for (int i = 0; i < _overflowGenerations.Length; i++)
        {
            _overflowGenerations[i] = new AdvancedFrameUploadOverflowGeneration(
                CreateStorageGeneration(options.OverflowCapacity));
        }
    }

    public AdvancedFrameSlotUploadArenaOptions Options => _options;
    public AdvancedFrameUploadCapacityProfile CurrentCapacity =>
        _activeGeneration.Capacity;
    public ulong CurrentStorageGeneration => _activeGeneration.Generation;
    public bool IsFrameOpen => _frameOpen;
    public uint CurrentSlot => _frameSlots.Current;
    public uint PreviousSlot => _frameSlots.Previous;
    public int MaxCopyRangeCount =>
        AdvancedFrameUploadCapacityProfile.StreamCount *
        _options.MaxDirtyRangesPerStream *
        (1 + _options.OverflowGenerationCount);
    public int PendingOverflowGenerationCount =>
        CountOverflowGenerations(EAdvancedFrameUploadOverflowState.PendingRetirement);
    public int AvailableOverflowGenerationCount =>
        CountOverflowGenerations(EAdvancedFrameUploadOverflowState.Idle);
    public int RetiredMainGenerationCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _retiredGenerations.Length; i++)
                if (_retiredGenerations[i].IsOccupied)
                    count++;
            return count;
        }
    }

    /// <summary>
    /// Opens an explicit frame boundary. Completion is polled by the caller; this method
    /// never waits for a slot or for the whole device.
    /// </summary>
    public bool TryBeginFrame(
        ulong frameOrdinal,
        ulong completedValue)
    {
        ThrowIfDisposed();
        if (_frameOpen)
            throw new InvalidOperationException("The advanced upload frame is already open.");
        if (completedValue < _lastCompletedValue)
            throw new ArgumentOutOfRangeException(
                nameof(completedValue),
                "Completed upload values must be monotonic.");

        _lastCompletedValue = completedValue;
        ResetFrameTelemetry(frameOrdinal);
        DrainCompletedGenerations(completedValue);
        ApplyPendingGrowthAtFrameBoundary(completedValue);

        AdvancedFrameSlotPair slots = AdvancedFrameSlotContract.Resolve(
            frameOrdinal,
            _options.SlotCount);
        _frameSlots = slots;
        ulong lastSubmission = _slotSubmittedCompletionValues[slots.Current];
        if (!AdvancedFrameSlotContract.CanReuse(lastSubmission, completedValue))
        {
            _slotReuseDeferralCount++;
            return false;
        }

        _frameOrdinal = frameOrdinal;
        _frameRequiredCapacity = default;
        _activeGeneration.BeginFrame(slots.Current);
        _frameOpen = true;
        return true;
    }

    public bool TryAllocate(
        EAdvancedFrameUploadStream stream,
        uint byteCount,
        out AdvancedFrameUploadAllocation allocation)
        => TryAllocate(
            stream,
            byteCount,
            _options.DefaultAlignmentBytes,
            out allocation);

    public bool TryAllocate(
        EAdvancedFrameUploadStream stream,
        uint byteCount,
        uint alignmentBytes,
        out AdvancedFrameUploadAllocation allocation)
    {
        ThrowIfDisposed();
        ThrowIfFrameClosed();
        ValidateStream(stream);
        allocation = default;

        uint alignment = Math.Max(1u, alignmentBytes);
        RecordFrameRequiredCapacity(stream, byteCount, alignment);
        if (_activeGeneration.TryAllocate(
                stream,
                byteCount,
                alignment,
                out Memory<byte> primaryMemory,
                out uint primaryOffset))
        {
            allocation = CreateAllocation(
                primaryMemory,
                primaryOffset,
                byteCount,
                stream,
                _activeGeneration.Generation,
                isOverflow: false);
            RecordSuccessfulAllocation(byteCount, isOverflow: false);
            return true;
        }

        for (int i = 0; i < _overflowGenerations.Length; i++)
        {
            AdvancedFrameUploadOverflowGeneration overflow =
                _overflowGenerations[i];
            if (overflow.State != EAdvancedFrameUploadOverflowState.Active ||
                overflow.ActiveFrameOrdinal != _frameOrdinal)
            {
                continue;
            }

            if (TryAllocateOverflow(
                    overflow,
                    stream,
                    byteCount,
                    alignment,
                    out allocation))
            {
                return true;
            }
        }

        for (int i = 0; i < _overflowGenerations.Length; i++)
        {
            AdvancedFrameUploadOverflowGeneration overflow =
                _overflowGenerations[i];
            if (!overflow.TryActivate(_frameOrdinal, _frameSlots.Current))
                continue;

            if (TryAllocateOverflow(
                    overflow,
                    stream,
                    byteCount,
                    alignment,
                    out allocation))
            {
                return true;
            }

            overflow.ReleaseEmpty();
        }

        _overflowExhaustionCount++;
        return false;
    }

    public int GetCurrentCopyRangeCount()
    {
        ThrowIfDisposed();
        ThrowIfFrameClosed();
        return ComputeCurrentCopyRangeCount();
    }

    /// <summary>
    /// Builds one bounded backend-neutral copy plan without allocating. The caller can
    /// record all returned ranges into one transfer/copy submission.
    /// </summary>
    public bool TryBuildCurrentCopyPlan(
        Span<AdvancedUploadCopyRange> destination,
        out int rangeCount)
    {
        ThrowIfDisposed();
        ThrowIfFrameClosed();

        int required = ComputeCurrentCopyRangeCount();
        if (destination.Length < required)
        {
            rangeCount = required;
            return false;
        }

        int writeIndex = _activeGeneration.CopyDirtyRangesTo(
            destination,
            destinationOffset: 0,
            _frameSlots.Current,
            isOverflow: false);
        for (int i = 0; i < _overflowGenerations.Length; i++)
        {
            AdvancedFrameUploadOverflowGeneration overflow =
                _overflowGenerations[i];
            if (overflow.State != EAdvancedFrameUploadOverflowState.Active ||
                overflow.ActiveFrameOrdinal != _frameOrdinal)
            {
                continue;
            }

            writeIndex += overflow.Storage.CopyDirtyRangesTo(
                destination,
                writeIndex,
                _frameSlots.Current,
                isOverflow: true);
        }

        rangeCount = writeIndex;
        _dirtyRangeCount = writeIndex;
        return true;
    }

    /// <summary>
    /// Seals the current frame and associates every used storage generation with the
    /// caller-provided fence or timeline value.
    /// </summary>
    public void EndFrame(ulong submissionCompletionValue)
    {
        ThrowIfDisposed();
        ThrowIfFrameClosed();

        _dirtyRangeCount = ComputeCurrentCopyRangeCount();
        _activeGeneration.EndFrame();
        _slotSubmittedCompletionValues[_frameSlots.Current] =
            submissionCompletionValue;
        for (int i = 0; i < _overflowGenerations.Length; i++)
        {
            AdvancedFrameUploadOverflowGeneration overflow =
                _overflowGenerations[i];
            if (overflow.State == EAdvancedFrameUploadOverflowState.Active &&
                overflow.ActiveFrameOrdinal == _frameOrdinal)
            {
                overflow.Complete(submissionCompletionValue);
                if (submissionCompletionValue == 0UL)
                    _retiredGenerationCount++;
            }
        }

        ScheduleCapacityFromHighWater();
        _frameOpen = false;
    }

    public AdvancedFrameUploadTelemetrySnapshot GetTelemetrySnapshot()
    {
        ThrowIfDisposed();
        return new AdvancedFrameUploadTelemetrySnapshot(
            _frameOrdinal,
            _frameSlots.Current,
            _bytesWritten,
            _dirtyRangeCount,
            _activeGeneration.Capacity.TotalBytesPerSlot,
            _activeGeneration.MappedByteCapacity,
            _capacityGrowthCount,
            _capacityGrowthBytes,
            _overflowAllocationCount,
            _overflowBytes,
            _overflowExhaustionCount,
            _retiredGenerationCount,
            _growthDeferralCount,
            _slotReuseDeferralCount,
            PendingOverflowGenerationCount,
            RetiredMainGenerationCount);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _activeGeneration.Dispose();
        for (int i = 0; i < _overflowGenerations.Length; i++)
            _overflowGenerations[i].Dispose();
        for (int i = 0; i < _retiredGenerations.Length; i++)
        {
            _retiredGenerations[i].Storage?.Dispose();
            _retiredGenerations[i].Clear();
        }
    }

    private AdvancedFrameUploadStorageGeneration CreateStorageGeneration(
        in AdvancedFrameUploadCapacityProfile capacity)
        => new(
            _nextStorageGeneration++,
            _options.SlotCount,
            capacity,
            _options.DefaultAlignmentBytes,
            _options.MaxDirtyRangesPerStream);

    private AdvancedFrameUploadAllocation CreateAllocation(
        Memory<byte> memory,
        uint byteOffset,
        uint byteCount,
        EAdvancedFrameUploadStream stream,
        ulong storageGeneration,
        bool isOverflow)
        => new(
            memory,
            stream,
            storageGeneration,
            _frameSlots.Current,
            byteOffset,
            byteCount,
            isOverflow);

    private bool TryAllocateOverflow(
        AdvancedFrameUploadOverflowGeneration overflow,
        EAdvancedFrameUploadStream stream,
        uint byteCount,
        uint alignment,
        out AdvancedFrameUploadAllocation allocation)
    {
        if (!overflow.Storage.TryAllocate(
                stream,
                byteCount,
                alignment,
                out Memory<byte> memory,
                out uint byteOffset))
        {
            allocation = default;
            return false;
        }

        allocation = CreateAllocation(
            memory,
            byteOffset,
            byteCount,
            stream,
            overflow.Storage.Generation,
            isOverflow: true);
        RecordSuccessfulAllocation(byteCount, isOverflow: true);
        return true;
    }

    private void RecordSuccessfulAllocation(
        uint byteCount,
        bool isOverflow)
    {
        _bytesWritten += byteCount;
        if (isOverflow)
        {
            _overflowAllocationCount++;
            _overflowBytes += byteCount;
        }

        _dirtyRangeCount = ComputeCurrentCopyRangeCount();
    }

    private void RecordFrameRequiredCapacity(
        EAdvancedFrameUploadStream stream,
        uint byteCount,
        uint alignment)
    {
        uint used = _frameRequiredCapacity.Get(stream);
        uint aligned = AlignUp(used, alignment);
        uint required = checked(aligned + byteCount);
        _frameRequiredCapacity = _frameRequiredCapacity.With(stream, required);
    }

    private void ScheduleCapacityFromHighWater()
    {
        AdvancedFrameUploadCapacityProfile target = _activeGeneration.Capacity;
        for (int i = 0; i < AdvancedFrameUploadCapacityProfile.StreamCount; i++)
        {
            EAdvancedFrameUploadStream stream = (EAdvancedFrameUploadStream)i;
            uint required = _frameRequiredCapacity.Get(stream);
            uint current = target.Get(stream);
            if (required > current)
                target = target.With(stream, NextPowerOfTwo(required));
        }

        _pendingCapacity = AdvancedFrameUploadCapacityProfile.Max(
            _pendingCapacity,
            target);
    }

    private void ApplyPendingGrowthAtFrameBoundary(ulong completedValue)
    {
        if (!_pendingCapacity.AnyGreaterThan(_activeGeneration.Capacity))
            return;

        ulong retireAfter = 0UL;
        for (int i = 0; i < _slotSubmittedCompletionValues.Length; i++)
            retireAfter = Math.Max(retireAfter, _slotSubmittedCompletionValues[i]);

        int retirementIndex = -1;
        if (!AdvancedFrameSlotContract.CanReuse(retireAfter, completedValue))
        {
            retirementIndex = FindFreeRetiredGenerationIndex();
            if (retirementIndex < 0)
            {
                _growthDeferralCount++;
                return;
            }
        }

        AdvancedFrameUploadStorageGeneration replacement =
            CreateStorageGeneration(_pendingCapacity);
        AdvancedFrameUploadStorageGeneration previous = _activeGeneration;
        _activeGeneration = replacement;
        Array.Clear(_slotSubmittedCompletionValues);

        if (retirementIndex >= 0)
        {
            _retiredGenerations[retirementIndex].Storage = previous;
            _retiredGenerations[retirementIndex].RetireAfterCompletionValue =
                retireAfter;
        }
        else
        {
            previous.Dispose();
            _retiredGenerationCount++;
        }

        _capacityGrowthCount++;
        _capacityGrowthBytes +=
            replacement.MappedByteCapacity - previous.MappedByteCapacity;
        _pendingCapacity = replacement.Capacity;
    }

    private void DrainCompletedGenerations(ulong completedValue)
    {
        for (int i = 0; i < _overflowGenerations.Length; i++)
            if (_overflowGenerations[i].TryRetire(completedValue))
                _retiredGenerationCount++;

        for (int i = 0; i < _retiredGenerations.Length; i++)
        {
            ref AdvancedFrameUploadRetiredGenerationEntry retired =
                ref _retiredGenerations[i];
            if (!retired.IsOccupied ||
                !AdvancedFrameSlotContract.CanReuse(
                    retired.RetireAfterCompletionValue,
                    completedValue))
            {
                continue;
            }

            retired.Storage!.Dispose();
            retired.Clear();
            _retiredGenerationCount++;
        }
    }

    private int ComputeCurrentCopyRangeCount()
    {
        int count = _activeGeneration.DirtyRangeCount;
        for (int i = 0; i < _overflowGenerations.Length; i++)
        {
            AdvancedFrameUploadOverflowGeneration overflow =
                _overflowGenerations[i];
            if (overflow.State == EAdvancedFrameUploadOverflowState.Active &&
                overflow.ActiveFrameOrdinal == _frameOrdinal)
            {
                count += overflow.Storage.DirtyRangeCount;
            }
        }

        return count;
    }

    private int CountOverflowGenerations(
        EAdvancedFrameUploadOverflowState state)
    {
        int count = 0;
        for (int i = 0; i < _overflowGenerations.Length; i++)
            if (_overflowGenerations[i].State == state)
                count++;
        return count;
    }

    private int FindFreeRetiredGenerationIndex()
    {
        for (int i = 0; i < _retiredGenerations.Length; i++)
            if (!_retiredGenerations[i].IsOccupied)
                return i;
        return -1;
    }

    private void ResetFrameTelemetry(ulong frameOrdinal)
    {
        _frameOrdinal = frameOrdinal;
        _bytesWritten = 0UL;
        _overflowBytes = 0UL;
        _capacityGrowthBytes = 0UL;
        _overflowAllocationCount = 0;
        _overflowExhaustionCount = 0;
        _capacityGrowthCount = 0;
        _retiredGenerationCount = 0;
        _growthDeferralCount = 0;
        _slotReuseDeferralCount = 0;
        _dirtyRangeCount = 0;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AdvancedFrameSlotUploadArena));
    }

    private void ThrowIfFrameClosed()
    {
        if (!_frameOpen)
            throw new InvalidOperationException("The advanced upload frame is not open.");
    }

    private static void ValidateOptions(
        in AdvancedFrameSlotUploadArenaOptions options)
    {
        AdvancedFrameSlotContract.ValidateSlotCount(options.SlotCount);
        if (options.DefaultAlignmentBytes == 0u)
            throw new ArgumentOutOfRangeException(nameof(options.DefaultAlignmentBytes));
        if (options.MaxDirtyRangesPerStream < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MaxDirtyRangesPerStream));
        if (options.OverflowGenerationCount < 1)
            throw new ArgumentOutOfRangeException(nameof(options.OverflowGenerationCount));
        if (options.RetiredGenerationCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(options.RetiredGenerationCapacity));

        for (int i = 0; i < AdvancedFrameUploadCapacityProfile.StreamCount; i++)
        {
            EAdvancedFrameUploadStream stream = (EAdvancedFrameUploadStream)i;
            if (options.InitialCapacity.Get(stream) == 0u)
                throw new ArgumentOutOfRangeException(
                    nameof(options.InitialCapacity),
                    $"Initial {stream} capacity must be non-zero.");
            if (options.OverflowCapacity.Get(stream) == 0u)
                throw new ArgumentOutOfRangeException(
                    nameof(options.OverflowCapacity),
                    $"Overflow {stream} capacity must be non-zero.");
        }
    }

    private static void ValidateStream(EAdvancedFrameUploadStream stream)
    {
        int index = (int)stream;
        if ((uint)index >= AdvancedFrameUploadCapacityProfile.StreamCount)
            throw new ArgumentOutOfRangeException(nameof(stream), stream, null);
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        if (alignment <= 1u)
            return value;

        ulong aligned = ((ulong)value + alignment - 1u) / alignment * alignment;
        if (aligned > uint.MaxValue)
            throw new InvalidOperationException("Advanced upload alignment exceeds the supported 32-bit buffer range.");
        return (uint)aligned;
    }

    private static uint NextPowerOfTwo(uint value)
    {
        if (value <= 1u)
            return 1u;
        if (value > 0x80000000u)
            return uint.MaxValue;

        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        return value + 1u;
    }
}
