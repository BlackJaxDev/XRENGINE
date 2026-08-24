namespace XREngine.Rendering.Vulkan;

/// <summary>Fixed-capacity dirty range set that coalesces overlapping writes without allocating.</summary>
internal struct VulkanFrameDataDirtyRanges
{
    private const int MaxRanges = 8;
    private VulkanDynamicDataDirtyRange _range0;
    private VulkanDynamicDataDirtyRange _range1;
    private VulkanDynamicDataDirtyRange _range2;
    private VulkanDynamicDataDirtyRange _range3;
    private VulkanDynamicDataDirtyRange _range4;
    private VulkanDynamicDataDirtyRange _range5;
    private VulkanDynamicDataDirtyRange _range6;
    private VulkanDynamicDataDirtyRange _range7;
    private byte _count;

    internal readonly int Count => _count;
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
    }

    internal readonly VulkanDynamicDataDirtyRange Get(int index) => index switch
    {
        0 => _range0, 1 => _range1, 2 => _range2, 3 => _range3,
        4 => _range4, 5 => _range5, 6 => _range6, 7 => _range7,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    internal void Clear() => _count = 0;

    private static bool RangesTouchOrOverlap(in VulkanDynamicDataDirtyRange left, in VulkanDynamicDataDirtyRange right)
        => left.Offset <= right.Offset + right.Length && right.Offset <= left.Offset + left.Length;

    private void Set(int index, in VulkanDynamicDataDirtyRange value)
    {
        switch (index)
        {
            case 0: _range0 = value; break; case 1: _range1 = value; break;
            case 2: _range2 = value; break; case 3: _range3 = value; break;
            case 4: _range4 = value; break; case 5: _range5 = value; break;
            case 6: _range6 = value; break; case 7: _range7 = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(index));
        }
    }
}
