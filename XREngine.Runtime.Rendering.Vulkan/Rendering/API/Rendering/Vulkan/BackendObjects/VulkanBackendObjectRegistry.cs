namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns Vulkan wrapper binding identities for one renderer and logical-device lifetime.
/// </summary>
internal sealed class VulkanBackendObjectRegistry
{
    private readonly Lock _bucketsLock = new();
    internal Lock IdentityCreationLock { get; } = new();
    private readonly Dictionary<Type, IVulkanBackendObjectBucket> _buckets = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<GenericRenderObject, VkObjectBase> _byData = new();
    public VulkanBindingAllocator BindingAllocator { get; } = new();

    public uint Cache<T>(VkObject<T> wrapper)
        where T : GenericRenderObject
    {
        uint bindingId = BindingAllocator.Allocate<T>();
        try
        {
            GetBucket<T>().Cache(bindingId, wrapper);
            _byData[wrapper.Data] = wrapper;
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
    {
        GetBucket<T>().Publish(bindingId, wrapper);
        _byData[wrapper.Data] = wrapper;
    }

    public VkObjectBase? Get(GenericRenderObject data)
    {
        if (!_byData.TryGetValue(data, out VkObjectBase? wrapper))
            return null;
        if (!wrapper.IsRetired && !data.IsDestroyed && data.IsApiWrapperPublicationReady)
            return wrapper;

        if (_byData.TryGetValue(data, out VkObjectBase? current) && ReferenceEquals(current, wrapper))
            _byData.TryRemove(data, out _);
        return null;
    }

    /// <summary>
    /// Publishes wrapper identity before native generation so other wrappers can
    /// resolve dependency objects while linking their engine data.
    /// </summary>
    public void PublishIdentity(GenericRenderObject data, VkObjectBase wrapper)
    {
        if (_byData.TryGetValue(data, out VkObjectBase? current) && !ReferenceEquals(current, wrapper))
            throw new InvalidOperationException("A different Vulkan wrapper is already published for this render object.");
        _byData[data] = wrapper;
    }

    public void RemoveIdentity(GenericRenderObject data, VkObjectBase wrapper)
    {
        if (_byData.TryGetValue(data, out VkObjectBase? current) && ReferenceEquals(current, wrapper))
            _byData.TryRemove(data, out _);
    }

    public void Remove<T>(uint bindingId)
        where T : GenericRenderObject
    {
        GetBucket<T>().Remove(bindingId);
        BindingAllocator.Release<T>(bindingId);
    }

    public void Remove(GenericRenderObject data)
        => _byData.TryRemove(data, out _);

    public VkObject<T>[] Snapshot<T>()
        where T : GenericRenderObject
        => GetBucket<T>().Snapshot();

    /// <summary>Best-effort destruction of every cached wrapper during logical-device teardown.</summary>
    internal void DestroyDanglingWrappers()
    {
        // Identity-only wrappers have not generated a typed binding ID yet, so
        // typed bucket snapshots cannot see them. The data registry covers both
        // generated and ungenerated identities for this exact backend generation.
        VkObjectBase[] wrappers = [.. _byData.Values];
        foreach (VkObjectBase wrapper in wrappers)
            try { wrapper.Retire(); }
            catch (Exception ex)
            {
                Debug.VulkanWarning("[Vulkan] Failed to destroy cached wrapper '{0}'. {1}",
                    wrapper.GetType().Name, ex.Message);
            }
    }

    private VulkanBackendObjectBucket<T> GetBucket<T>()
        where T : GenericRenderObject
    {
        lock (_bucketsLock)
        {
            Type dataType = typeof(T);
            if (_buckets.TryGetValue(dataType, out IVulkanBackendObjectBucket? existing))
                return (VulkanBackendObjectBucket<T>)existing;

            VulkanBackendObjectBucket<T> bucket = new();
            _buckets.Add(dataType, bucket);
            return bucket;
        }
    }
}
