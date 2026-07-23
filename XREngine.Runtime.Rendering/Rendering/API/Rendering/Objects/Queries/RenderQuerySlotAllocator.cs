namespace XREngine.Rendering;

/// <summary>
/// Fixed-capacity contiguous query-slot allocator. Allocation and release are
/// heap-allocation-free after construction.
/// </summary>
public sealed class RenderQuerySlotAllocator
{
    private readonly bool[] _occupied;

    public RenderQuerySlotAllocator(uint capacity)
    {
        if (capacity == 0u)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _occupied = new bool[capacity];
    }

    public uint Capacity => (uint)_occupied.Length;
    public uint Allocated { get; private set; }
    public uint HighWater { get; private set; }

    public bool TryAllocate(uint count, out uint first)
    {
        first = 0u;
        if (count == 0u || count > Capacity - Allocated)
            return false;

        uint run = 0u;
        for (uint slot = 0u; slot < Capacity; slot++)
        {
            if (_occupied[slot])
            {
                run = 0u;
                continue;
            }

            run++;
            if (run != count)
                continue;

            first = slot + 1u - count;
            uint end = first + count;
            for (uint occupied = first; occupied < end; occupied++)
                _occupied[occupied] = true;
            Allocated += count;
            HighWater = Math.Max(HighWater, Allocated);
            return true;
        }
        return false;
    }

    public bool Release(uint first, uint count)
    {
        if (count == 0u || first >= Capacity || count > Capacity - first)
            return false;
        for (uint slot = first; slot < first + count; slot++)
        {
            if (!_occupied[slot])
                return false;
        }
        for (uint slot = first; slot < first + count; slot++)
            _occupied[slot] = false;
        Allocated -= count;
        return true;
    }
}
