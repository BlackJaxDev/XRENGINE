using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed unsafe partial class VulkanResourceRuntime
{
    /// <summary>Registers a descriptor-set layout with this device generation.</summary>
    internal void RegisterDescriptorSetLayout(DescriptorSetLayout layout, string owner)
    {
        if (layout.Handle == 0)
            return;

        Descriptors.LiveDescriptorSetLayoutHandles[layout.Handle] = owner;
        Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.DescriptorSetLayout, layout.Handle),
            owner,
            externallyOwned: false);
    }

    /// <summary>
    /// Destroys a descriptor-set layout once the last command-buffer dependency
    /// has completed, or queues it on the supplied frame slot otherwise.
    /// </summary>
    internal void DestroyDescriptorSetLayout(
        Vk api,
        Device device,
        int frameSlot,
        DescriptorSetLayout layout,
        string owner)
    {
        if (layout.Handle == 0 ||
            !Descriptors.LiveDescriptorSetLayoutHandles.TryRemove(layout.Handle, out _))
        {
            return;
        }

        VulkanResourceLifetimeKey key = new(ObjectType.DescriptorSetLayout, layout.Handle);
        VulkanRetirementTicket ticket = CaptureRetirementTicket(key, owner);
        lock (Lifetime.Retirement.SyncRoot)
        {
            if (Lifetime.Retirement.AllDescriptorSetLayoutHandles.Contains(layout.Handle))
            {
                Descriptors.LiveDescriptorSetLayoutHandles[layout.Handle] = owner;
                return;
            }
        }

        if (!Lifetime.Tracker.IsRetirementReady(ticket))
        {
            Descriptors.LiveDescriptorSetLayoutHandles[layout.Handle] = owner;
            lock (Lifetime.Retirement.SyncRoot)
            {
                VulkanResourceRetirementQueue.TryEnqueueUniqueNoLock(
                    frameSlot,
                    layout.Handle,
                    new VulkanRetiredDescriptorSetLayout(layout, ticket, owner),
                    Lifetime.Retirement.DescriptorSetLayouts,
                    Lifetime.Retirement.DescriptorSetLayoutHandles,
                    Lifetime.Retirement.AllDescriptorSetLayoutHandles);
            }
            return;
        }

        api.DestroyDescriptorSetLayout(device, layout, null);
        CompleteSimpleResourceDestruction(ObjectType.DescriptorSetLayout, layout.Handle);
    }

    /// <summary>Releases every remaining descriptor-set layout at device teardown.</summary>
    internal void DestroyRemainingDescriptorSetLayouts(
        Vk api,
        Device device,
        int frameSlot)
    {
        foreach ((ulong handle, string owner) in Descriptors.LiveDescriptorSetLayoutHandles.ToArray())
        {
            DestroyDescriptorSetLayout(
                api,
                device,
                frameSlot,
                new DescriptorSetLayout { Handle = handle },
                $"Shutdown:{owner}");
        }
    }
}
