using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Binds producer markers to the joined graphics timeline. Binary-only fork
    /// submissions cannot publish the timeline completion their consumers require.
    /// </summary>
    private void ConsolidateAdvancedQueueOverlapSubmissionMarkers(VulkanAdvancedQueueOverlapSlot slot)
    {
        lock (Synchronization._submissionMarkerLock)
        {
            MoveAdvancedQueueOverlapMarkersNoLock(slot.Prefix, slot.Final);
            MoveAdvancedQueueOverlapMarkersNoLock(slot.Classification, slot.Final);
            MoveAdvancedQueueOverlapMarkersNoLock(slot.Independent, slot.Final);
        }
    }

    private void MoveAdvancedQueueOverlapMarkersNoLock(CommandBuffer source, CommandBuffer destination)
    {
        var markers = Synchronization._submissionMarkersByCommandBuffer;
        if (!markers.Remove(source.Handle, out List<VulkanTimelineGpuFence>? sourceMarkers))
            return;
        if (!markers.TryGetValue(destination.Handle, out List<VulkanTimelineGpuFence>? destinationMarkers))
        {
            markers.Add(destination.Handle, sourceMarkers);
            return;
        }
        destinationMarkers.AddRange(sourceMarkers);
        sourceMarkers.Clear();
    }

    /// <summary>Removes tentative fork references before the frame-plan owner settles pooled fences.</summary>
    private void SettleFailedAdvancedQueueOverlapMarkers(CommandBuffer final, bool callerOwnsMarkers)
    {
        if (_advancedQueueOverlapSlots is not { } slots)
            return;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] is not { } slot || slot.Final.Handle != final.Handle)
                continue;
            slot.Recorded = false;
            if (callerOwnsMarkers)
            {
                DiscardSubmissionMarkersForCommandBuffer(slot.Prefix);
                DiscardSubmissionMarkersForCommandBuffer(slot.Classification);
                DiscardSubmissionMarkersForCommandBuffer(slot.Independent);
            }
            else
            {
                FailSubmissionMarkersForCommandBuffer(slot.Prefix);
                FailSubmissionMarkersForCommandBuffer(slot.Classification);
                FailSubmissionMarkersForCommandBuffer(slot.Independent);
            }
            return;
        }
    }
}
