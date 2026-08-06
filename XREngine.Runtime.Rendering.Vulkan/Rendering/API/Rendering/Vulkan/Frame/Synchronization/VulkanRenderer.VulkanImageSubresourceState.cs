namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Stores submitted and completed synchronization state for a tracked image
/// subresource, including pending queue-family ownership transfer state.
/// </summary>
internal sealed class VulkanImageSubresourceState
{
    /// <summary>The newest state published by a successful submission.</summary>
    public VulkanImageAccessState Submitted = VulkanImageAccessState.Undefined;

    /// <summary>The newest state whose associated queue work has completed.</summary>
    public VulkanImageAccessState Completed = VulkanImageAccessState.Undefined;

    /// <summary>
    /// The release half of a cross-queue ownership transfer awaiting its
    /// matching acquire.
    /// </summary>
    public VulkanPendingQueueOwnershipRelease? PendingQueueOwnershipRelease;

    /// <summary>The last graphics-queue sequence that wrote this state.</summary>
    public ulong GraphicsSequence;

    /// <summary>The last transfer-queue sequence that wrote this state.</summary>
    public ulong TransferSequence;

    /// <summary>The last non-graphics/non-transfer sequence that wrote this state.</summary>
    public ulong OtherSequence;
}
