namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns wrapper binding identities for one renderer/device generation.
/// Allocation is deliberately separate from wrapper publication.
/// </summary>
internal sealed class VulkanBindingAllocator
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Type, VulkanBindingSlotAllocator> _allocators = [];

    public uint Allocate<T>()
        where T : GenericRenderObject
        => GetAllocator(typeof(T)).Allocate();

    public void Release<T>(uint bindingId)
        where T : GenericRenderObject
        => GetAllocator(typeof(T)).Release(bindingId);

    public int ActiveCount<T>()
        where T : GenericRenderObject
        => GetAllocator(typeof(T)).ActiveCount;

    private VulkanBindingSlotAllocator GetAllocator(Type dataType)
    {
        lock (_lock)
        {
            if (_allocators.TryGetValue(dataType, out VulkanBindingSlotAllocator? allocator))
                return allocator;

            allocator = new VulkanBindingSlotAllocator();
            _allocators.Add(dataType, allocator);
            return allocator;
        }
    }
}
