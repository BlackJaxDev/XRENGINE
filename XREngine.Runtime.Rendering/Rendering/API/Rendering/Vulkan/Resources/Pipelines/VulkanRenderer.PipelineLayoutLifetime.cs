using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> _livePipelineLayoutHandles = new();

    internal void TrackLivePipelineLayout(PipelineLayout pipelineLayout, string owner = "unknown")
    {
        if (pipelineLayout.Handle != 0)
        {
            _livePipelineLayoutHandles[pipelineLayout.Handle] = owner;
            RegisterVulkanResource(ObjectType.PipelineLayout, pipelineLayout.Handle, owner);
        }
    }

    internal bool TryBeginDestroyPipelineLayout(PipelineLayout pipelineLayout, string owner)
    {
        if (pipelineLayout.Handle == 0)
            return false;

        if (_livePipelineLayoutHandles.TryRemove(pipelineLayout.Handle, out string? trackedOwner))
        {
            VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                ObjectType.PipelineLayout,
                pipelineLayout.Handle,
                owner);
            if (!IsVulkanRetirementReady(ticket))
            {
                _livePipelineLayoutHandles[pipelineLayout.Handle] = trackedOwner ?? owner;
                Debug.VulkanWarning(
                    "[Vulkan.ResourceLifetime] Pipeline-layout destruction deferred until shutdown: handle=0x{0:X} owner={1}.",
                    pipelineLayout.Handle,
                    owner);
                return false;
            }

            CompleteVulkanResourceDestruction(ObjectType.PipelineLayout, pipelineLayout.Handle);
            return true;
        }

        Debug.VulkanEvery(
            $"Vulkan.PipelineLayout.SkipStaleDestroy.{GetHashCode()}.{owner}.{pipelineLayout.Handle}",
            TimeSpan.FromSeconds(5),
            "[Vulkan] Skipping stale destroy for pipeline layout 0x{0:X} in {1}; the handle is not live in renderer tracking.",
            pipelineLayout.Handle,
            owner);
        return false;
    }

    private void DestroyRemainingTrackedPipelineLayouts()
    {
        int destroyedLayouts = 0;
        foreach (KeyValuePair<ulong, string> pair in _livePipelineLayoutHandles.ToArray())
        {
            if (!_livePipelineLayoutHandles.TryRemove(pair.Key, out string? owner))
                continue;

            PipelineLayout pipelineLayout = new() { Handle = pair.Key };
            Debug.Vulkan(
                "[Vulkan] Destroying remaining tracked pipeline layout 0x{0:X} owner={1} during renderer shutdown.",
                pair.Key,
                owner);
            Api!.DestroyPipelineLayout(device, pipelineLayout, null);
            CompleteVulkanResourceDestruction(ObjectType.PipelineLayout, pair.Key);
            destroyedLayouts++;
        }

        if (destroyedLayouts > 0)
        {
            Debug.Vulkan(
                "[Vulkan] Destroyed {0} remaining tracked pipeline layout(s) during renderer shutdown.",
                destroyedLayouts);
        }
    }
}
