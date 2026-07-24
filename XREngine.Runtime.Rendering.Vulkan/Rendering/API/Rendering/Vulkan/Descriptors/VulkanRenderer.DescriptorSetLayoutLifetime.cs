using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    /// <summary>
    /// Tracks the lifetime of Vulkan descriptor set layouts, ensuring proper destruction and avoiding stale resource usage.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, string> _liveDescriptorSetLayoutHandles = new();

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

        _liveDescriptorSetLayoutHandles[descriptorSetLayout.Handle] = owner;
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

        if (!_liveDescriptorSetLayoutHandles.TryRemove(descriptorSetLayout.Handle, out _))
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
        if (!IsVulkanRetirementReady(ticket))
        {
            _liveDescriptorSetLayoutHandles[descriptorSetLayout.Handle] = owner;
            Debug.VulkanWarning(
                "[Vulkan.ResourceLifetime] Descriptor-set-layout destruction was requested before its completion point: handle=0x{0:X} owner={1}.",
                descriptorSetLayout.Handle,
                owner);
            return false;
        }

        CompleteVulkanResourceDestruction(
            ObjectType.DescriptorSetLayout,
            descriptorSetLayout.Handle);
        return true;
    }

    /// <summary>
    /// Destroys all remaining tracked Vulkan descriptor set layouts, ensuring proper cleanup during shutdown.
    /// </summary>
    private void DestroyRemainingTrackedDescriptorSetLayouts()
    {
        foreach ((ulong handle, string owner) in _liveDescriptorSetLayoutHandles.ToArray())
        {
            DescriptorSetLayout layout = new() { Handle = handle };
            if (!TryBeginDestroyDescriptorSetLayout(layout, $"Shutdown:{owner}"))
                continue;

            Api!.DestroyDescriptorSetLayout(device, layout, null);
        }
    }
}
