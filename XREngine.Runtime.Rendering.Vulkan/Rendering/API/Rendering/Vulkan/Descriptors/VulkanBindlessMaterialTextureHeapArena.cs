namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Append-only descriptor-heap ranges for the global material texture table.
/// A growth receives fresh ranges; old ranges remain immutable while the heap
/// storage itself remains live through device teardown.
/// </summary>
internal sealed class VulkanBindlessMaterialTextureHeapArena
{
    internal uint ResourceBaseIndex;
    internal uint SamplerBaseIndex;
    internal ulong ResourceOffset;
    internal ulong SamplerOffset;
    internal uint ResourceStride;
    internal uint SamplerStride;
    internal uint Capacity;
    internal ulong[] PublishedRewriteSerials = [];

    /// <summary>Abandons this table's ranges; their bytes survive until heap teardown.</summary>
    internal void Reset()
    {
        ResourceBaseIndex = 0;
        SamplerBaseIndex = 0;
        ResourceOffset = 0;
        SamplerOffset = 0;
        ResourceStride = 0;
        SamplerStride = 0;
        Capacity = 0;
        PublishedRewriteSerials = [];
    }
}
