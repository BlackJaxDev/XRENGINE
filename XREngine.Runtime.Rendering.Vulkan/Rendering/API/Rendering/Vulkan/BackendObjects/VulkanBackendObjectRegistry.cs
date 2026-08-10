namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns Vulkan wrapper binding identities for one renderer and logical-device lifetime.
/// </summary>
internal sealed class VulkanBackendObjectRegistry
{
    private readonly Lock _bucketsLock = new();
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
        => _byData.TryGetValue(data, out VkObjectBase? wrapper) ? wrapper : null;

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
        DestroyCachedWrappers(Snapshot<XRMaterial>(), "material");
        DestroyCachedWrappers(Snapshot<XRMeshRenderer.BaseVersion>(), "mesh renderer");
        DestroyCachedWrappers(Snapshot<XRRenderProgramPipeline>(), "render program pipeline");
        DestroyCachedWrappers(Snapshot<XRRenderProgram>(), "render program");
        DestroyCachedWrappers(Snapshot<XRDataBuffer>(), "data buffer");
        DestroyCachedWrappers(Snapshot<XRFrameBuffer>(), "framebuffer");
        DestroyCachedWrappers(Snapshot<XRRenderBuffer>(), "renderbuffer");
        DestroyCachedWrappers(Snapshot<XRTexture1D>(), "texture1D");
        DestroyCachedWrappers(Snapshot<XRTexture1DArray>(), "texture1DArray");
        DestroyCachedWrappers(Snapshot<XRTexture2D>(), "texture2D");
        DestroyCachedWrappers(Snapshot<XRTexture2DArray>(), "texture2DArray");
        DestroyCachedWrappers(Snapshot<XRTexture3D>(), "texture3D");
        DestroyCachedWrappers(Snapshot<XRTextureCube>(), "textureCube");
        DestroyCachedWrappers(Snapshot<XRTextureCubeArray>(), "textureCubeArray");
        DestroyCachedWrappers(Snapshot<XRTextureRectangle>(), "textureRectangle");
        DestroyCachedWrappers(Snapshot<XRTextureBuffer>(), "textureBuffer");
        DestroyCachedWrappers(Snapshot<XRTextureViewBase>(), "textureView");
        DestroyCachedWrappers(Snapshot<XRSampler>(), "sampler");
    }

    private static void DestroyCachedWrappers<T>(VkObject<T>[] wrappers, string label)
        where T : GenericRenderObject
    {
        foreach (VkObject<T>? wrapper in wrappers)
            try { wrapper?.Destroy(); }
            catch (Exception ex)
            {
                Debug.VulkanWarning("[Vulkan] Failed to destroy cached {0} wrapper '{1}'. {2}",
                    label, wrapper?.GetType().Name ?? "<null>", ex.Message);
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
