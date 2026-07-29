namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Allocates reusable non-zero binding identities for one wrapper data type.
/// </summary>
internal sealed class VulkanBindingSlotAllocator
{
    private readonly Lock _lock = new();
    private readonly Stack<uint> _free = new();
    private readonly HashSet<uint> _leased = [];
    private uint _next = 1;

    public uint Allocate()
    {
        lock (_lock)
        {
            uint bindingId = _free.Count > 0 ? _free.Pop() : _next++;
            if (bindingId == VkObjectBase.InvalidBindingId || !_leased.Add(bindingId))
                throw new InvalidOperationException("Vulkan binding identity allocation became inconsistent.");
            return bindingId;
        }
    }

    public void Release(uint bindingId)
    {
        if (bindingId == VkObjectBase.InvalidBindingId)
            return;

        lock (_lock)
        {
            if (!_leased.Remove(bindingId))
                return;
            _free.Push(bindingId);
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_lock)
                return _leased.Count;
        }
    }
}
