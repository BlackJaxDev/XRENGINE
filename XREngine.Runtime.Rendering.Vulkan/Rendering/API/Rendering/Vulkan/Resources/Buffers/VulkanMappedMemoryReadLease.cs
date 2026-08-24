namespace XREngine.Rendering.Vulkan;

/// <summary>Read-only name for a scoped mapped-memory lease.</summary>
internal unsafe ref struct VulkanMappedMemoryReadLease
{
    private VulkanMappedMemoryLease _lease;
    internal VulkanMappedMemoryReadLease(VulkanMappedMemoryLease lease) => _lease = lease;
    public readonly ReadOnlySpan<byte> Bytes => _lease.Bytes;
    public void Dispose() => _lease.Dispose();
}
