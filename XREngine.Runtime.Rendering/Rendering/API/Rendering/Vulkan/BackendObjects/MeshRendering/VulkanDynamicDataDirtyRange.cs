namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocation-free dirty-byte accumulator for one frame-indexed dynamic-data arena.
/// </summary>
internal struct VulkanDynamicDataDirtyRange
{
    private ulong _start;
    private ulong _end;

    public readonly bool IsEmpty => _end <= _start;
    public readonly ulong Offset => IsEmpty ? 0UL : _start;
    public readonly ulong Length => IsEmpty ? 0UL : _end - _start;

    public void Include(ulong offset, ulong length)
    {
        if (length == 0UL)
            return;

        ulong end = checked(offset + length);
        if (IsEmpty)
        {
            _start = offset;
            _end = end;
            return;
        }

        _start = Math.Min(_start, offset);
        _end = Math.Max(_end, end);
    }

    public void Clear()
    {
        _start = 0UL;
        _end = 0UL;
    }
}
