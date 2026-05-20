namespace XREngine.Rendering.Compute;

/// <summary>
/// Interface for objects that can provide BVH data for GPU culling.
/// </summary>
public interface IGpuBvhProvider
{
    /// <summary>Gets the BVH node buffer for GPU traversal.</summary>
    XRDataBuffer? BvhNodeBuffer { get; }

    /// <summary>Gets the primitive range buffer for leaf node lookups.</summary>
    XRDataBuffer? BvhRangeBuffer { get; }

    /// <summary>Gets the Morton code buffer with object IDs.</summary>
    XRDataBuffer? BvhMortonBuffer { get; }

    /// <summary>Gets the number of nodes in the BVH.</summary>
    uint BvhNodeCount { get; }

    /// <summary>Whether the BVH is ready for use.</summary>
    bool IsBvhReady { get; }
}
