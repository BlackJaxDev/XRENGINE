namespace XREngine.Rendering.Commands;

/// <summary>
/// Preallocated immutable copy of one table's structural publication. The
/// publication-ring owner creates one snapshot per retained ring entry, then
/// asks the live table to capture into it while sealing that entry.
/// </summary>
public sealed class AdvancedGpuRecordTablePublicationSnapshot<T> where T : unmanaged
{
    private readonly T[] _physicalRecords;
    private readonly AdvancedGpuHandle[] _physicalHandles;
    private readonly byte[] _physicalOccupancy;
    private readonly AdvancedGpuHandleLookup[] _handleLookups;
    private readonly AdvancedGpuRecordPublicationDelta[] _deltas;
    private readonly AdvancedGpuHandleRemap[] _remaps;
    private int _physicalHighWater;
    private int _recordCount;
    private int _logicalLookupCount;
    private int _deltaCount;
    private int _remapCount;

    public AdvancedGpuRecordTablePublicationSnapshot(
        int deltaCapacity,
        int remapCapacity,
        int recordCapacity = 0)
    {
        if (deltaCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaCapacity));
        if (remapCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(remapCapacity));
        if (recordCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(recordCapacity));

        _physicalRecords = new T[recordCapacity];
        _physicalHandles = new AdvancedGpuHandle[recordCapacity];
        _physicalOccupancy = new byte[recordCapacity];
        _handleLookups = recordCapacity == 0
            ? []
            : new AdvancedGpuHandleLookup[checked(recordCapacity + 1)];
        _deltas = new AdvancedGpuRecordPublicationDelta[deltaCapacity];
        _remaps = new AdvancedGpuHandleRemap[remapCapacity];
        if (_handleLookups.Length > 0)
            FillInvalidLookups(_handleLookups);
    }

    public ulong Sequence { get; private set; }

    /// <summary>
    /// Oldest publication generation represented by <see cref="Deltas"/>.
    /// A consumer whose applied sequence precedes this floor cannot safely
    /// patch a retained image and must materialize a fresh image instead.
    /// </summary>
    public ulong JournalFloorSequence { get; private set; }

    public bool HasRetainedJournal => _deltaCount != 0;

    public AdvancedGpuOwnerGenerations Generations { get; private set; }

    /// <summary>
    /// Whether this snapshot retains an exact physical record and logical
    /// lookup image in addition to its structural deltas.
    /// </summary>
    public bool HasRecordImage => _physicalRecords.Length > 0;

    public int RecordCount => _recordCount;

    public ReadOnlySpan<T> PhysicalRecords
        => _physicalRecords.AsSpan(0, _physicalHighWater);

    public ReadOnlySpan<AdvancedGpuHandle> PhysicalHandles
        => _physicalHandles.AsSpan(0, _physicalHighWater);

    public ReadOnlySpan<byte> PhysicalOccupancy
        => _physicalOccupancy.AsSpan(0, _physicalHighWater);

    public ReadOnlySpan<AdvancedGpuHandleLookup> HandleLookups
        => _handleLookups.AsSpan(0, _logicalLookupCount);

    public ReadOnlySpan<AdvancedGpuRecordPublicationDelta> Deltas
        => _deltas.AsSpan(0, _deltaCount);

    public ReadOnlySpan<AdvancedGpuHandleRemap> Remaps
        => _remaps.AsSpan(0, _remapCount);

    internal int DeltaCapacity => _deltas.Length;

    internal int RemapCapacity => _remaps.Length;

    internal int RecordCapacity => _physicalRecords.Length;

    public bool TryGet(AdvancedGpuHandle handle, out T record)
    {
        if (!TryGetDenseIndex(handle, out uint denseIndex))
        {
            record = default;
            return false;
        }

        record = _physicalRecords[checked((int)denseIndex)];
        return true;
    }

    public bool TryGetDenseIndex(AdvancedGpuHandle handle, out uint denseIndex)
    {
        denseIndex = AdvancedGpuHandleRemap.InvalidDenseIndex;
        if (!HasRecordImage || !handle.IsValid ||
            handle.Index >= (uint)_logicalLookupCount)
        {
            return false;
        }

        AdvancedGpuHandleLookup lookup = _handleLookups[checked((int)handle.Index)];
        if (lookup.Generation != handle.Generation ||
            lookup.DenseIndex >= (uint)_physicalHighWater ||
            _physicalOccupancy[checked((int)lookup.DenseIndex)] == 0 ||
            _physicalHandles[checked((int)lookup.DenseIndex)] != handle)
        {
            return false;
        }

        denseIndex = lookup.DenseIndex;
        return true;
    }

    internal bool TryCapture(
        ulong sequence,
        ReadOnlySpan<AdvancedGpuRecordPublicationDelta> deltas,
        ReadOnlySpan<AdvancedGpuHandleRemap> remaps,
        ReadOnlySpan<T> physicalRecords,
        ReadOnlySpan<AdvancedGpuHandle> physicalHandles,
        ReadOnlySpan<byte> physicalOccupancy,
        AdvancedGpuRecordTable<T> sourceTable)
    {
        ArgumentNullException.ThrowIfNull(sourceTable);
        Sequence = 0u;
        JournalFloorSequence = 0u;
        Generations = default;
        if (sequence == 0u ||
            deltas.Length > _deltas.Length ||
            remaps.Length > _remaps.Length ||
            physicalRecords.Length != physicalHandles.Length ||
            physicalRecords.Length != physicalOccupancy.Length ||
            HasRecordImage &&
            (physicalRecords.Length > _physicalRecords.Length ||
             sourceTable.LogicalLookupCount > (uint)_handleLookups.Length))
        {
            return false;
        }

        if (HasRecordImage && !TryCaptureRecordImage(
                physicalRecords,
                physicalHandles,
                physicalOccupancy,
                sourceTable))
        {
            return false;
        }

        deltas.CopyTo(_deltas);
        remaps.CopyTo(_remaps);
        _deltaCount = deltas.Length;
        _remapCount = remaps.Length;
        Sequence = sequence;
        Generations = sourceTable.Generations;
        JournalFloorSequence = deltas.IsEmpty
            ? sequence
            : deltas[0].PublicationGeneration;
        return true;
    }

    private bool TryCaptureRecordImage(
        ReadOnlySpan<T> physicalRecords,
        ReadOnlySpan<AdvancedGpuHandle> physicalHandles,
        ReadOnlySpan<byte> physicalOccupancy,
        AdvancedGpuRecordTable<T> sourceTable)
    {
        ClearPreviousRecordImage();
        physicalRecords.CopyTo(_physicalRecords);
        physicalHandles.CopyTo(_physicalHandles);
        physicalOccupancy.CopyTo(_physicalOccupancy);
        _physicalHighWater = physicalRecords.Length;

        for (int denseIndex = 0; denseIndex < _physicalHighWater; ++denseIndex)
        {
            if (_physicalOccupancy[denseIndex] == 0)
                continue;

            AdvancedGpuHandle handle = _physicalHandles[denseIndex];
            if (!handle.IsValid || handle.Index >= (uint)_handleLookups.Length)
            {
                ClearPreviousRecordImage();
                return false;
            }
        }

        if (!sourceTable.CopyLogicalLookups(
                _handleLookups,
                out _logicalLookupCount))
        {
            ClearPreviousRecordImage();
            return false;
        }

        int count = 0;
        for (uint slotIndex = 1u;
             slotIndex < (uint)_logicalLookupCount;
             ++slotIndex)
        {
            AdvancedGpuHandleLookup lookup =
                _handleLookups[checked((int)slotIndex)];
            if (!lookup.IsResident)
                continue;

            if (lookup.DenseIndex >= (uint)_physicalHighWater ||
                _physicalOccupancy[checked((int)lookup.DenseIndex)] == 0 ||
                _physicalHandles[checked((int)lookup.DenseIndex)] !=
                    new AdvancedGpuHandle(slotIndex, lookup.Generation))
            {
                ClearPreviousRecordImage();
                return false;
            }

            ++count;
        }

        _recordCount = count;
        return true;
    }

    private void ClearPreviousRecordImage()
    {
        FillInvalidLookups(_handleLookups.AsSpan(0, _logicalLookupCount));
        _physicalRecords.AsSpan(0, _physicalHighWater).Clear();
        _physicalHandles.AsSpan(0, _physicalHighWater).Clear();
        _physicalOccupancy.AsSpan(0, _physicalHighWater).Clear();
        _physicalHighWater = 0;
        _recordCount = 0;
        _logicalLookupCount = 0;
    }

    private static void FillInvalidLookups(Span<AdvancedGpuHandleLookup> lookups)
    {
        for (int index = 0; index < lookups.Length; ++index)
            lookups[index] = AdvancedGpuHandleLookup.Invalid;
    }
}
