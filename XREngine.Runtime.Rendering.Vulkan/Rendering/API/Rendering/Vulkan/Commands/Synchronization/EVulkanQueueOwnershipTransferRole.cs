namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Identifies which half of a queue-family ownership transfer a submission records.
/// </summary>
internal enum EVulkanQueueOwnershipTransferRole : byte
{
    Invalid,
    Release,
    Acquire,
}
