namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns Vulkan wrapper binding identities for one renderer and logical-device lifetime.
/// </summary>
internal sealed class VulkanBackendObjectRegistry
{
    private readonly Lock _bucketsLock = new();
    private readonly Dictionary<Type, object> _buckets = [];
    public VulkanBindingAllocator BindingAllocator { get; } = new();

    public uint Cache<T>(VkObject<T> wrapper)
        where T : GenericRenderObject
    {
        uint bindingId = BindingAllocator.Allocate<T>();
        try
        {
            GetBucket<T>().Cache(bindingId, wrapper);
            return bindingId;
        }
        catch
        {
            BindingAllocator.Release<T>(bindingId);
            throw;
        }
    }

    public VkObject<T>? Get<T>(uint bindingId)
        where T : GenericRenderObject
        => GetBucket<T>().Get(bindingId);

    public void Publish<T>(uint bindingId, VkObject<T> wrapper)
        where T : GenericRenderObject
        => GetBucket<T>().Publish(bindingId, wrapper);

    public void Remove<T>(uint bindingId)
        where T : GenericRenderObject
    {
        GetBucket<T>().Remove(bindingId);
        BindingAllocator.Release<T>(bindingId);
    }

    public VkObject<T>[] Snapshot<T>()
        where T : GenericRenderObject
        => GetBucket<T>().Snapshot();

    private VulkanBackendObjectBucket<T> GetBucket<T>()
        where T : GenericRenderObject
    {
        lock (_bucketsLock)
        {
            Type dataType = typeof(T);
            if (_buckets.TryGetValue(dataType, out object? existing))
                return (VulkanBackendObjectBucket<T>)existing;

            VulkanBackendObjectBucket<T> bucket = new();
            _buckets.Add(dataType, bucket);
            return bucket;
        }
    }
}
