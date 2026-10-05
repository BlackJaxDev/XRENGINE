namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Recorded native identities one mesh-draw operation contributes to a
/// command-chain packet key. Captured once per packet-lowering pass so the
/// bounded identity capacity can be checked arithmetically for every start
/// position instead of re-reading renderer state under its lock.
/// </summary>
internal readonly struct MeshPacketIdentityDemand(
    int vertexBufferCount,
    int indexBufferCount,
    int descriptorSetCount)
{
    public int VertexBufferCount { get; } = vertexBufferCount;
    public int IndexBufferCount { get; } = indexBufferCount;
    public int DescriptorSetCount { get; } = descriptorSetCount;
}
