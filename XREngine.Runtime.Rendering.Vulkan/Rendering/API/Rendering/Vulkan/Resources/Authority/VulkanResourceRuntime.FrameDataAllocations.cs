using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    /// <summary>Includes exact recorded parents so native reset can release secondary readers.</summary>
    internal bool AppendRecordedCommandParents(VulkanMappedFrameAllocation allocation, List<ulong> handles, HashSet<ulong> visited)
    {
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (handles.Count == 0 || allocation.IsOpen ||
                !Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(handles[0], out VulkanCommandBufferLifetimeRecord? lifetime) ||
                !lifetime.FrameDataAllocations.Contains(allocation))
                return false;
            for (int index = 0; index < handles.Count; index++)
            {
                VulkanResourceLifetimeKey key = new(ObjectType.CommandBuffer, handles[index]);
                if (!Lifetime.Tracker.ResourceCommandBufferDependencies.TryGetValue(key, out HashSet<ulong>? parents))
                    continue;
                foreach (ulong parent in parents)
                    if (visited.Add(parent))
                        handles.Add(parent);
            }
            return true;
        }
    }

    /// <summary>Rejects refresh of addresses retained by a retired command allocation.</summary>
    internal bool AreCommandFrameDataAllocationsOpen(CommandBuffer commandBuffer)
    {
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(
                unchecked((ulong)commandBuffer.Handle), out VulkanCommandBufferLifetimeRecord? lifetime))
                return false;
            long retirementVersion = MappedFrameArena?.AllocationRetirementVersion ?? 0;
            if (lifetime.FrameDataRetirementVersion == retirementVersion)
                return lifetime.FrameDataAllocationsOpen;
            lifetime.FrameDataRetirementVersion = retirementVersion;
            lifetime.FrameDataAllocationsOpen = true;
            foreach (VulkanMappedFrameAllocation allocation in lifetime.FrameDataAllocations)
                if (!allocation.IsOpen)
                {
                    lifetime.FrameDataAllocationsOpen = false;
                    return false;
                }
            if (lifetime.Level == CommandBufferLevel.Primary)
            {
                foreach (VulkanResourceLifetimeKey dependency in lifetime.Dependencies.Keys)
                {
                    if (dependency.Type != ObjectType.CommandBuffer)
                        continue;
                    if (!Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(dependency.Handle, out VulkanCommandBufferLifetimeRecord? secondary))
                    {
                        lifetime.FrameDataAllocationsOpen = false;
                        return false;
                    }
                    foreach (VulkanMappedFrameAllocation allocation in secondary.FrameDataAllocations)
                        if (!allocation.IsOpen)
                        {
                            lifetime.FrameDataAllocationsOpen = false;
                            return false;
                        }
                }
            }
            return true;
        }
    }

    /// <summary>Retains the exact range before a command records its address or offset.</summary>
    internal bool TryRetainFrameDataAllocation(CommandBuffer commandBuffer, VulkanMappedFrameAllocation allocation)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return false;
        lock (Lifetime.Tracker.SyncRoot)
        {
            if (!Lifetime.Tracker.CommandBufferLifetimes.TryGetValue(handle, out VulkanCommandBufferLifetimeRecord? lifetime))
            {
                lifetime = new VulkanCommandBufferLifetimeRecord();
                Lifetime.Tracker.CommandBufferLifetimes.Add(handle, lifetime);
            }
            if (lifetime.QueuedSubmissionCount != 0 || !allocation.TryRetainCommand(handle))
                return false;
            lifetime.FrameDataAllocations.Add(allocation);
            lifetime.FrameDataRetirementVersion = -1;
            return true;
        }
    }
}
