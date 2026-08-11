namespace XREngine.Rendering.Vulkan;

/// <summary>Fixed-capacity per-operation use list; overflow is a planning error.</summary>
internal struct FrameOpResourceUseList
{
    private FrameOpResourceUseBuffer _items;
    public int Count { get; private set; }

    public readonly FrameOpResourceUse this[int index] => _items[index];

    public void Clear() => Count = 0;

    public void Add(ulong resourceId, ulong version, EFrameOpResourceAccess access)
    {
        if (resourceId == 0UL)
            throw new InvalidOperationException("Frame-operation resource identity must be non-zero.");
        for (int index = 0; index < Count; index++)
        {
            FrameOpResourceUse current = _items[index];
            if (current.ResourceId != resourceId || current.Version != version)
                continue;
            _items[index] = current with { Access = current.Access | access };
            return;
        }
        if (Count >= FrameOpResourceUseBuffer.Capacity)
        {
            throw new InvalidOperationException(
                $"Frame-operation resource-use capacity of {FrameOpResourceUseBuffer.Capacity} was exceeded.");
        }
        _items[Count++] = new(resourceId, version, access);
    }
}
