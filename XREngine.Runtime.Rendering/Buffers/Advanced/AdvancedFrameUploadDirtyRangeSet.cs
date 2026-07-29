namespace XREngine.Rendering;

/// <summary>
/// Fixed-capacity, allocation-free dirty range accumulator. When a stream exceeds
/// its range budget, the ranges collapse to one conservative contiguous envelope.
/// </summary>
internal sealed class AdvancedFrameUploadDirtyRangeSet
{
    private readonly AdvancedFrameUploadDirtyRange[] _ranges;
    private readonly int[] _counts;
    private readonly int _rangeCapacityPerStream;

    public AdvancedFrameUploadDirtyRangeSet(int rangeCapacityPerStream)
    {
        if (rangeCapacityPerStream < 1)
            throw new ArgumentOutOfRangeException(nameof(rangeCapacityPerStream));

        _rangeCapacityPerStream = rangeCapacityPerStream;
        _ranges = new AdvancedFrameUploadDirtyRange[
            AdvancedFrameUploadCapacityProfile.StreamCount * rangeCapacityPerStream];
        _counts = new int[AdvancedFrameUploadCapacityProfile.StreamCount];
    }

    public int TotalRangeCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _counts.Length; i++)
                count += _counts[i];
            return count;
        }
    }

    public void Clear()
        => Array.Clear(_counts);

    public void Include(
        EAdvancedFrameUploadStream stream,
        uint offsetBytes,
        uint byteCount)
    {
        if (byteCount == 0u)
            return;

        int streamIndex = ValidateStream(stream);
        int baseIndex = streamIndex * _rangeCapacityPerStream;
        int count = _counts[streamIndex];
        ulong mergedStart = offsetBytes;
        ulong mergedEnd = checked((ulong)offsetBytes + byteCount);

        for (int i = 0; i < count;)
        {
            AdvancedFrameUploadDirtyRange current = _ranges[baseIndex + i];
            if (mergedStart > current.EndBytes || current.OffsetBytes > mergedEnd)
            {
                i++;
                continue;
            }

            mergedStart = Math.Min(mergedStart, current.OffsetBytes);
            mergedEnd = Math.Max(mergedEnd, current.EndBytes);
            RemoveAt(baseIndex, ref count, i);
        }

        if (count == _rangeCapacityPerStream)
        {
            for (int i = 0; i < count; i++)
            {
                AdvancedFrameUploadDirtyRange current = _ranges[baseIndex + i];
                mergedStart = Math.Min(mergedStart, current.OffsetBytes);
                mergedEnd = Math.Max(mergedEnd, current.EndBytes);
            }

            _ranges[baseIndex] = CreateRange(mergedStart, mergedEnd);
            _counts[streamIndex] = 1;
            return;
        }

        int insertIndex = count;
        while (insertIndex > 0 &&
               _ranges[baseIndex + insertIndex - 1].OffsetBytes > mergedStart)
        {
            _ranges[baseIndex + insertIndex] =
                _ranges[baseIndex + insertIndex - 1];
            insertIndex--;
        }

        _ranges[baseIndex + insertIndex] = CreateRange(mergedStart, mergedEnd);
        _counts[streamIndex] = count + 1;
    }

    public int CopyTo(
        Span<AdvancedUploadCopyRange> destination,
        int destinationOffset,
        ulong storageGeneration,
        uint frameSlot,
        bool isOverflow)
    {
        int writeIndex = destinationOffset;
        for (int streamIndex = 0; streamIndex < _counts.Length; streamIndex++)
        {
            int count = _counts[streamIndex];
            int baseIndex = streamIndex * _rangeCapacityPerStream;
            for (int i = 0; i < count; i++)
            {
                AdvancedFrameUploadDirtyRange range = _ranges[baseIndex + i];
                destination[writeIndex++] = new AdvancedUploadCopyRange(
                    (EAdvancedFrameUploadStream)streamIndex,
                    storageGeneration,
                    frameSlot,
                    range.OffsetBytes,
                    range.OffsetBytes,
                    range.ByteCount,
                    isOverflow);
            }
        }

        return writeIndex - destinationOffset;
    }

    private static int ValidateStream(EAdvancedFrameUploadStream stream)
    {
        int streamIndex = (int)stream;
        if ((uint)streamIndex >= AdvancedFrameUploadCapacityProfile.StreamCount)
            throw new ArgumentOutOfRangeException(nameof(stream), stream, null);
        return streamIndex;
    }

    private void RemoveAt(int baseIndex, ref int count, int index)
    {
        for (int i = index + 1; i < count; i++)
            _ranges[baseIndex + i - 1] = _ranges[baseIndex + i];
        count--;
    }

    private static AdvancedFrameUploadDirtyRange CreateRange(
        ulong start,
        ulong end)
    {
        if (start > uint.MaxValue || end > uint.MaxValue || end < start)
            throw new InvalidOperationException("Advanced upload dirty range exceeds the supported 32-bit buffer range.");

        return new AdvancedFrameUploadDirtyRange(
            (uint)start,
            checked((uint)(end - start)));
    }
}
