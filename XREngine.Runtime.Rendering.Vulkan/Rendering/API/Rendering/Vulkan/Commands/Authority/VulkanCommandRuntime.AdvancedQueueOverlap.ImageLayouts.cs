using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Carries predicted entry state through a freshly recorded execution chain.
    /// Entries are not exits: the unordered classification child must not publish
    /// its sibling's untouched images as though it had written them.
    /// </summary>
    private void SeedFreshExecutionImageLayoutState(CommandBuffer successor, CommandBuffer predecessor)
    {
        SeedRecordedImageLayoutState(successor, predecessor);
        _ = EnterImageLayoutLockMeasured();
        try
        {
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(unchecked((ulong)predecessor.Handle), out var source) ||
                !Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(unchecked((ulong)successor.Handle), out var destination))
                throw new VulkanPlanPreconditionException("An Advanced execution segment has no sealed image-state predecessor.");
            // Preserve inherited entries where the immediate predecessor did not
            // touch a resource. Its actual exits, already copied above, win.
            foreach (var pair in source.EntrySubresources)
                destination.EntrySubresources.TryAdd(pair.Key, pair.Value);
            destination.UseExecutionPredecessorEntryState = true;
        }
        finally
        {
            Monitor.Exit(Synchronization._vulkanImageLayoutLock);
        }
    }
}
