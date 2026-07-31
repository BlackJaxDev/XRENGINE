namespace XREngine;

/// <summary>
/// Non-graphics Vulkan operation families admitted to secondary command buffers.
/// </summary>
public enum EVulkanSecondaryCommandFamily : byte
{
    Compute = 0,
    Transfer,
    Query,
    Count,
}
