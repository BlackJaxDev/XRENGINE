namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reusable authoring-side resource-use storage. The backing array grows only
/// while a worker's retained operation pool learns its high-water mark; sealed
/// operation streams copy the populated prefix into their own flat column.
/// </summary>
internal struct FrameOpResourceUseList
{
    private const int InitialCapacity = 8;
    private FrameOpResourceUse[]? _items;
    public int Count { get; private set; }

    public readonly FrameOpResourceUse this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _items![index];
        }
    }

    public void Clear() => Count = 0;

    public void Add(ulong resourceId, ulong version, EFrameOpResourceAccess access)
    {
        if (resourceId == 0UL)
            throw new InvalidOperationException("Frame-operation resource identity must be non-zero.");
        for (int index = 0; index < Count; index++)
        {
            FrameOpResourceUse current = _items![index];
            if (current.ResourceId != resourceId || current.Version != version)
                continue;
            _items[index] = current with { Access = current.Access | access };
            return;
        }
        EnsureCapacity(Count + 1);
        _items![Count++] = new(resourceId, version, access);
    }

    internal readonly void CopyTo(Span<FrameOpResourceUse> destination)
    {
        if (destination.Length < Count)
            throw new ArgumentException(
                "The destination is smaller than the resource-use list.",
                nameof(destination));
        if (Count > 0)
            _items.AsSpan(0, Count).CopyTo(destination);
    }

    private void EnsureCapacity(int required)
    {
        if ((_items?.Length ?? 0) >= required)
            return;

        int current = _items?.Length ?? 0;
        int capacity = Math.Max(
            required,
            current == 0 ? InitialCapacity : checked(current * 2));
        Array.Resize(ref _items, capacity);
    }
}
