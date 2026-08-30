using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

/// <summary>Fixed-capacity dirty range set that coalesces overlapping writes without allocating.</summary>
internal struct VulkanFrameDataDirtyRanges
{
    private const int MaxRanges = 32;
    private VulkanFrameDataDirtyRangeStorage _ranges;
    private byte _count;
    private uint _capacityCollapseCount;

    internal readonly int Count => _count;
    internal readonly uint CapacityCollapseCount => _capacityCollapseCount;
    internal readonly VulkanFrameDataDirtyRangeCollapseReason CollapseReason
        => _capacityCollapseCount == 0
            ? VulkanFrameDataDirtyRangeCollapseReason.None
            : VulkanFrameDataDirtyRangeCollapseReason.CapacityExceeded;
    internal readonly ulong TotalLength
    {
        get
        {
            ulong total = 0;
            for (int index = 0; index < _count; index++)
                total += Get(index).Length;
            return total;
        }
    }

    internal void Include(ulong offset, ulong length)
    {
        if (length == 0)
            return;
        VulkanDynamicDataDirtyRange merged = default;
        merged.Include(offset, length);
        int writeIndex = 0;
        for (int readIndex = 0; readIndex < _count; readIndex++)
        {
            VulkanDynamicDataDirtyRange current = Get(readIndex);
            if (RangesTouchOrOverlap(current, merged))
            {
                merged.Include(current.Offset, current.Length);
                continue;
            }
            Set(writeIndex++, current);
        }
        if (writeIndex < MaxRanges)
        {
            Set(writeIndex, merged);
            _count = (byte)(writeIndex + 1);
            return;
        }

        // A bounded set must never allocate: collapse to one conservative range on overflow.
        for (int index = 0; index < writeIndex; index++)
            merged.Include(Get(index).Offset, Get(index).Length);
        Set(0, merged);
        _count = 1;
        if (_capacityCollapseCount != uint.MaxValue)
            _capacityCollapseCount++;
    }

    internal readonly VulkanDynamicDataDirtyRange Get(int index)
    {
        if ((uint)index >= _count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _ranges[index];
    }

    internal void Clear()
    {
        _count = 0;
        _capacityCollapseCount = 0;
    }

    private static bool RangesTouchOrOverlap(in VulkanDynamicDataDirtyRange left, in VulkanDynamicDataDirtyRange right)
        => left.Offset <= right.Offset + right.Length && right.Offset <= left.Offset + left.Length;

    private void Set(int index, in VulkanDynamicDataDirtyRange value)
    {
        if ((uint)index >= MaxRanges)
            throw new ArgumentOutOfRangeException(nameof(index));
        _ranges[index] = value;
    }

    [InlineArray(MaxRanges)]
    private struct VulkanFrameDataDirtyRangeStorage
    {
        private VulkanDynamicDataDirtyRange _element0;
    }
}
