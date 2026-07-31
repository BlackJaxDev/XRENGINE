namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Bounded mapping from a frequency owner to its stable physical payload slot.
/// It also publishes the exact owner slot selected for each logical draw so
/// descriptor-offset preparation consumes the same location.
/// </summary>
internal sealed class VulkanAutoUniformOwnerSlotTable
{
    private readonly Dictionary<ulong, int> _ownerSlots;
    private readonly int[] _publishedOwnerSlots;

    internal VulkanAutoUniformOwnerSlotTable(
        int frameCount,
        int drawSlotCapacity)
    {
        FrameCount = Math.Max(frameCount, 1);
        DrawSlotCapacity = Math.Max(drawSlotCapacity, 1);
        _ownerSlots = new Dictionary<ulong, int>(DrawSlotCapacity);
        _publishedOwnerSlots =
            new int[checked(FrameCount * DrawSlotCapacity)];
        Array.Fill(_publishedOwnerSlots, -1);
    }

    internal int FrameCount { get; }
    internal int DrawSlotCapacity { get; }
    internal int OwnerCount => _ownerSlots.Count;

    internal int ResolveAndPublish(
        int frameIndex,
        int drawSlot,
        ulong ownerIdentity)
    {
        int fallbackSlot = Math.Clamp(
            drawSlot,
            0,
            DrawSlotCapacity - 1);
        int ownerSlot;
        if (ownerIdentity == 0)
        {
            ownerSlot = fallbackSlot;
        }
        else if (!_ownerSlots.TryGetValue(ownerIdentity, out ownerSlot))
        {
            ownerSlot = _ownerSlots.Count;
            if (ownerSlot >= DrawSlotCapacity)
                ownerSlot = fallbackSlot;
            else
                _ownerSlots.Add(ownerIdentity, ownerSlot);
        }

        _publishedOwnerSlots[
            ResolveLogicalIndex(frameIndex, drawSlot)] = ownerSlot;
        return ownerSlot;
    }

    internal int ResolvePublished(int frameIndex, int drawSlot)
    {
        int fallbackSlot = Math.Clamp(
            drawSlot,
            0,
            DrawSlotCapacity - 1);
        int ownerSlot =
            _publishedOwnerSlots[
                ResolveLogicalIndex(frameIndex, drawSlot)];
        return ownerSlot < 0 ? fallbackSlot : ownerSlot;
    }

    private int ResolveLogicalIndex(int frameIndex, int drawSlot)
    {
        int frame = Math.Clamp(frameIndex, 0, FrameCount - 1);
        int slot = Math.Clamp(drawSlot, 0, DrawSlotCapacity - 1);
        return checked(frame * DrawSlotCapacity + slot);
    }
}
