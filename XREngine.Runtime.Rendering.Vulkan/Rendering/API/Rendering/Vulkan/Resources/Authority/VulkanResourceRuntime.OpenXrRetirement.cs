using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    /// <summary>Logs bounded child provenance only after the parent retirement diagnostic is rate-admitted.</summary>
    internal void LogUndestroyedOpenXrChildren(long retirementGeneration,
        ReadOnlySpan<VulkanPinnedResourceGeneration> generations)
    {
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            int logged = 0;
            for (int i = 0; i < generations.Length && logged < 6; i++)
            {
                VulkanPinnedResourceGeneration generation = generations[i];
                if (!tracker.TryResolveResourceGenerationNoLock(generation.Key, generation.Generation,
                        out VulkanResourceLifetimeRecord resource) ||
                    (resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)
                    continue;
                logged++;
                VulkanResourceGenerationPins pins = resource.Pins;
                Debug.VulkanWarning("[OpenXR.Retirement.Child] parent={0} resource={1} generation={2} owner={3} state={4} ticketReady={5} pins=(descriptor={6},template={7},recorded={8},queued={9}) sequences={10}/{11}/{12} completed={13}/{14}/{15}.",
                    retirementGeneration, generation.Key, generation.Generation, resource.Owner, resource.State,
                    tracker.IsRetirementReadyNoLock(resource.RetirementTicket),
                    pins.DescriptorReferenceCount, pins.TemplateReferenceCount, pins.RecordedReferenceCount, pins.QueuedReferenceCount,
                    pins.LastGraphicsSequence, pins.LastTransferSequence, pins.LastOtherSequence,
                    tracker.CompletedGraphicsSequence, tracker.CompletedTransferSequence, tracker.CompletedOtherSequence);
            }
        }
        lock (Lifetime.Retirement.SyncRoot)
        {
            for (int slot = 0; slot < Lifetime.Retirement.Images.Length; slot++)
            {
                List<RetiredImageResourceEntry> queue = Lifetime.Retirement.Images[slot];
                for (int i = 0; i < queue.Count; i++)
                {
                    RetiredImageResourceEntry entry = queue[i];
                    bool matches = false;
                    for (int child = 0; child < generations.Length && !matches; child++)
                        matches = generations[child].Key.Type == ObjectType.ImageView &&
                            generations[child].Key.Handle == entry.Resources.PrimaryView.Handle &&
                            generations[child].Generation == entry.PrimaryViewGeneration;
                    if (!matches)
                        continue;
                    Debug.VulkanWarning("[OpenXR.Retirement.Queue] parent={0} slot={1} view=0x{2:X} ticketReady={3} dependency={4} scanAvailable={5} queueCount={6}.",
                        retirementGeneration, slot, entry.Resources.PrimaryView.Handle,
                        Lifetime.Tracker.IsRetirementReady(entry.Ticket),
                        HasUndestroyedImageDependency(entry.Resources),
                        RetirementMeter.ReserveScanLimit(EVulkanRetirementWorkClass.Image, queue, queue.Count), queue.Count);
                }
            }
        }
        VulkanRetirementMeterSnapshot meter = GetRetirementMeterSnapshot();
        Debug.VulkanWarning("[OpenXR.Retirement.Budget] parent={0} frame={1} images=(backlog={2},admitted={3},completed={4},cap={5}) views=(backlog={6},admitted={7},completed={8},cap={9}).",
            retirementGeneration, meter.FrameSerial,
            meter.GetBacklog(EVulkanRetirementWorkClass.Image), meter.GetAdmitted(EVulkanRetirementWorkClass.Image),
            meter.GetCompleted(EVulkanRetirementWorkClass.Image), meter.GetOrdinaryCap(EVulkanRetirementWorkClass.Image),
            meter.GetBacklog(EVulkanRetirementWorkClass.ImageView), meter.GetAdmitted(EVulkanRetirementWorkClass.ImageView),
            meter.GetCompleted(EVulkanRetirementWorkClass.ImageView), meter.GetOrdinaryCap(EVulkanRetirementWorkClass.ImageView));
    }

    /// <summary>
    /// Tests exact generations rather than handles so a reused native handle
    /// cannot make a retired OpenXR parent appear safe to destroy.
    /// </summary>
    internal bool AreResourceGenerationsDestroyed(
        ReadOnlySpan<VulkanPinnedResourceGeneration> generations)
    {
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            for (int i = 0; i < generations.Length; i++)
            {
                VulkanPinnedResourceGeneration generation = generations[i];
                if (!tracker.TryResolveResourceGenerationNoLock(
                        generation.Key, generation.Generation, out VulkanResourceLifetimeRecord resource))
                    continue;

                if ((resource.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                    return false;
            }
        }

        return true;
    }

    internal bool AreDetachedExternalResourceSlotsReady(
        ReadOnlySpan<VulkanResourceSlotHandle> slots)
    {
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        lock (tracker.SyncRoot)
            for (int i = 0; i < slots.Length; i++)
                if (!tracker.IsDetachedResourceSlotRetirementReadyNoLock(slots[i]))
                    return false;

        return true;
    }
}
