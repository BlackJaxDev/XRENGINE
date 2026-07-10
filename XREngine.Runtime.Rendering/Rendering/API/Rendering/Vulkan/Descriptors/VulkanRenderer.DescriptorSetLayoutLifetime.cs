using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly ConcurrentDictionary<ulong, string> _liveDescriptorSetLayoutHandles = new();

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
