namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Maintains binding slots and generated wrappers for one render-object data type.
/// </summary>
internal sealed class VulkanBackendObjectBucket<T>
    where T : GenericRenderObject
{
    private readonly Lock _lock = new();
    private readonly Dictionary<uint, VkObject<T>> _bindingSlots = [];
    private readonly Dictionary<uint, VkObject<T>> _generatedWrappers = [];

    public void Cache(uint bindingId, VkObject<T> wrapper)
    {
        lock (_lock)
            _bindingSlots.Add(bindingId, wrapper);
    }

    public VkObject<T>? Get(uint bindingId)
    {
        lock (_lock)
            return _bindingSlots.GetValueOrDefault(bindingId);
    }

    public void Publish(uint bindingId, VkObject<T> wrapper)
    {
        lock (_lock)
            _generatedWrappers[bindingId] = wrapper;
    }

    public void Remove(uint bindingId)
    {
        lock (_lock)
        {
            _generatedWrappers.Remove(bindingId);
            _bindingSlots.Remove(bindingId);
        }
    }

    public VkObject<T>[] Snapshot()
    {
        lock (_lock)
            return [.. _generatedWrappers.Values];
    }
}
