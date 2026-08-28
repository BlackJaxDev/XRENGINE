namespace XREngine.Rendering.Commands;

/// <summary>
/// Fixed-capacity generational table with stable logical handles and an optionally
/// compacted physical record array. Growth is only available through the explicitly
/// named structural-boundary method.
/// </summary>
public sealed class AdvancedGpuRecordTable<T> where T : unmanaged
{
    private T[] _records;
    private AdvancedGpuHandle[] _physicalHandles;
    private byte[] _physicalOccupancy;
    private uint[] _slotGenerations;
    private uint[] _slotToDense;
    private byte[] _slotTombstones;
    private uint[] _freeSlots;
    private AdvancedGpuHandleRemap[] _publishedRemaps;
    private AdvancedGpuRecordPublicationDelta[] _publicationDeltas;
    private uint[] _retiredSlots;
    private ulong[] _retiredSlotPublicationGenerations;
    private uint _freeSlotCount;
    private uint _retiredSlotCount;
    private uint _nextSlotIndex = 1u;
    private uint _count;
    private uint _physicalHighWater;
    private uint _dirtyMin = uint.MaxValue;
    private uint _dirtyMaxExclusive;
    private uint _lookupDirtyMin;
    private uint _lookupDirtyMaxExclusive = 1u;
    private int _publishedRemapCount;
    private int _publicationDeltaCount;
    private ulong _publishedRemapVersion;
    private ulong _activePublicationGeneration = 1u;
    private ulong _acknowledgedPublicationGeneration;
    private bool _isPacked = true;

    public AdvancedGpuRecordTable(uint capacity)
    {
        int arrayCapacity = ValidateArrayCapacity(capacity);
        _records = new T[arrayCapacity];
        _physicalHandles = new AdvancedGpuHandle[arrayCapacity];
        _physicalOccupancy = new byte[arrayCapacity];
        _slotGenerations = new uint[checked(arrayCapacity + 1)];
        _slotToDense = new uint[checked(arrayCapacity + 1)];
        _slotTombstones = new byte[checked(arrayCapacity + 1)];
        _freeSlots = new uint[arrayCapacity];
        _publishedRemaps = new AdvancedGpuHandleRemap[GetRemapCapacity(arrayCapacity)];
        _publicationDeltas = new AdvancedGpuRecordPublicationDelta[GetJournalCapacity(arrayCapacity)];
        _retiredSlots = new uint[arrayCapacity];
        _retiredSlotPublicationGenerations = new ulong[arrayCapacity];
        FillInvalidDenseIndices(_slotToDense, 0);
    }

    public uint Capacity => (uint)_records.Length;

    public uint Count => _count;

    public uint PhysicalHighWater => _physicalHighWater;

    public bool IsPacked => _isPacked;

    public ulong PublishedRemapVersion => _publishedRemapVersion;

    /// <summary>
    /// Generation stamped on mutations appended to <see cref="PublicationDeltas"/>.
    /// Change it only at a structural publication boundary.
    /// </summary>
    public ulong ActivePublicationGeneration => _activePublicationGeneration;

    public ulong AcknowledgedPublicationGeneration => _acknowledgedPublicationGeneration;

    /// <summary>
    /// Allocates a retained-publication snapshot at a setup or growth boundary.
    /// Sealing a publication into the returned object performs no allocation.
    /// </summary>
    public AdvancedGpuRecordTablePublicationSnapshot<T> CreatePublicationSnapshot(
        bool includeRecordImage = false)
        => new(
            _publicationDeltas.Length,
            _publishedRemaps.Length,
            includeRecordImage ? _records.Length : 0);

    internal bool CanSealPublication(
        AdvancedGpuRecordTablePublicationSnapshot<T> snapshot)
        => snapshot.DeltaCapacity >= _publicationDeltas.Length &&
           snapshot.RemapCapacity >= _publishedRemaps.Length &&
           (snapshot.RecordCapacity == 0 || snapshot.RecordCapacity >= _records.Length);

    public AdvancedGpuDirtyRange DirtyRange
        => _dirtyMin == uint.MaxValue
            ? AdvancedGpuDirtyRange.Empty
            : new AdvancedGpuDirtyRange(_dirtyMin, _dirtyMaxExclusive - _dirtyMin);

    /// <summary>
    /// Logical lookup rows changed since the last lookup-table publication.
    /// Slot zero starts dirty so a newly materialized GPU lookup table receives
    /// the explicit invalid sentinel rather than a zero dense index.
    /// </summary>
    public AdvancedGpuDirtyRange LogicalLookupDirtyRange
        => _lookupDirtyMin == uint.MaxValue
            ? AdvancedGpuDirtyRange.Empty
            : new AdvancedGpuDirtyRange(
                _lookupDirtyMin,
                _lookupDirtyMaxExclusive - _lookupDirtyMin);

    /// <summary>
    /// Number of logical lookup rows including reserved slot zero.
    /// </summary>
    public uint LogicalLookupCount => _nextSlotIndex;

    /// <summary>
    /// Physical upload image. Holes are zeroed and identified by
    /// <see cref="PhysicalOccupancy"/> until <see cref="Compact"/> is called.
    /// </summary>
    public ReadOnlySpan<T> PhysicalRecords
        => _records.AsSpan(0, checked((int)_physicalHighWater));

    public ReadOnlySpan<AdvancedGpuHandle> PhysicalHandles
        => _physicalHandles.AsSpan(0, checked((int)_physicalHighWater));

    public ReadOnlySpan<byte> PhysicalOccupancy
        => _physicalOccupancy.AsSpan(0, checked((int)_physicalHighWater));

    public ReadOnlySpan<AdvancedGpuHandleRemap> PublishedRemaps
        => _publishedRemaps.AsSpan(0, _publishedRemapCount);

    /// <summary>
    /// Exact mutations that have not yet been acknowledged by all consumers.
    /// The span is backed by fixed storage and must be consumed before the next
    /// table mutation or acknowledgement boundary.
    /// </summary>
    public ReadOnlySpan<AdvancedGpuRecordPublicationDelta> PublicationDeltas
        => _publicationDeltas.AsSpan(0, _publicationDeltaCount);

    /// <summary>
    /// Alias for the unacknowledged bounded publication journal.
    /// </summary>
    public ReadOnlySpan<AdvancedGpuRecordPublicationDelta> PublishedDeltas
        => PublicationDeltas;

    /// <summary>
    /// Copies this table's exact journal segment and remap batch for
    /// <paramref name="publicationSequence"/> into a publication-ring-owned
    /// snapshot. The snapshot remains stable after the live table mutates.
    /// </summary>
    public bool TrySealPublication(
        ulong publicationSequence,
        AdvancedGpuRecordTablePublicationSnapshot<T> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (publicationSequence == 0u || publicationSequence != _activePublicationGeneration)
            return false;

        int deltaStart = 0;
        while (deltaStart < _publicationDeltaCount &&
               _publicationDeltas[deltaStart].PublicationGeneration < publicationSequence)
        {
            ++deltaStart;
        }

        int deltaEnd = deltaStart;
        while (deltaEnd < _publicationDeltaCount &&
               _publicationDeltas[deltaEnd].PublicationGeneration == publicationSequence)
        {
            ++deltaEnd;
        }

        return snapshot.TryCapture(
            publicationSequence,
            _publicationDeltas.AsSpan(deltaStart, deltaEnd - deltaStart),
            PublishedRemaps,
            PhysicalRecords,
            PhysicalHandles,
            PhysicalOccupancy,
            this);
    }

    /// <summary>
    /// Selects the generation stamped on subsequent journal entries. Growth and
    /// reclamation remain explicit boundary operations; ordinary mutation never
    /// allocates or changes journal capacity.
    /// </summary>
    public void BeginPublicationGeneration(ulong publicationGeneration)
    {
        if (publicationGeneration == 0u)
            throw new ArgumentOutOfRangeException(nameof(publicationGeneration));
        if (publicationGeneration < _activePublicationGeneration)
            throw new ArgumentOutOfRangeException(nameof(publicationGeneration));

        _activePublicationGeneration = publicationGeneration;
    }

    public bool TryAdd(in T value, out AdvancedGpuHandle handle)
    {
        handle = AdvancedGpuHandle.Invalid;
        if (_count >= Capacity || !CanAppendPublicationDeltas(1))
            return false;

        uint denseIndex = FindFreeDenseIndex();
        if (denseIndex == AdvancedGpuHandleRemap.InvalidDenseIndex)
            return false;

        uint slotIndex;
        if (_freeSlotCount > 0u)
        {
            slotIndex = _freeSlots[--_freeSlotCount];
        }
        else
        {
            if (_nextSlotIndex > Capacity)
                return false;

            slotIndex = _nextSlotIndex++;
        }

        uint generation = _slotGenerations[slotIndex];
        if (generation == 0u)
            generation = 1u;

        handle = new AdvancedGpuHandle(slotIndex, generation);
        _slotGenerations[slotIndex] = generation;
        _slotToDense[slotIndex] = denseIndex;
        _slotTombstones[slotIndex] = 0;
        _records[denseIndex] = value;
        _physicalHandles[denseIndex] = handle;
        _physicalOccupancy[denseIndex] = 1;
        ++_count;
        if (denseIndex == _physicalHighWater)
            ++_physicalHighWater;

        MarkDirty(denseIndex);
        MarkLogicalLookupDirty(slotIndex);
        AppendPublicationDelta(new AdvancedGpuRecordPublicationDelta(
            handle,
            EAdvancedGpuRecordPublicationChange.Added,
            EAdvancedGpuMutationDomain.LayoutTopology,
            AdvancedGpuHandleRemap.InvalidDenseIndex,
            denseIndex,
            _activePublicationGeneration));
        RecalculatePackedState();
        return true;
    }

    /// <summary>
    /// Internal rollback escape hatch for records created before a publication is
    /// exposed. Production retirement must use <see cref="TryTombstone"/>.
    /// </summary>
    internal bool TryRemoveImmediatelyBeforePublication(AdvancedGpuHandle handle)
    {
        if (!TryGetDenseIndex(handle, out uint denseIndex) ||
            !CanPublishRemaps(1) ||
            !CanAppendPublicationDeltas(1))
            return false;

        _records[denseIndex] = default;
        _physicalHandles[denseIndex] = AdvancedGpuHandle.Invalid;
        _physicalOccupancy[denseIndex] = 0;
        _slotToDense[handle.Index] = AdvancedGpuHandleRemap.InvalidDenseIndex;
        _slotGenerations[handle.Index] = NextGeneration(handle.Generation);
        _freeSlots[_freeSlotCount++] = handle.Index;
        --_count;

        AppendRemap(new AdvancedGpuHandleRemap(
            handle,
            denseIndex,
            AdvancedGpuHandleRemap.InvalidDenseIndex));
        AppendPublicationDelta(new AdvancedGpuRecordPublicationDelta(
            handle,
            EAdvancedGpuRecordPublicationChange.Tombstoned,
            EAdvancedGpuMutationDomain.LayoutTopology,
            denseIndex,
            AdvancedGpuHandleRemap.InvalidDenseIndex,
            _activePublicationGeneration));
        MarkDirty(denseIndex);
        MarkLogicalLookupDirty(handle.Index);
        TrimPhysicalHighWater();
        RecalculatePackedState();
        return true;
    }

    public bool TryGet(AdvancedGpuHandle handle, out T value)
    {
        if (!TryGetDenseIndex(handle, out uint denseIndex))
        {
            value = default;
            return false;
        }

        value = _records[denseIndex];
        return true;
    }

    public bool TryReplace(
        AdvancedGpuHandle handle,
        in T value,
        EAdvancedGpuMutationDomain domain = EAdvancedGpuMutationDomain.Content)
    {
        if (!TryGetDenseIndex(handle, out uint denseIndex) ||
            !CanAppendPublicationDeltas(1))
            return false;

        _records[denseIndex] = value;
        MarkDirty(denseIndex);
        AppendPublicationDelta(new AdvancedGpuRecordPublicationDelta(
            handle,
            EAdvancedGpuRecordPublicationChange.Updated,
            domain,
            denseIndex,
            denseIndex,
            _activePublicationGeneration));
        return true;
    }

    /// <summary>
    /// Preflights one publisher transaction against fixed row, journal, remap,
    /// and tombstone capacities. With external mutation serialization, calls
    /// covered by a successful preflight cannot fail for capacity.
    /// </summary>
    internal bool CanApply(int addCount, int replaceCount, int tombstoneCount)
    {
        if (addCount < 0 || replaceCount < 0 || tombstoneCount < 0)
            return false;

        int occupiedCount = checked((int)(_count + _retiredSlotCount));
        int capacity = checked((int)Capacity);
        int mutationCount = checked(addCount + replaceCount + tombstoneCount);
        return addCount <= capacity - occupiedCount &&
            tombstoneCount <= capacity - checked((int)_retiredSlotCount) &&
            CanAppendPublicationDeltas(mutationCount) &&
            CanPublishRemaps(tombstoneCount);
    }

    public bool TrySet(AdvancedGpuHandle handle, in T value)
        => TryReplace(handle, value);

    public bool IsCurrent(AdvancedGpuHandle handle)
        => TryGetDenseIndex(handle, out _);

    /// <summary>
    /// Invalidates <paramref name="handle"/> for new logical lookups immediately,
    /// but pins its generation, dense row, physical handle, occupancy, and payload
    /// until every consumer has acknowledged the selected publication generation.
    /// This prevents ABA reuse and preserves the old physical row for in-flight
    /// frame packages.
    /// </summary>
    public bool TryTombstone(AdvancedGpuHandle handle)
    {
        if (!TryGetDenseIndex(handle, out uint denseIndex) ||
            !CanPublishRemaps(1) ||
            !CanAppendPublicationDeltas(1) ||
            _retiredSlotCount >= Capacity)
            return false;

        TombstoneResolvedHandle(handle, denseIndex);
        return true;
    }

    private void TombstoneResolvedHandle(AdvancedGpuHandle handle, uint denseIndex)
    {

        _slotTombstones[handle.Index] = 1;
        _retiredSlots[_retiredSlotCount] = handle.Index;
        _retiredSlotPublicationGenerations[_retiredSlotCount] = _activePublicationGeneration;
        ++_retiredSlotCount;
        --_count;

        AppendPublicationDelta(new AdvancedGpuRecordPublicationDelta(
            handle,
            EAdvancedGpuRecordPublicationChange.Tombstoned,
            EAdvancedGpuMutationDomain.LayoutTopology,
            denseIndex,
            AdvancedGpuHandleRemap.InvalidDenseIndex,
            _activePublicationGeneration));
        MarkLogicalLookupDirty(handle.Index);
        RecalculatePackedState();
    }

    /// <summary>
    /// Tombstones a record under an explicit publication generation. This is the
    /// integration-friendly form for owners that publish several tables as one
    /// coherent frame-boundary transaction.
    /// </summary>
    public bool TryTombstone(AdvancedGpuHandle handle, ulong publicationGeneration)
    {
        if (publicationGeneration == 0u ||
            publicationGeneration < _activePublicationGeneration ||
            !TryGetDenseIndex(handle, out uint denseIndex) ||
            !CanPublishRemaps(1) ||
            !CanAppendPublicationDeltas(1) ||
            _retiredSlotCount >= Capacity)
        {
            return false;
        }

        _activePublicationGeneration = publicationGeneration;
        TombstoneResolvedHandle(handle, denseIndex);
        return true;
    }

    /// <summary>
    /// Releases tombstoned logical slots and journal entries observed by all
    /// consumers through <paramref name="acknowledgedPublicationGeneration"/>.
    /// This is intentionally a publication-boundary operation.
    /// </summary>
    public int ReclaimAcknowledged(ulong acknowledgedPublicationGeneration)
    {
        if (acknowledgedPublicationGeneration <= _acknowledgedPublicationGeneration)
            return 0;

        int reclaimableCount = 0;
        for (uint retiredIndex = 0u; retiredIndex < _retiredSlotCount; ++retiredIndex)
        {
            if (_retiredSlotPublicationGenerations[retiredIndex] <= acknowledgedPublicationGeneration)
                ++reclaimableCount;
        }
        if (!CanPublishRemaps(reclaimableCount))
            return 0;

        // Validate all pinned rows before changing any state so a damaged table
        // fails atomically instead of reclaiming only a prefix of the batch.
        for (uint retiredIndex = 0u; retiredIndex < _retiredSlotCount; ++retiredIndex)
        {
            if (_retiredSlotPublicationGenerations[retiredIndex] > acknowledgedPublicationGeneration)
                continue;

            uint slotIndex = _retiredSlots[retiredIndex];
            uint denseIndex = _slotToDense[slotIndex];
            AdvancedGpuHandle retiredHandle = new(
                slotIndex,
                _slotGenerations[slotIndex]);
            if (_slotTombstones[slotIndex] == 0 ||
                denseIndex >= _physicalHighWater ||
                _physicalHandles[denseIndex] != retiredHandle)
            {
                throw new InvalidOperationException(
                    "A tombstoned GPU record lost its pinned physical row before acknowledgement.");
            }
        }

        _acknowledgedPublicationGeneration = acknowledgedPublicationGeneration;
        int reclaimedCount = 0;
        uint retainedCount = 0u;
        for (uint retiredIndex = 0u; retiredIndex < _retiredSlotCount; ++retiredIndex)
        {
            if (_retiredSlotPublicationGenerations[retiredIndex] <= acknowledgedPublicationGeneration)
            {
                uint slotIndex = _retiredSlots[retiredIndex];
                uint denseIndex = _slotToDense[slotIndex];
                AdvancedGpuHandle retiredHandle = new(
                    slotIndex,
                    _slotGenerations[slotIndex]);

                _records[denseIndex] = default;
                _physicalHandles[denseIndex] = AdvancedGpuHandle.Invalid;
                _physicalOccupancy[denseIndex] = 0;
                _slotToDense[slotIndex] = AdvancedGpuHandleRemap.InvalidDenseIndex;
                _slotGenerations[slotIndex] = NextGeneration(retiredHandle.Generation);
                _slotTombstones[slotIndex] = 0;
                _freeSlots[_freeSlotCount++] = slotIndex;
                AppendRemap(new AdvancedGpuHandleRemap(
                    retiredHandle,
                    denseIndex,
                    AdvancedGpuHandleRemap.InvalidDenseIndex));
                MarkDirty(denseIndex);
                MarkLogicalLookupDirty(slotIndex);
                ++reclaimedCount;
                continue;
            }

            _retiredSlots[retainedCount] = _retiredSlots[retiredIndex];
            _retiredSlotPublicationGenerations[retainedCount] =
                _retiredSlotPublicationGenerations[retiredIndex];
            ++retainedCount;
        }
        _retiredSlotCount = retainedCount;
        TrimPhysicalHighWater();
        RecalculatePackedState();

        int firstUnacknowledgedDelta = 0;
        while (firstUnacknowledgedDelta < _publicationDeltaCount &&
               _publicationDeltas[firstUnacknowledgedDelta].PublicationGeneration <=
               acknowledgedPublicationGeneration)
        {
            ++firstUnacknowledgedDelta;
        }

        if (firstUnacknowledgedDelta > 0)
        {
            int remainingDeltaCount = _publicationDeltaCount - firstUnacknowledgedDelta;
            if (remainingDeltaCount > 0)
            {
                Array.Copy(
                    _publicationDeltas,
                    firstUnacknowledgedDelta,
                    _publicationDeltas,
                    0,
                    remainingDeltaCount);
            }
            _publicationDeltaCount = remainingDeltaCount;
        }

        return reclaimedCount;
    }

    public bool TryGetDenseIndex(AdvancedGpuHandle handle, out uint denseIndex)
    {
        denseIndex = AdvancedGpuHandleRemap.InvalidDenseIndex;
        if (!handle.IsValid || handle.Index >= (uint)_slotToDense.Length)
            return false;
        if (_slotGenerations[handle.Index] != handle.Generation)
            return false;
        if (_slotTombstones[handle.Index] != 0)
            return false;

        uint candidate = _slotToDense[handle.Index];
        if (candidate >= _physicalHighWater || _physicalOccupancy[candidate] == 0)
            return false;
        if (_physicalHandles[candidate] != handle)
            return false;

        denseIndex = candidate;
        return true;
    }

    /// <summary>
    /// Packs live physical rows without allocating and appends every relocation to
    /// <see cref="PublishedRemaps"/>. Returns -1 if callers retained too many old
    /// remaps to publish the current batch.
    /// </summary>
    public int Compact()
    {
        // Tombstoned rows are pinned until acknowledgement. Compacting while one
        // is in flight could invalidate the dense index retained by an old frame.
        if (_retiredSlotCount != 0u)
            return -1;
        if (_isPacked)
            return 0;

        uint maximumMoves = _physicalHighWater - _count;
        if (!CanPublishRemaps(checked((int)maximumMoves)) ||
            !CanAppendPublicationDeltas(checked((int)maximumMoves)))
            return -1;

        int moveCount = 0;
        uint left = 0u;
        uint right = _physicalHighWater == 0u ? 0u : _physicalHighWater - 1u;
        while (left < _count)
        {
            if (_physicalOccupancy[left] != 0)
            {
                ++left;
                continue;
            }

            while (right > left && _physicalOccupancy[right] == 0)
                --right;
            if (right <= left)
                break;

            AdvancedGpuHandle movedHandle = _physicalHandles[right];
            _records[left] = _records[right];
            _physicalHandles[left] = movedHandle;
            _physicalOccupancy[left] = 1;
            _slotToDense[movedHandle.Index] = left;

            _records[right] = default;
            _physicalHandles[right] = AdvancedGpuHandle.Invalid;
            _physicalOccupancy[right] = 0;

            AppendRemap(new AdvancedGpuHandleRemap(movedHandle, right, left));
            AppendPublicationDelta(new AdvancedGpuRecordPublicationDelta(
                movedHandle,
                EAdvancedGpuRecordPublicationChange.DenseRemapped,
                EAdvancedGpuMutationDomain.ResourceBinding,
                right,
                left,
                _activePublicationGeneration));
            MarkDirty(left);
            MarkDirty(right);
            MarkLogicalLookupDirty(movedHandle.Index);
            ++moveCount;
            ++left;
            if (right > 0u)
                --right;
        }

        _physicalHighWater = _count;
        _isPacked = true;
        return moveCount;
    }

    /// <summary>
    /// Explicit structural-boundary growth. Ordinary mutation never grows storage.
    /// </summary>
    public void GrowAtBoundary(uint requiredCapacity)
    {
        if (requiredCapacity <= Capacity)
            return;

        int oldCapacity = _records.Length;
        int newCapacity = ValidateArrayCapacity(requiredCapacity);
        Array.Resize(ref _records, newCapacity);
        Array.Resize(ref _physicalHandles, newCapacity);
        Array.Resize(ref _physicalOccupancy, newCapacity);
        Array.Resize(ref _slotGenerations, checked(newCapacity + 1));
        Array.Resize(ref _slotToDense, checked(newCapacity + 1));
        Array.Resize(ref _slotTombstones, checked(newCapacity + 1));
        Array.Resize(ref _freeSlots, newCapacity);
        Array.Resize(ref _publishedRemaps, GetRemapCapacity(newCapacity));
        Array.Resize(ref _publicationDeltas, GetJournalCapacity(newCapacity));
        Array.Resize(ref _retiredSlots, newCapacity);
        Array.Resize(ref _retiredSlotPublicationGenerations, newCapacity);
        FillInvalidDenseIndices(_slotToDense, oldCapacity + 1);
        if (_physicalHighWater > 0u)
            MarkDirty(0u, _physicalHighWater);
    }

    public void ClearDirtyRange()
    {
        _dirtyMin = uint.MaxValue;
        _dirtyMaxExclusive = 0u;
    }

    public void ClearLogicalLookupDirtyRange()
    {
        _lookupDirtyMin = uint.MaxValue;
        _lookupDirtyMaxExclusive = 0u;
    }

    public void ClearPublishedRemaps()
        => _publishedRemapCount = 0;

    /// <summary>
    /// Discards only journal entries that have already been acknowledged. This
    /// provides an explicit reset for a fully acknowledged publication batch.
    /// </summary>
    public void ClearAcknowledgedPublicationDeltas()
        => ReclaimAcknowledged(_acknowledgedPublicationGeneration);

    /// <summary>
    /// Applies the currently published physical relocations to a dependent dense-index
    /// table without CPU readback or temporary allocations.
    /// </summary>
    public void ApplyPublishedRemaps(Span<uint> dependentDenseIndices)
    {
        ReadOnlySpan<AdvancedGpuHandleRemap> remaps = PublishedRemaps;
        for (int index = 0; index < dependentDenseIndices.Length; ++index)
        {
            uint denseIndex = dependentDenseIndices[index];
            for (int remapIndex = 0; remapIndex < remaps.Length; ++remapIndex)
            {
                AdvancedGpuHandleRemap remap = remaps[remapIndex];
                if (denseIndex == remap.PreviousDenseIndex)
                    denseIndex = remap.CurrentDenseIndex;
            }
            dependentDenseIndices[index] = denseIndex;
        }
    }

    public bool CopyDirtyRecords(Span<T> destination, out AdvancedGpuDirtyRange copiedRange)
    {
        copiedRange = DirtyRange;
        if (copiedRange.IsEmpty)
            return true;
        if (destination.Length < copiedRange.Count)
            return false;

        _records.AsSpan(
            checked((int)copiedRange.Start),
            checked((int)copiedRange.Count)).CopyTo(destination);
        return true;
    }

    /// <summary>
    /// Copies the complete upload-ready logical-to-physical lookup image.
    /// The destination contains slot zero followed by every slot handed out so far.
    /// </summary>
    public bool CopyLogicalLookups(
        Span<AdvancedGpuHandleLookup> destination,
        out int lookupCount)
    {
        lookupCount = checked((int)LogicalLookupCount);
        if (destination.Length < lookupCount)
            return false;

        destination[0] = AdvancedGpuHandleLookup.Invalid;
        for (uint slotIndex = 1u; slotIndex < LogicalLookupCount; ++slotIndex)
        {
            destination[checked((int)slotIndex)] = _slotTombstones[slotIndex] != 0
                ? AdvancedGpuHandleLookup.Invalid
                : new AdvancedGpuHandleLookup(
                    _slotGenerations[slotIndex],
                    _slotToDense[slotIndex]);
        }

        return true;
    }

    /// <summary>
    /// Copies only changed lookup rows into a destination span representing the
    /// dirty range. The returned range supplies the GPU destination row offset.
    /// </summary>
    public bool CopyDirtyLogicalLookups(
        Span<AdvancedGpuHandleLookup> destination,
        out AdvancedGpuDirtyRange copiedRange)
    {
        copiedRange = LogicalLookupDirtyRange;
        if (copiedRange.IsEmpty)
            return true;
        if (destination.Length < copiedRange.Count)
            return false;

        for (uint relativeIndex = 0u; relativeIndex < copiedRange.Count; ++relativeIndex)
        {
            uint slotIndex = copiedRange.Start + relativeIndex;
            destination[checked((int)relativeIndex)] = slotIndex == 0u
                ? AdvancedGpuHandleLookup.Invalid
                : _slotTombstones[slotIndex] != 0
                    ? AdvancedGpuHandleLookup.Invalid
                : new AdvancedGpuHandleLookup(
                    _slotGenerations[slotIndex],
                    _slotToDense[slotIndex]);
        }

        return true;
    }

    private uint FindFreeDenseIndex()
    {
        for (uint index = 0u; index < _physicalHighWater; ++index)
            if (_physicalOccupancy[index] == 0)
                return index;

        return _physicalHighWater < Capacity
            ? _physicalHighWater
            : AdvancedGpuHandleRemap.InvalidDenseIndex;
    }

    private void TrimPhysicalHighWater()
    {
        while (_physicalHighWater > 0u &&
               _physicalOccupancy[_physicalHighWater - 1u] == 0)
        {
            --_physicalHighWater;
        }
    }

    private void RecalculatePackedState()
    {
        if (_physicalHighWater != _count)
        {
            _isPacked = false;
            return;
        }

        for (uint index = 0u; index < _physicalHighWater; ++index)
        {
            if (_physicalOccupancy[index] != 0)
                continue;

            _isPacked = false;
            return;
        }

        _isPacked = true;
    }

    private void AppendRemap(in AdvancedGpuHandleRemap remap)
    {
        _publishedRemaps[_publishedRemapCount++] = remap;
        unchecked
        {
            ++_publishedRemapVersion;
        }
    }

    private bool CanPublishRemaps(int count)
        => count >= 0 && _publishedRemapCount <= _publishedRemaps.Length - count;

    private bool CanAppendPublicationDeltas(int count)
        => count >= 0 && _publicationDeltaCount <= _publicationDeltas.Length - count;

    private void AppendPublicationDelta(in AdvancedGpuRecordPublicationDelta delta)
        => _publicationDeltas[_publicationDeltaCount++] = delta;

    private void MarkDirty(uint index)
        => MarkDirty(index, 1u);

    private void MarkDirty(uint start, uint count)
    {
        if (count == 0u)
            return;

        uint endExclusive = checked(start + count);
        _dirtyMin = Math.Min(_dirtyMin, start);
        _dirtyMaxExclusive = Math.Max(_dirtyMaxExclusive, endExclusive);
    }

    private void MarkLogicalLookupDirty(uint slotIndex)
    {
        uint endExclusive = checked(slotIndex + 1u);
        _lookupDirtyMin = Math.Min(_lookupDirtyMin, slotIndex);
        _lookupDirtyMaxExclusive = Math.Max(
            _lookupDirtyMaxExclusive,
            endExclusive);
    }

    private static uint NextGeneration(uint generation)
    {
        unchecked
        {
            ++generation;
        }

        return generation == 0u ? 1u : generation;
    }

    private static int ValidateArrayCapacity(uint capacity)
    {
        if (capacity > int.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        return (int)capacity;
    }

    private static int GetRemapCapacity(int recordCapacity)
    {
        if (recordCapacity == 0)
            return 0;
        if (recordCapacity > int.MaxValue / 2)
            throw new ArgumentOutOfRangeException(nameof(recordCapacity));
        return checked(recordCapacity * 2);
    }

    private static int GetJournalCapacity(int recordCapacity)
    {
        if (recordCapacity == 0)
            return 0;
        if (recordCapacity > int.MaxValue / 4)
            throw new ArgumentOutOfRangeException(nameof(recordCapacity));
        return checked(recordCapacity * 4);
    }

    private static void FillInvalidDenseIndices(uint[] indices, int startIndex)
    {
        for (int index = startIndex; index < indices.Length; ++index)
            indices[index] = AdvancedGpuHandleRemap.InvalidDenseIndex;
    }
}
