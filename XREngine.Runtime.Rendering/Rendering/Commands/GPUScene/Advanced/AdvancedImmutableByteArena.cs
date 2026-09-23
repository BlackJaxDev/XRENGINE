namespace XREngine.Rendering.Commands;

/// <summary>
/// Append-only scene-owned byte arena. Appends never grow implicitly; capacity
/// changes and generation replacement are explicit boundary operations.
/// </summary>
public sealed class AdvancedImmutableByteArena
{
    private byte[] _data;
    private uint _countBytes;
    private uint _dirtyMin = uint.MaxValue;
    private uint _dirtyMaxExclusive;
    private uint _bufferIndex;
    private uint _generation = 1u;

    public AdvancedImmutableByteArena(uint bufferIndex, uint capacityBytes)
    {
        if (bufferIndex == 0u)
            throw new ArgumentOutOfRangeException(nameof(bufferIndex));
        if (capacityBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));

        _bufferIndex = bufferIndex;
        _data = new byte[(int)capacityBytes];
    }

    public AdvancedGpuHandle BufferHandle => new(_bufferIndex, _generation);

    public uint CountBytes => _countBytes;

    public uint CapacityBytes => (uint)_data.Length;

    public ReadOnlySpan<byte> Data
        => _data.AsSpan(0, checked((int)_countBytes));

    public AdvancedGpuDirtyRange DirtyByteRange
        => _dirtyMin == uint.MaxValue
            ? AdvancedGpuDirtyRange.Empty
            : new AdvancedGpuDirtyRange(_dirtyMin, _dirtyMaxExclusive - _dirtyMin);

    public bool CanAppend(uint byteCount, uint elementStride)
    {
        if (byteCount == 0u || elementStride == 0u || byteCount % elementStride != 0u)
            return false;

        uint alignedOffset = AlignUp(_countBytes, elementStride);
        return (ulong)alignedOffset + byteCount <= CapacityBytes;
    }

    public bool TryAppend(
        ReadOnlySpan<byte> data,
        uint elementStride,
        out AdvancedBufferReference reference)
    {
        reference = AdvancedBufferReference.Invalid;
        if (!CanAppend((uint)data.Length, elementStride))
            return false;

        uint alignedOffset = AlignUp(_countBytes, elementStride);
        uint padding = alignedOffset - _countBytes;
        if (padding > 0u)
            _data.AsSpan(checked((int)_countBytes), checked((int)padding)).Clear();

        data.CopyTo(_data.AsSpan(checked((int)alignedOffset), data.Length));
        uint elementCount = (uint)data.Length / elementStride;
        reference = new AdvancedBufferReference(
            BufferHandle,
            alignedOffset,
            alignedOffset / elementStride,
            elementCount,
            elementStride,
            0u);
        _countBytes = checked(alignedOffset + (uint)data.Length);
        MarkDirty(alignedOffset, (uint)data.Length);
        return true;
    }

    public void GrowAtBoundary(uint requiredCapacityBytes)
    {
        if (requiredCapacityBytes <= CapacityBytes)
            return;
        if (requiredCapacityBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requiredCapacityBytes));

        Array.Resize(ref _data, checked((int)requiredCapacityBytes));
        if (_countBytes > 0u)
            MarkDirty(0u, _countBytes);
    }

    /// <summary>
    /// Creates an empty, next-generation arena for a boundary-only copy-forward
    /// transaction. The current backing image remains untouched until the caller
    /// has copied every live reference and atomically adopts the successor.
    /// </summary>
    internal bool TryCreateSuccessorAtBoundary(
        uint capacityBytes,
        out AdvancedImmutableByteArena successor)
    {
        successor = null!;
        if (capacityBytes > int.MaxValue || _generation == uint.MaxValue)
            return false;

        successor = new AdvancedImmutableByteArena(_bufferIndex, capacityBytes)
        {
            _generation = _generation + 1u,
        };
        return true;
    }

    /// <summary>
    /// Copies a live reference into a staged successor while preserving its ABI
    /// flags. The source handle and exact byte extent are validated so a stale
    /// row cannot be made current by a compaction transaction.
    /// </summary>
    internal bool TryCopyReferenceTo(
        AdvancedImmutableByteArena successor,
        in AdvancedBufferReference source,
        out AdvancedBufferReference remapped)
    {
        remapped = AdvancedBufferReference.Invalid;
        ArgumentNullException.ThrowIfNull(successor);
        if (!source.IsValid || source.Buffer != BufferHandle ||
            source.ByteOffset > _countBytes ||
            source.ByteLength > _countBytes - source.ByteOffset ||
            source.ByteOffset % source.ElementStride != 0u ||
            source.ElementOffset != source.ByteOffset / source.ElementStride ||
            !successor.TryAppend(
                _data.AsSpan(
                    checked((int)source.ByteOffset),
                    checked((int)source.ByteLength)),
                source.ElementStride,
                out AdvancedBufferReference copied))
        {
            return false;
        }

        remapped = copied with { Flags = source.Flags };
        return true;
    }

    internal bool IsCurrentReference(in AdvancedBufferReference source)
        => source.IsValid && source.Buffer == BufferHandle &&
           source.ByteOffset <= _countBytes &&
           source.ByteLength <= _countBytes - source.ByteOffset &&
           source.ByteOffset % source.ElementStride == 0u &&
           source.ElementOffset == source.ByteOffset / source.ElementStride;

    /// <summary>
    /// Replaces this arena with a fully staged successor. Call only after every
    /// dependent geometry row has been preflighted for a coherent publication.
    /// </summary>
    internal bool TryAdoptSuccessorAtBoundary(AdvancedImmutableByteArena successor)
    {
        ArgumentNullException.ThrowIfNull(successor);
        if (!CanAdoptSuccessorAtBoundary(successor))
            return false;

        _data = successor._data;
        _countBytes = successor._countBytes;
        _dirtyMin = successor._dirtyMin;
        _dirtyMaxExclusive = successor._dirtyMaxExclusive;
        _generation = successor._generation;
        return true;
    }

    internal bool CanAdoptSuccessorAtBoundary(AdvancedImmutableByteArena successor)
        => successor is not null && _generation < uint.MaxValue &&
           successor._bufferIndex == _bufferIndex &&
           successor._generation == _generation + 1u;

    /// <summary>
    /// Invalidates every old reference and starts a new immutable generation.
    /// </summary>
    public void ResetAtBoundary()
    {
        uint previousCount = _countBytes;
        // Publications retain the previous backing array. Reusing it here would
        // make a valid old-generation slice observe new bytes after an ABA cycle.
        _data = new byte[_data.Length];
        _countBytes = 0u;
        unchecked
        {
            ++_generation;
        }
        if (_generation == 0u)
            _generation = 1u;
        if (previousCount > 0u)
            MarkDirty(0u, previousCount);
    }

    public void ClearDirtyRange()
    {
        _dirtyMin = uint.MaxValue;
        _dirtyMaxExclusive = 0u;
    }

    internal AdvancedImmutableByteArenaPublicationSnapshot CapturePublicationSnapshot()
    {
        AdvancedImmutableByteArenaPublicationSnapshot snapshot = new(
            _data,
            BufferHandle,
            _countBytes,
            DirtyByteRange);
        ClearDirtyRange();
        return snapshot;
    }

    private void MarkDirty(uint start, uint count)
    {
        if (count == 0u)
            return;

        _dirtyMin = Math.Min(_dirtyMin, start);
        _dirtyMaxExclusive = Math.Max(_dirtyMaxExclusive, checked(start + count));
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        uint remainder = value % alignment;
        return remainder == 0u
            ? value
            : checked(value + alignment - remainder);
    }
}
