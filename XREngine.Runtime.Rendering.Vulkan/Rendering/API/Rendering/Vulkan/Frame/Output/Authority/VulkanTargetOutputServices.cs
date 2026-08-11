using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Concrete native services used by presentationless and headless output targets.
/// </summary>
internal sealed unsafe partial class VulkanFrameLoop : IVulkanTargetOutputHost
{
    public Vk VulkanApi => Api;
    public Instance Instance => _deviceContext.Instance;
    public PhysicalDevice PhysicalDevice => _deviceContext.PhysicalDevice;
    public Device Device => _deviceContext.Device;
    public Queue GraphicsQueue => _deviceContext.GraphicsQueue;
    public Queue PresentQueue => _deviceContext.PresentQueue;
    public SurfaceKHR TargetSurface => _outputRuntime.Surface;
    public uint GraphicsQueueFamilyIndex => _deviceContext.QueueFamilies.GraphicsFamilyIndex!.Value;
    public uint PresentQueueFamilyIndex => _deviceContext.QueueFamilies.PresentFamilyIndex!.Value;
    public bool StreamlineDlssProvisioned => _outputRuntime._streamlineDlssProvisioned;
    public bool StreamlineFrameGenerationProvisioned => _outputRuntime._streamlineFrameGenerationProvisioned;
    public VulkanStreamlineDeviceBinding CaptureStreamlineDeviceBinding()
        => _outputRuntime.CaptureStreamlineDeviceBinding(_deviceContext);
    public CommandBuffer[] CreateDesktopOutputArtifacts(int imageCount)
        => _commandRuntime.CreateDesktopOutputArtifacts(
            Api, _deviceContext, _resourceRuntime, imageCount);
    public int ReserveOpenXrFrameDataSlots(int desktopImageCount)
    {
        int desktopSlots = Math.Max(Math.Max(desktopImageCount, 2), 1);
        int totalSlots = checked(desktopSlots + _outputRuntime.OpenXrBackend.EyeFrameDataSlotCount);
        _resourceRuntime.Descriptors.EnsureFrameSlotCountFloor(totalSlots);
        _commandRuntime.EnsureFrameDataSlotCapacity(totalSlots);
        return totalSlots;
    }
    public void PublishDesktopImageTimelineValues(ulong[]? timelineValues)
        => _commandRuntime.Synchronization._desktopImageTimelineValues = timelineValues;
    public void PublishDesktopSwapchainExtent(Extent2D extent)
    {
        _commandRuntime.StateTracker.SetSwapchainExtent(extent);
        _commandRuntime.StateTracker.SetCurrentTargetExtent(extent);
        _framePlanner.PublishDesktopSwapchainExtent(extent);
    }
    public void RetireDesktopOutputArtifacts(CommandBuffer[]? commandBuffers)
        => _commandRuntime.RetireDesktopOutputArtifacts(
            Api,
            _deviceContext,
            _resourceRuntime,
            _resourceRuntime.FramebufferRetirementFrameSlot,
            commandBuffers);
    public void DrainRetiredDesktopCommandBuffers(int frameSlot)
        => _commandRuntime.DrainRetiredCommandBuffers(
            Api, _deviceContext.Device, _resourceRuntime, frameSlot, int.MaxValue);

    public KhrSurface RequireSurfaceApi()
        => _outputRuntime.SurfaceApi
            ?? throw new InvalidOperationException("Vulkan surface API is not initialized.");

    void IVulkanTargetOutputHost.ThrowIfVulkanDeviceOperationNotAdmitted(string operation)
    {
        if (!TryAdmitVulkanDeviceOperation(operation, out string failureReason))
            throw new InvalidOperationException(failureReason);
    }

    bool IVulkanTargetOutputHost.TryAdmitVulkanDeviceOperation(string operation, out string failureReason)
    {
        if (_deviceContext.IsOperational)
        {
            failureReason = string.Empty;
            return true;
        }

        failureReason = $"Cannot start Vulkan operation '{operation}' while device state is {_deviceContext.State}.";
        return false;
    }

    public void NotifyVulkanFenceCompleted(Fence fence)
    {
        if (fence.Handle == 0)
            return;

        ulong handle = unchecked((ulong)fence.Handle);
        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            for (int index = tracker.LifetimeSubmissions.Count - 1; index >= 0; index--)
            {
                VulkanLifetimeSubmission submission = tracker.LifetimeSubmissions[index];
                if (submission.FenceHandle != handle)
                    continue;

                tracker.MarkQueueSequenceCompletedNoLock(
                    submission.QueueDomain,
                    submission.QueueSequence);
                tracker.LifetimeSubmissions.RemoveAt(index);
            }
        }
    }

    public Result CreateVulkanCommandPoolTracked(ref CommandPoolCreateInfo createInfo, out CommandPool pool, string owner)
    {
        pool = default;
        ThrowIfVulkanDeviceOperationNotAdmitted(owner);
        lock (_commandRuntime.Pools.Gate)
        {
            Result result = Api.CreateCommandPool(Device, ref createInfo, null, out pool);
            ObserveNativeResult(owner, result);
            if (result == Result.Success)
                RegisterResource(ObjectType.CommandPool, pool.Handle, owner);
            return result;
        }
    }

    public Result AllocateVulkanCommandBufferTracked(ref CommandBufferAllocateInfo allocateInfo, out CommandBuffer commandBuffer, string owner)
    {
        commandBuffer = default;
        ThrowIfVulkanDeviceOperationNotAdmitted(owner);
        lock (_commandRuntime.Pools.Gate)
        {
            Result result = Api.AllocateCommandBuffers(Device, ref allocateInfo, out commandBuffer);
            ObserveNativeResult(owner, result);
            if (result != Result.Success || commandBuffer.Handle == 0)
                return result;

            ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
            RegisterResource(ObjectType.CommandBuffer, commandBufferHandle, owner);
            VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
            VulkanResourceLifetimeKey poolKey = ResourceKey(ObjectType.CommandPool, allocateInfo.CommandPool.Handle);
            lock (tracker.SyncRoot)
            {
                if (!tracker.CommandBuffersByPool.TryGetValue(poolKey, out HashSet<ulong>? children))
                {
                    children = [];
                    tracker.CommandBuffersByPool.Add(poolKey, children);
                }
                children.Add(commandBufferHandle);
            }
            return result;
        }
    }

    public Result ResetVulkanCommandPoolTracked(CommandPool pool, string owner)
    {
        if (pool.Handle == 0)
            return Result.Success;

        ThrowIfVulkanDeviceOperationNotAdmitted(owner);
        lock (_commandRuntime.Pools.Gate)
        {
            Result result = Api.ResetCommandPool(Device, pool, 0);
            ObserveNativeResult(owner, result);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResetCommandPoolCall();
            return result;
        }
    }

    public Result EndCommandBufferTracked(CommandBuffer commandBuffer)
        => _commandRuntime.EndCommandBufferTracked(commandBuffer);

    public void DestroyCommandPoolHostSynchronized(CommandPool pool)
    {
        if (pool.Handle == 0)
            return;

        lock (_commandRuntime.Pools.Gate)
        {
            Api.DestroyCommandPool(Device, pool, null);
            CompleteCommandPoolResources(pool);
        }
    }

    public Result CreateVulkanImageTracked(ref ImageCreateInfo createInfo, out Image image, string owner)
    {
        image = default;
        ThrowIfVulkanDeviceOperationNotAdmitted("vkCreateImage." + owner);
        Result result = Api.CreateImage(Device, ref createInfo, null, out image);
        ObserveNativeResult("vkCreateImage." + owner, result);
        if (result == Result.Success)
        {
            RegisterResource(ObjectType.Image, image.Handle, owner);
            _commandRuntime.RegisterTrackedImageInitialLayouts(image, in createInfo);
        }
        return result;
    }

    public void DestroyVulkanImageImmediateTracked(Image image, string owner)
    {
        if (image.Handle == 0 || !TryCompleteResource(ObjectType.Image, image.Handle, owner))
            return;

        Api.DestroyImage(Device, image, null);
        _resourceRuntime.Allocations.Images.Allocations.TryRemove(image.Handle, out _);
    }

    public VulkanMemoryAllocation AllocateImageMemoryWithFallback(Image image, MemoryPropertyFlags requiredProperties)
    {
        IVulkanMemoryAllocator allocator = RequireMemoryAllocator();
        if (allocator.TryAllocateForImage(
                Api,
                Device,
                image,
                requiredProperties,
                out VulkanMemoryAllocation allocation,
                out Result result))
        {
            return allocation;
        }

        ObserveNativeResult("vkAllocateMemory.TargetImage", result);
        if (requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit))
        {
            MemoryPropertyFlags fallback = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
            if (allocator.TryAllocateForImage(Api, Device, image, fallback, out allocation, out result))
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanOomFallback();
                return allocation;
            }
            ObserveNativeResult("vkAllocateMemory.TargetImageFallback", result);
        }

        throw new VulkanOutOfMemoryException(
            $"Vulkan target image allocation failed ({result}). Requested={requiredProperties}",
            requiredProperties);
    }

    public VulkanMemoryAllocation AllocateBufferMemoryWithFallback(Buffer buffer, MemoryPropertyFlags requiredProperties)
    {
        IVulkanMemoryAllocator allocator = RequireMemoryAllocator();
        if (allocator.TryAllocateForBuffer(
                Api,
                Device,
                buffer,
                requiredProperties,
                out VulkanMemoryAllocation allocation,
                out Result result))
        {
            return allocation;
        }

        ObserveNativeResult("vkAllocateMemory.TargetBuffer", result);
        if (requiredProperties.HasFlag(MemoryPropertyFlags.DeviceLocalBit))
        {
            MemoryPropertyFlags fallback = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
            if (allocator.TryAllocateForBuffer(Api, Device, buffer, fallback, out allocation, out result))
            {
                RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanOomFallback();
                return allocation;
            }
            ObserveNativeResult("vkAllocateMemory.TargetBufferFallback", result);
        }

        throw new VulkanOutOfMemoryException(
            $"Vulkan target buffer allocation failed ({result}). Requested={requiredProperties}",
            requiredProperties);
    }

    public void FreeMemoryAllocation(VulkanMemoryAllocation allocation)
    {
        if (allocation.Memory.Handle != 0)
            RequireMemoryAllocator().Free(Api, Device, allocation);
    }

    public void TrackLiveBuffer(Buffer buffer, string owner)
    {
        if (buffer.Handle == 0)
            return;

        _resourceRuntime.Allocations.Buffers.LiveHandles[buffer.Handle] = 0;
        RegisterResource(ObjectType.Buffer, buffer.Handle, owner);
    }

    public void TrackExternalBufferAllocation(Buffer buffer, in VulkanMemoryAllocation allocation)
    {
        if (buffer.Handle == 0)
            throw new ArgumentException("A tracked buffer allocation requires a live Vulkan buffer.", nameof(buffer));
        if (allocation.Memory.Handle == 0)
            throw new ArgumentException("A tracked buffer allocation requires bound Vulkan memory.", nameof(allocation));

        _resourceRuntime.Allocations.Buffers.Allocations[buffer.Handle] = allocation;
    }

    public void DestroyBufferRaw(Buffer? buffer, DeviceMemory? memory)
    {
        if (buffer is { Handle: not 0 } liveBuffer &&
            TryCompleteResource(ObjectType.Buffer, liveBuffer.Handle, nameof(DestroyBufferRaw)))
        {
            _resourceRuntime.Allocations.Buffers.LiveHandles.TryRemove(liveBuffer.Handle, out _);
            Api.DestroyBuffer(Device, liveBuffer, null);
            if (_resourceRuntime.Allocations.Buffers.Allocations.TryRemove(
                    liveBuffer.Handle,
                    out VulkanMemoryAllocation allocation))
            {
                FreeMemoryAllocation(allocation);
                return;
            }
        }

        if (memory is { Handle: not 0 } liveMemory)
            Api.FreeMemory(Device, liveMemory, null);
    }

    public bool TryBeginDestroyImageView(ImageView imageView, string owner)
        => imageView.Handle != 0 && TryCompleteResource(ObjectType.ImageView, imageView.Handle, owner);

    public void TrackLiveImageView(ImageView imageView, in ImageViewCreateInfo createInfo, string owner)
    {
        if (imageView.Handle == 0)
            return;

        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        RegisterResource(ObjectType.ImageView, imageView.Handle, owner);
        lock (tracker.SyncRoot)
            tracker.ImageViewBackingImages[imageView.Handle] = createInfo.Image.Handle;
    }

    public Result SubmitToQueueTracked(Queue queue, ref SubmitInfo submitInfo, Fence fence, string caller)
    {
        ThrowIfVulkanDeviceOperationNotAdmitted(caller);
        Result result = Api.QueueSubmit(queue, 1, ref submitInfo, fence);
        ObserveNativeResult(caller, result);
        return result;
    }

    public bool TryMapMemoryAllocation(VulkanMemoryAllocation allocation, ulong offset, ulong length, out void* mapped)
    {
        bool mappedSuccessfully = RequireMemoryAllocator().TryMap(
            Api,
            Device,
            allocation,
            offset,
            length,
            out mapped,
            out Result result);
        ObserveNativeResult("vkMapMemory.TargetOutput", result);
        return mappedSuccessfully;
    }

    public void UnmapMemoryAllocation(VulkanMemoryAllocation allocation)
        => RequireMemoryAllocator().Unmap(Api, Device, allocation);

    void IVulkanTargetOutputHost.MarkDeviceLost(string reason, string operation, Result result)
    {
        ObserveNativeResult(operation, result);
        Debug.VulkanWarning(
            "[Vulkan] Target output observed device loss. Operation={0} Result={1} Reason={2}",
            operation,
            result,
            reason);
    }

    private IVulkanMemoryAllocator RequireMemoryAllocator()
        => _resourceRuntime.Allocations.Buffers.MemoryAllocator
            ?? throw new InvalidOperationException("The Vulkan memory allocator is not initialized.");

    public void ObserveNativeResult(string operation, Result result)
    {
        _deviceContext.ObserveNativeResult(operation, result);
    }

    private void RegisterResource(ObjectType type, ulong handle, string owner)
        => _resourceRuntime.Lifetime.Tracker.RegisterResource(
            ResourceKey(type, handle),
            owner,
            externallyOwned: false);

    private bool TryCompleteResource(ObjectType type, ulong handle, string owner)
    {
        if (handle == 0)
            return false;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            if (tracker.ResourceLifetimes.TryGetValue(key, out VulkanResourceLifetimeRecord? record) &&
                !tracker.IsRetirementReadyNoLock(new VulkanRetirementTicket(
                    tracker.LastGraphicsSequence,
                    tracker.LastTransferSequence,
                    tracker.LastOtherSequence,
                    0,
                    0,
                    false)))
            {
                Debug.VulkanWarning(
                    "[Vulkan] Target output deferred destruction of {0} 0x{1:X} in {2}; generation {3} is still in flight.",
                    type,
                    handle,
                    owner,
                    record.Generation);
                return false;
            }

            tracker.PublishedResourceGenerations.TryRemove(key, out _);
            tracker.ResourceLifetimes.Remove(key);
            if (type == ObjectType.ImageView)
                tracker.ImageViewBackingImages.Remove(handle);
            return true;
        }
    }

    private void CompleteCommandPoolResources(CommandPool pool)
    {
        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        VulkanResourceLifetimeKey poolKey = ResourceKey(ObjectType.CommandPool, pool.Handle);
        lock (tracker.SyncRoot)
        {
            if (tracker.CommandBuffersByPool.Remove(poolKey, out HashSet<ulong>? children))
            {
                foreach (ulong child in children)
                {
                    VulkanResourceLifetimeKey childKey = ResourceKey(ObjectType.CommandBuffer, child);
                    tracker.CommandBufferLifetimes.Remove(child);
                    tracker.PublishedResourceGenerations.TryRemove(childKey, out _);
                    tracker.ResourceLifetimes.Remove(childKey);
                }
            }

            tracker.PublishedResourceGenerations.TryRemove(poolKey, out _);
            tracker.ResourceLifetimes.Remove(poolKey);
        }
    }

    private static VulkanResourceLifetimeKey ResourceKey(ObjectType type, ulong handle)
        => new(type, handle);
}
