using System.Diagnostics;
using Silk.NET.Vulkan;
using XREngine.Rendering.DLSS;
using Image = Silk.NET.Vulkan.Image;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int MaximumPendingSwapchainGenerations = 8;

    private bool TryPrepareSwapchainRetirementMarkers(
        out Fence graphicsMarkerFence,
        out Fence presentMarkerFence)
    {
        graphicsMarkerFence = default;
        presentMarkerFence = default;
        DrainRetiredSwapchainGenerations();

        if (_outputRuntime._retiredSwapchainGenerations.Count >= MaximumPendingSwapchainGenerations ||
            _outputRuntime._orphanedSwapchainMarkerFences.Count != 0)
        {
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanSwapchainRetirement(
                pending: _outputRuntime._retiredSwapchainGenerations.Count,
                deferred: 1);
            Debug.VulkanEvery(
                $"Vulkan.Swapchain.RetirementPressure.{GetHashCode()}",
                TimeSpan.FromMilliseconds(500),
                "[Vulkan] Deferring swapchain recreation while bounded retirement is under pressure. PendingGenerations={0}/{1} OrphanedMarkers={2}.",
                _outputRuntime._retiredSwapchainGenerations.Count,
                MaximumPendingSwapchainGenerations,
                _outputRuntime._orphanedSwapchainMarkerFences.Count);
            return false;
        }

        FenceCreateInfo fenceInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
        };
        if (Api!.CreateFence(_deviceContext.Device, ref fenceInfo, null, out graphicsMarkerFence) != Result.Success)
            return false;

        bool distinctPresentQueue = _deviceContext.PresentQueue.Handle != _deviceContext.GraphicsQueue.Handle;
        if (distinctPresentQueue &&
            Api.CreateFence(_deviceContext.Device, ref fenceInfo, null, out presentMarkerFence) != Result.Success)
        {
            Api.DestroyFence(_deviceContext.Device, graphicsMarkerFence, null);
            graphicsMarkerFence = default;
            return false;
        }

        if (!TrySubmitSwapchainRetirementMarker(_deviceContext.GraphicsQueue, graphicsMarkerFence, "SwapchainRetirement.Graphics"))
        {
            Api.DestroyFence(_deviceContext.Device, graphicsMarkerFence, null);
            if (presentMarkerFence.Handle != 0)
                Api.DestroyFence(_deviceContext.Device, presentMarkerFence, null);
            graphicsMarkerFence = default;
            presentMarkerFence = default;
            return false;
        }

        if (distinctPresentQueue &&
            !TrySubmitSwapchainRetirementMarker(_deviceContext.PresentQueue, presentMarkerFence, "SwapchainRetirement.Present"))
        {
            // The graphics marker is already submitted and must remain alive until it
            // signals. Keep it in the bounded orphan queue; no swapchain state has been
            // detached yet, so the recreate itself safely defers.
            _outputRuntime._orphanedSwapchainMarkerFences.Add(graphicsMarkerFence);
            Api.DestroyFence(_deviceContext.Device, presentMarkerFence, null);
            graphicsMarkerFence = default;
            presentMarkerFence = default;
            return false;
        }

        return true;
    }

    private bool TrySubmitSwapchainRetirementMarker(Queue queue, Fence fence, string owner)
    {
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
        };
        Result result = SubmitToQueueTracked(queue, ref submitInfo, fence, caller: owner);
        if (result == Result.Success)
        {
            SetDebugObjectName(ObjectType.Fence, fence.Handle, owner);
            return true;
        }

        Debug.VulkanWarning(
            "[Vulkan] Swapchain retirement marker submission failed. Owner={0} Result={1}.",
            owner,
            result);
        return false;
    }

    private void QueueRetiredSwapchainGeneration(RetiredSwapchainGeneration generation)
    {
        _outputRuntime._retiredSwapchainGenerations.Add(generation);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanSwapchainRetirement(
            queued: 1,
            pending: _outputRuntime._retiredSwapchainGenerations.Count);
        Debug.Vulkan(
            "[Vulkan] Queued swapchain generation retirement. Extent={0}x{1} Pending={2}/{3} Handle=0x{4:X}.",
            generation.Width,
            generation.Height,
            _outputRuntime._retiredSwapchainGenerations.Count,
            MaximumPendingSwapchainGenerations,
            generation.Swapchain.Handle);
    }

    private void DrainRetiredSwapchainGenerations(bool force = false)
    {
        if (force)
            BeginForcedVulkanRetirementDrain();
        try
        {
            DrainOrphanedSwapchainMarkerFences(force);
            int drained = 0;
            for (int index = _outputRuntime._retiredSwapchainGenerations.Count - 1; index >= 0; index--)
            {
                RetiredSwapchainGeneration generation = _outputRuntime._retiredSwapchainGenerations[index];
                if (!force &&
                    (!IsSwapchainMarkerComplete(generation.GraphicsMarkerFence) ||
                     !IsSwapchainMarkerComplete(generation.PresentMarkerFence)))
                {
                    continue;
                }

                PublishCompletedSwapchainMarker(generation.GraphicsMarkerFence, force);
                PublishCompletedSwapchainMarker(generation.PresentMarkerFence, force);
                DrainCompletedSwapchainDependencies();
                if (!force && HasLiveSwapchainGenerationDependencies(generation))
                    continue;

                DestroyRetiredSwapchainGeneration(generation, force);
                DestroySwapchainMarkerFence(generation.GraphicsMarkerFence);
                DestroySwapchainMarkerFence(generation.PresentMarkerFence);
                _outputRuntime._retiredSwapchainGenerations.RemoveAt(index);
                drained++;
            }

            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanSwapchainRetirement(
                drained: drained,
                pending: _outputRuntime._retiredSwapchainGenerations.Count);
        }
        finally
        {
            if (force)
                EndForcedVulkanRetirementDrain();
        }
    }

    private void DrainOrphanedSwapchainMarkerFences(bool force)
    {
        for (int index = _outputRuntime._orphanedSwapchainMarkerFences.Count - 1; index >= 0; index--)
        {
            Fence fence = _outputRuntime._orphanedSwapchainMarkerFences[index];
            if (!force && !IsSwapchainMarkerComplete(fence))
                continue;

            PublishCompletedSwapchainMarker(fence, force);
            DestroySwapchainMarkerFence(fence);
            _outputRuntime._orphanedSwapchainMarkerFences.RemoveAt(index);
        }
    }

    private bool IsSwapchainMarkerComplete(Fence fence)
    {
        if (fence.Handle == 0)
            return true;

        Result result = Api!.GetFenceStatus(_deviceContext.Device, fence);
        if (result == Result.Success)
            return true;
        if (result == Result.NotReady)
            return false;
        if (result == Result.ErrorDeviceLost)
        {
            MarkDeviceLost(
                "Swapchain retirement marker fence reported device loss",
                "vkGetFenceStatus.SwapchainRetirement",
                result);
            return false;
        }

        Debug.VulkanWarning(
            "[Vulkan] Swapchain retirement marker status query failed. Fence=0x{0:X} Result={1}.",
            fence.Handle,
            result);
        return false;
    }

    private void PublishCompletedSwapchainMarker(Fence fence, bool force)
    {
        if (fence.Handle == 0)
            return;

        if (!force)
            NotifyVulkanFenceCompleted(fence);
    }

    private void DestroySwapchainMarkerFence(Fence fence)
    {
        if (fence.Handle != 0)
            Api!.DestroyFence(_deviceContext.Device, fence, null);
    }

    private void DrainCompletedSwapchainDependencies()
    {
        for (int frameSlot = 0; frameSlot < MAX_FRAMES_IN_FLIGHT; frameSlot++)
        {
            DrainRetiredCommandBuffers(frameSlot, int.MaxValue);
            DrainRetiredDescriptorSets(frameSlot, int.MaxValue);
            DrainRetiredDescriptorPools(frameSlot, int.MaxValue);
            DrainRetiredFramebuffers(frameSlot, int.MaxValue);
        }

        for (int pass = 0; pass < MAX_FRAMES_IN_FLIGHT; pass++)
        {
            for (int frameSlot = 0; frameSlot < MAX_FRAMES_IN_FLIGHT; frameSlot++)
                DrainRetiredImages(frameSlot, int.MaxValue);
        }
    }

    private bool HasLiveSwapchainGenerationDependencies(RetiredSwapchainGeneration generation)
    {
        for (int i = 0; i < generation.ImageViews.Length; i++)
        {
            if (_liveImageViewHandles.ContainsKey(generation.ImageViews[i].Handle))
                return true;
        }

        lock (ResourceRuntime.Lifetime.Tracker.SyncRoot)
        {
            for (int i = 0; i < generation.Framebuffers.Length; i++)
            {
                ulong handle = generation.Framebuffers[i].Handle;
                if (handle == 0)
                    continue;
                if (ResourceRuntime.Lifetime.Tracker.ResourceLifetimes.TryGetValue(
                        ResourceKey(ObjectType.Framebuffer, handle),
                        out VulkanResourceLifetimeRecord? lifetime) &&
                    (lifetime.State & EVulkanResourceLifetimeState.Destroyed) == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void DestroyRetiredSwapchainGeneration(RetiredSwapchainGeneration generation, bool force)
    {
        DestroyRetiredSwapchainRenderPass(generation.ClearRenderPass, force);
        DestroyRetiredSwapchainRenderPass(generation.LoadRenderPass, force);

        for (int i = 0; i < generation.PresentBridgeSemaphores.Length; i++)
        {
            Semaphore semaphore = generation.PresentBridgeSemaphores[i];
            if (semaphore.Handle != 0)
                Api!.DestroySemaphore(_deviceContext.Device, semaphore, null);
        }

        if (generation.Swapchain.Handle != 0)
        {
            if (generation.StreamlineProxy &&
                !NvidiaDlssManager.Native.TryDestroyProxySwapchain(this, generation.Swapchain, out string failureReason))
            {
                Debug.RenderingError(
                    "NVIDIA DLSS frame generation failed to destroy retired proxy swapchain cleanly ({0}). Falling back to VK_KHR_swapchain destruction.",
                    failureReason);
                OutputRuntime.Desktop.SwapchainExtension!.DestroySwapchain(_deviceContext.Device, generation.Swapchain, null);
            }
            else if (!generation.StreamlineProxy)
            {
                OutputRuntime.Desktop.SwapchainExtension!.DestroySwapchain(_deviceContext.Device, generation.Swapchain, null);
            }
        }

        for (int i = 0; i < generation.Images.Length; i++)
        {
            Image image = generation.Images[i];
            if (image.Handle == 0)
                continue;

            ulong lifetimeGeneration = i < generation.ImageLifetimeGenerations.Length
                ? generation.ImageLifetimeGenerations[i]
                : 0;
            CompleteDetachedExternalVulkanResourceDestruction(
                ObjectType.Image,
                image.Handle,
                lifetimeGeneration,
                force);
        }

        Debug.Vulkan(
            "[Vulkan] Drained swapchain generation retirement. Extent={0}x{1} AgeMs={2:F1} Handle=0x{3:X}.",
            generation.Width,
            generation.Height,
            Stopwatch.GetElapsedTime(generation.EnqueuedTimestamp).TotalMilliseconds,
            generation.Swapchain.Handle);
    }

    private void DestroyRetiredSwapchainRenderPass(RenderPass renderPass, bool force)
    {
        if (renderPass.Handle == 0)
            return;

        UnregisterRenderPass(renderPass);
        Api!.DestroyRenderPass(_deviceContext.Device, renderPass, null);
        CompleteVulkanResourceDestruction(ObjectType.RenderPass, renderPass.Handle, force);
    }
}
