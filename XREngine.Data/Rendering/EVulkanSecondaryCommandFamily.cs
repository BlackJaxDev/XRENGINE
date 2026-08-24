namespace XREngine;

/// <summary>
/// Non-graphics Vulkan operation families admitted to secondary command buffers.
/// </summary>
public enum EVulkanSecondaryCommandFamily : byte
{
    Compute = 0,
    /// <summary>Explicit fixed memory barriers; graph/layout transfers stay primary-owned.</summary>
    Synchronization,
    Transfer,
    Query,
    Count,
}
