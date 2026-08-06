using Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Linq;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal readonly record struct RetiredPipelineLayout(
        PipelineLayout PipelineLayout,
        VulkanRetirementTicket Ticket,
        string Owner);

    internal void TrackLivePipelineLayout(PipelineLayout pipelineLayout, string owner = "unknown")
    {
        if (pipelineLayout.Handle != 0)
        {
            ResourceRuntime.Lifetime.LivePipelineLayoutHandles[pipelineLayout.Handle] = owner;
            RegisterVulkanResource(ObjectType.PipelineLayout, pipelineLayout.Handle, owner);
        }
    }

    internal bool TryBeginDestroyPipelineLayout(PipelineLayout pipelineLayout, string owner)
    {
        if (pipelineLayout.Handle == 0)
            return false;

        if (ResourceRuntime.Lifetime.LivePipelineLayoutHandles.TryRemove(pipelineLayout.Handle, out string? trackedOwner))
        {
            VulkanRetirementTicket ticket = CaptureVulkanRetirementTicket(
                ObjectType.PipelineLayout,
                pipelineLayout.Handle,
                owner);
            lock (ResourceRuntime.Lifetime.Retirement.SyncRoot)
            {
                if (ResourceRuntime.Lifetime.Retirement.AllPipelineLayoutHandles.Contains(pipelineLayout.Handle))
                {
                    ResourceRuntime.Lifetime.LivePipelineLayoutHandles[pipelineLayout.Handle] = trackedOwner ?? owner;
                    return false;
                }
            }

            if (!IsVulkanRetirementReady(ticket))
            {
                ResourceRuntime.Lifetime.LivePipelineLayoutHandles[pipelineLayout.Handle] = trackedOwner ?? owner;
                int frameSlot = CurrentDesktopFrameSlot;
                lock (ResourceRuntime.Lifetime.Retirement.SyncRoot)
                {
                    if (ResourceRuntime.Lifetime.Retirement.AllPipelineLayoutHandles.Add(pipelineLayout.Handle))
                    {
                        ResourceRuntime.Lifetime.Retirement.PipelineLayoutHandles[frameSlot].Add(pipelineLayout.Handle);
                        ResourceRuntime.Lifetime.Retirement.PipelineLayouts[frameSlot].Add(new RetiredPipelineLayout(
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
        => DrainRetiredPipelineLayouts(CurrentDesktopFrameSlot, maxItems);

    private void DrainRetiredPipelineLayouts(int frameSlot, int maxItems)
    {
        if (Api is null || _deviceContext.Device.Handle == 0)
            return;

        RetiredPipelineLayout[] retired;
        int remaining;
        lock (ResourceRuntime.Lifetime.Retirement.SyncRoot)
        {
            List<RetiredPipelineLayout> list = ResourceRuntime.Lifetime.Retirement.PipelineLayouts[frameSlot];
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
                ResourceRuntime.Lifetime.Retirement.PipelineLayoutHandles[frameSlot].Remove(candidate.PipelineLayout.Handle);
                ResourceRuntime.Lifetime.Retirement.AllPipelineLayoutHandles.Remove(candidate.PipelineLayout.Handle);
                ResourceRuntime.Lifetime.LivePipelineLayoutHandles.TryRemove(candidate.PipelineLayout.Handle, out _);
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

            Api.DestroyPipelineLayout(_deviceContext.Device, entry.PipelineLayout, null);
            CompleteVulkanResourceDestruction(
                ObjectType.PipelineLayout,
                entry.PipelineLayout.Handle);
        }
    }

    private void DestroyRemainingTrackedPipelineLayouts()
    {
        int destroyedLayouts = 0;
        foreach (KeyValuePair<ulong, string> pair in ResourceRuntime.Lifetime.LivePipelineLayoutHandles.ToArray())
        {
            if (!ResourceRuntime.Lifetime.LivePipelineLayoutHandles.TryRemove(pair.Key, out string? owner))
                continue;

            PipelineLayout pipelineLayout = new() { Handle = pair.Key };
            Debug.Vulkan(
                "[Vulkan] Destroying remaining tracked pipeline layout 0x{0:X} owner={1} during renderer shutdown.",
                pair.Key,
                owner);
            Api!.DestroyPipelineLayout(_deviceContext.Device, pipelineLayout, null);
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
