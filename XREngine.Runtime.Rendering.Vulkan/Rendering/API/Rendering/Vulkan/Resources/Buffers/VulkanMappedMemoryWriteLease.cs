namespace XREngine.Rendering.Vulkan;

/// <summary>Write name for a scoped mapped-memory lease; disposal publishes non-coherent writes.</summary>
internal unsafe ref struct VulkanMappedMemoryWriteLease
{
    private VulkanMappedMemoryLease _lease;
    internal VulkanMappedMemoryWriteLease(VulkanMappedMemoryLease lease) => _lease = lease;
    public readonly Span<byte> Bytes => _lease.Bytes;
    public void Dispose() => _lease.Dispose();
}
