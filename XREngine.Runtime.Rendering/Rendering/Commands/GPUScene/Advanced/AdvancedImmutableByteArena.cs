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
    /// Invalidates every old reference and starts a new immutable generation.
    /// </summary>
    public void ResetAtBoundary()
    {
        uint previousCount = _countBytes;
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
