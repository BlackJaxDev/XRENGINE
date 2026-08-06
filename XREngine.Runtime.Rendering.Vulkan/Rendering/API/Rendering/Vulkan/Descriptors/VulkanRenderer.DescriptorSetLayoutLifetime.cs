using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct RetiredDescriptorSetLayout(
        DescriptorSetLayout DescriptorSetLayout,
        VulkanRetirementTicket Ticket,
        string Owner);

    /// <summary>
    /// Tracks a Vulkan descriptor set layout as live, associating it with an owner for proper resource management.
    /// </summary>
    /// <param name="descriptorSetLayout">The Vulkan descriptor set layout to track as live.</param>
    /// <param name="owner">The owner or context responsible for this descriptor set layout.</param>
    private void TrackLiveDescriptorSetLayout(
        DescriptorSetLayout descriptorSetLayout,
        string owner)
    {
        if (descriptorSetLayout.Handle == 0)
            return;

        ResourceRuntime.Descriptors.LiveDescriptorSetLayoutHandles[descriptorSetLayout.Handle] = owner;
        RegisterVulkanResource(
            ObjectType.DescriptorSetLayout,
            descriptorSetLayout.Handle,
            owner);
    }

    /// <summary>
    /// Attempts to begin the destruction of a Vulkan descriptor set layout, ensuring it is safe to do so and not stale.
    /// </summary>
    /// <param name="descriptorSetLayout">The Vulkan descriptor set layout to attempt to destroy.</param>
    /// <param name="owner">The owner or context responsible for this descriptor set layout.</param>
    /// <returns>True if the destruction process was successfully initiated; otherwise, false.</returns>
    private bool TryBeginDestroyDescriptorSetLayout(
        DescriptorSetLayout descriptorSetLayout,
        string owner)
    {
        if (descriptorSetLayout.Handle == 0)
            return false;

        if (!ResourceRuntime.Descriptors.LiveDescriptorSetLayoutHandles.TryRemove(descriptorSetLayout.Handle, out _))
        {
            Debug.VulkanEvery(
                $"Vulkan.DescriptorSetLayout.SkipStaleDestroy.{GetHashCode()}.{descriptorSetLayout.Handle}",
                TimeSpan.FromSeconds(5),
                "[Vulkan] Skipping stale descriptor-set-layout destroy: handle=0x{0:X} owner={1}.",
                descriptorSetLayout.Handle,
                owner);
            return false;
        }

        VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
            ObjectType.DescriptorSetLayout,
            descriptorSetLayout.Handle,
            owner);
        lock (ResourceRuntime.Lifetime.Retirement.SyncRoot)
        {
            if (ResourceRuntime.Lifetime.Retirement.AllDescriptorSetLayoutHandles.Contains(
                    descriptorSetLayout.Handle))
            {
                ResourceRuntime.Descriptors.LiveDescriptorSetLayoutHandles[descriptorSetLayout.Handle] = owner;
                return false;
            }
        }

        if (!IsVulkanRetirementReady(ticket))
        {
            ResourceRuntime.Descriptors.LiveDescriptorSetLayoutHandles[descriptorSetLayout.Handle] = owner;
            int frameSlot = CurrentDesktopFrameSlot;
            lock (ResourceRuntime.Lifetime.Retirement.SyncRoot)
            {
                VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                    frameSlot,
                    descriptorSetLayout.Handle,
                    new RetiredDescriptorSetLayout(descriptorSetLayout, ticket, owner),
                    ResourceRuntime.Lifetime.Retirement.DescriptorSetLayouts,
                    ResourceRuntime.Lifetime.Retirement.DescriptorSetLayoutHandles,
                    ResourceRuntime.Lifetime.Retirement.AllDescriptorSetLayoutHandles);
            }
            Debug.VulkanEvery(
                $"Vulkan.DescriptorSetLayout.RetirementQueued.{GetHashCode()}.{descriptorSetLayout.Handle}",
                TimeSpan.FromSeconds(1),
                "[Vulkan.ResourceLifetime] Descriptor-set-layout destruction queued for exact-ticket retirement: handle=0x{0:X} owner={1} graphics={2} transfer={3} other={4}.",
                descriptorSetLayout.Handle,
                owner,
                ticket.GraphicsSequence,
                ticket.TransferSequence,
                ticket.OtherSequence);
            return false;
        }

        CompleteVulkanResourceDestruction(
            ObjectType.DescriptorSetLayout,
            descriptorSetLayout.Handle);
        return true;
    }

    private void DrainRetiredDescriptorSetLayouts(int maxItems = RetiredPipelineDrainLimitPerFrame)
        => DrainRetiredDescriptorSetLayouts(CurrentDesktopFrameSlot, maxItems);

    private void DrainRetiredDescriptorSetLayouts(int frameSlot, int maxItems)
    {
        if (Api is null || _deviceContext.Device.Handle == 0)
            return;

        RetiredDescriptorSetLayout[] retired;
        int remaining;
        lock (ResourceRuntime.Lifetime.Retirement.SyncRoot)
        {
            List<RetiredDescriptorSetLayout> list =
                ResourceRuntime.Lifetime.Retirement.DescriptorSetLayouts[frameSlot];
            int capacity = GetRetiredResourceDrainCount(list.Count, maxItems);
            if (capacity == 0)
                return;

            List<RetiredDescriptorSetLayout> ready = new(capacity);
            for (int i = 0; i < list.Count && ready.Count < capacity;)
            {
                RetiredDescriptorSetLayout candidate = list[i];
                if (!IsVulkanRetirementReady(candidate.Ticket))
                {
                    i++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(i);
                VulkanResourceRetirementQueue.ReleaseUniqueNoLock(
                    frameSlot,
                    candidate.DescriptorSetLayout.Handle,
                    ResourceRuntime.Lifetime.Retirement.DescriptorSetLayoutHandles,
                    ResourceRuntime.Lifetime.Retirement.AllDescriptorSetLayoutHandles);
                ResourceRuntime.Descriptors.LiveDescriptorSetLayoutHandles.TryRemove(
                    candidate.DescriptorSetLayout.Handle,
                    out _);
            }

            retired = [.. ready];
            remaining = list.Count;
        }

        ReportRetiredResourceBacklog("descriptor set layouts", frameSlot, remaining);
        for (int i = 0; i < retired.Length; i++)
        {
            DescriptorSetLayout layout = retired[i].DescriptorSetLayout;
            if (layout.Handle == 0)
                continue;

            Api.DestroyDescriptorSetLayout(_deviceContext.Device, layout, null);
            CompleteVulkanResourceDestruction(ObjectType.DescriptorSetLayout, layout.Handle);
        }
    }

    /// <summary>
    /// Destroys all remaining tracked Vulkan descriptor set layouts, ensuring proper cleanup during shutdown.
    /// </summary>
    private void DestroyRemainingTrackedDescriptorSetLayouts()
    {
        foreach ((ulong handle, string owner) in ResourceRuntime.Descriptors.LiveDescriptorSetLayoutHandles.ToArray())
        {
            DescriptorSetLayout layout = new() { Handle = handle };
            if (!TryBeginDestroyDescriptorSetLayout(layout, $"Shutdown:{owner}"))
                continue;

            Api!.DestroyDescriptorSetLayout(_deviceContext.Device, layout, null);
        }
    }
}
