using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free publication queue for one frequency owner and frame slot.
/// The queue coalesces overlapping or adjacent ranges and collapses to the
/// complete payload when a schema contains more ranges than the fixed budget.
/// </summary>
internal struct VulkanAutoUniformDirtyRangeQueue
{
    internal const int Capacity = 16;

    private VulkanAutoUniformDirtyRangeStorage _ranges;
    private int _count;

    internal readonly int Count => _count;

    internal void Publish(
        ReadOnlySpan<VulkanAutoUniformDirtyRange> ranges,
        uint payloadSize)
    {
        _count = 0;
        for (int i = 0; i < ranges.Length; i++)
            Enqueue(ranges[i], payloadSize);
    }

    internal readonly VulkanAutoUniformDirtyRange GetRange(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _ranges[index];
    }

    internal void Reset()
        => _count = 0;

    private void Enqueue(
        VulkanAutoUniformDirtyRange range,
        uint payloadSize)
    {
        if (range.Size == 0)
            return;
        if (range.Offset > payloadSize ||
            range.Size > payloadSize - range.Offset)
        {
            throw new ArgumentOutOfRangeException(
                nameof(range),
                "Dirty range must fit inside its frequency-owned payload.");
        }

        uint start = range.Offset;
        uint end = range.End;
        int insertionIndex = 0;
        while (insertionIndex < _count)
        {
            VulkanAutoUniformDirtyRange existing =
                _ranges[insertionIndex];
            if (end < existing.Offset)
                break;
            if (start > existing.End)
            {
                insertionIndex++;
                continue;
            }

            start = Math.Min(start, existing.Offset);
            end = Math.Max(end, existing.End);
            RemoveAt(insertionIndex);
        }

        if (_count == Capacity)
        {
            _ranges[0] = new VulkanAutoUniformDirtyRange(0, payloadSize);
            _count = 1;
            return;
        }

        for (int i = _count; i > insertionIndex; i--)
            _ranges[i] = _ranges[i - 1];
        _ranges[insertionIndex] =
            new VulkanAutoUniformDirtyRange(start, end - start);
        _count++;
    }

    private void RemoveAt(int index)
    {
        for (int i = index; i < _count - 1; i++)
            _ranges[i] = _ranges[i + 1];
        _count--;
    }

    [InlineArray(Capacity)]
    private struct VulkanAutoUniformDirtyRangeStorage
    {
        private VulkanAutoUniformDirtyRange _element0;
    }
}
