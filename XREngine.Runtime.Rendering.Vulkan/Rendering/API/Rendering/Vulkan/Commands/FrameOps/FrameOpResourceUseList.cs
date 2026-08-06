using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Vulkan;

[InlineArray(24)]
internal struct FrameOpResourceUseBuffer
{
    private FrameOpResourceUse _element0;
}

/// <summary>Fixed-capacity per-operation use list; overflow is a planning error.</summary>
internal struct FrameOpResourceUseList
{
    private FrameOpResourceUseBuffer _items;
    public int Count { get; private set; }

    public readonly FrameOpResourceUse this[int index] => _items[index];

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
        if (Count >= 24)
            throw new InvalidOperationException("Frame-operation resource-use capacity exceeded.");
        _items[Count++] = new(resourceId, version, access);
    }
}
