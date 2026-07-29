namespace XREngine.Rendering.Vulkan;

internal sealed class VulkanResourcePlan
{
    public static VulkanResourcePlan Empty { get; } = new(
        Array.Empty<VulkanAllocationRequest>(),
        Array.Empty<VulkanAllocationRequest>(),
        Array.Empty<VulkanAllocationRequest>(),
        Array.Empty<VulkanBufferAllocationRequest>(),
        Array.Empty<VulkanBufferAllocationRequest>(),
        Array.Empty<VulkanBufferAllocationRequest>(),
        new Dictionary<string, VulkanFrameBufferPlan>(StringComparer.OrdinalIgnoreCase));

    internal VulkanResourcePlan(
        IReadOnlyList<VulkanAllocationRequest> persistent,
        IReadOnlyList<VulkanAllocationRequest> transient,
        IReadOnlyList<VulkanAllocationRequest> external,
        IReadOnlyList<VulkanBufferAllocationRequest> persistentBuffers,
        IReadOnlyList<VulkanBufferAllocationRequest> transientBuffers,
        IReadOnlyList<VulkanBufferAllocationRequest> externalBuffers,
        IReadOnlyDictionary<string, VulkanFrameBufferPlan> frameBuffers)
    {
        PersistentTextures = persistent;
        TransientTextures = transient;
        ExternalTextures = external;
        PersistentBuffers = persistentBuffers;
        TransientBuffers = transientBuffers;
        ExternalBuffers = externalBuffers;
        FrameBuffers = frameBuffers;
    }

    public IReadOnlyList<VulkanAllocationRequest> PersistentTextures { get; }
    public IReadOnlyList<VulkanAllocationRequest> TransientTextures { get; }
    public IReadOnlyList<VulkanAllocationRequest> ExternalTextures { get; }
    public IReadOnlyList<VulkanBufferAllocationRequest> PersistentBuffers { get; }
    public IReadOnlyList<VulkanBufferAllocationRequest> TransientBuffers { get; }
    public IReadOnlyList<VulkanBufferAllocationRequest> ExternalBuffers { get; }
    public IReadOnlyDictionary<string, VulkanFrameBufferPlan> FrameBuffers { get; }

    public IEnumerable<VulkanAllocationRequest> AllTextures()
    {
        foreach (VulkanAllocationRequest request in PersistentTextures)
            yield return request;
        foreach (VulkanAllocationRequest request in TransientTextures)
            yield return request;
        foreach (VulkanAllocationRequest request in ExternalTextures)
            yield return request;
    }

    public IEnumerable<VulkanBufferAllocationRequest> AllBuffers()
    {
        foreach (VulkanBufferAllocationRequest request in PersistentBuffers)
            yield return request;
        foreach (VulkanBufferAllocationRequest request in TransientBuffers)
            yield return request;
        foreach (VulkanBufferAllocationRequest request in ExternalBuffers)
            yield return request;
    }
}
