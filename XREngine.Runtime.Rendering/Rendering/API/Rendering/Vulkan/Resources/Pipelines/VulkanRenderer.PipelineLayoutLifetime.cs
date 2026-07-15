using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private readonly record struct RetiredPipelineLayout(
        PipelineLayout PipelineLayout,
        VulkanRetirementTicket Ticket,
        string Owner);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, string> _livePipelineLayoutHandles = new();
    private readonly List<RetiredPipelineLayout>[] _retiredPipelineLayouts = [new(), new()];
    private readonly HashSet<ulong>[] _retiredPipelineLayoutHandles = [new(), new()];
    private readonly HashSet<ulong> _retiredPipelineLayoutHandlesAll = new();

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
            lock (_retiredResourceLock)
            {
                if (_retiredPipelineLayoutHandlesAll.Contains(pipelineLayout.Handle))
                {
                    _livePipelineLayoutHandles[pipelineLayout.Handle] = trackedOwner ?? owner;
                    return false;
                }
            }

            if (!IsVulkanRetirementReady(ticket))
            {
                _livePipelineLayoutHandles[pipelineLayout.Handle] = trackedOwner ?? owner;
                int frameSlot = currentFrame;
                lock (_retiredResourceLock)
                {
                    if (_retiredPipelineLayoutHandlesAll.Add(pipelineLayout.Handle))
                    {
                        _retiredPipelineLayoutHandles[frameSlot].Add(pipelineLayout.Handle);
                        _retiredPipelineLayouts[frameSlot].Add(new RetiredPipelineLayout(
                            pipelineLayout,
                            ticket,
                            trackedOwner ?? owner));
                    }
                }
                Debug.VulkanEvery(
                    $"Vulkan.PipelineLayout.RetirementQueued.{GetHashCode()}.{pipelineLayout.Handle}",
                    TimeSpan.FromSeconds(1),
                    "[Vulkan.ResourceLifetime] Pipeline-layout destruction queued for exact-ticket retirement: handle=0x{0:X} owner={1} graphics={2} transfer={3} other={4}.",
                    pipelineLayout.Handle,
                    owner,
                    ticket.GraphicsSequence,
                    ticket.TransferSequence,
                    ticket.OtherSequence);
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

    private void DrainRetiredPipelineLayouts(int maxItems = RetiredPipelineDrainLimitPerFrame)
        => DrainRetiredPipelineLayouts(currentFrame, maxItems);

    private void DrainRetiredPipelineLayouts(int frameSlot, int maxItems)
    {
        if (Api is null || device.Handle == 0)
            return;

        RetiredPipelineLayout[] retired;
        int remaining;
        lock (_retiredResourceLock)
        {
            List<RetiredPipelineLayout> list = _retiredPipelineLayouts[frameSlot];
            int capacity = GetRetiredResourceDrainCount(list.Count, maxItems);
            if (capacity == 0)
                return;

            List<RetiredPipelineLayout> ready = new(capacity);
            for (int i = 0; i < list.Count && ready.Count < capacity;)
            {
                RetiredPipelineLayout candidate = list[i];
                if (!IsVulkanRetirementReady(candidate.Ticket))
                {
                    i++;
                    continue;
                }

                ready.Add(candidate);
                list.RemoveAt(i);
                _retiredPipelineLayoutHandles[frameSlot].Remove(candidate.PipelineLayout.Handle);
                _retiredPipelineLayoutHandlesAll.Remove(candidate.PipelineLayout.Handle);
                _livePipelineLayoutHandles.TryRemove(candidate.PipelineLayout.Handle, out _);
            }

            retired = [.. ready];
            remaining = list.Count;
        }

        ReportRetiredResourceBacklog("pipeline layouts", frameSlot, remaining);
        for (int i = 0; i < retired.Length; i++)
        {
            RetiredPipelineLayout entry = retired[i];
            if (entry.PipelineLayout.Handle == 0)
                continue;

            Api.DestroyPipelineLayout(device, entry.PipelineLayout, null);
            CompleteVulkanResourceDestruction(
                ObjectType.PipelineLayout,
                entry.PipelineLayout.Handle);
        }
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
