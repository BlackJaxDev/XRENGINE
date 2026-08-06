using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Holds the command-buffer-local image state overlay recorded before the
/// command buffer is submitted and published to global synchronization state.
/// </summary>
internal sealed class VulkanRecordedImageLayoutState
{
    /// <summary>Immutable image states expected when recording began.</summary>
    public readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> EntrySubresources = new(8);

    /// <summary>Descriptor image states a primary must establish for a secondary.</summary>
    public readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> SecondaryDescriptorRequirements = new(8);

    /// <summary>
    /// Exact descriptor payload publications from which
    /// <see cref="SecondaryDescriptorRequirements"/> was captured. A secondary
    /// may skip the logical descriptor scan only while every referenced native
    /// descriptor set still publishes this generation.
    /// </summary>
    public readonly Dictionary<ulong, ulong> SecondaryDescriptorPayloadGenerations = new(4);

    /// <summary>The newest state recorded for each touched subresource.</summary>
    public readonly Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> Subresources = new(32);

    /// <summary>Compact publication list rebuilt from <see cref="Subresources"/>.</summary>
    public readonly List<KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState>> TouchedSubresources = new(32);

    /// <summary>Explicit cross-queue ownership transfers recorded by the command buffer.</summary>
    public readonly List<VulkanQueueOwnershipTransferRequirement> QueueOwnershipTransfers = new(4);

    /// <summary>Whether the entry contract could not be captured completely.</summary>
    public bool EntryStateIncomplete;

    /// <summary>The first actionable failure that made the entry contract incomplete.</summary>
    public VulkanImageEntryStateMismatch EntryStateFailure;

    /// <summary>The command-buffer recording generation that owns this journal.</summary>
    public ulong RecordingGeneration;

    /// <summary>
    /// Rebuilds the compact touched-subresource list from the current overlay
    /// so submission publication avoids re-discovering dictionary entries.
    /// </summary>
    public void RefreshTouchedSubresources()
    {
        TouchedSubresources.Clear();
        TouchedSubresources.EnsureCapacity(Subresources.Count);
        foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> subresource in Subresources)
            TouchedSubresources.Add(subresource);
    }
}
