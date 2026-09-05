namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
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
