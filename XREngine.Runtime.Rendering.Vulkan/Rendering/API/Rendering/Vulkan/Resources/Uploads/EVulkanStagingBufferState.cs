namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Tracks whether a pooled staging allocation may be reserved, is owned by a
/// caller, or is waiting for its resource-lifetime generation to be republished.
/// </summary>
internal enum EVulkanStagingBufferState : byte
{
    Idle,
    InUse,
    Retiring,
}
