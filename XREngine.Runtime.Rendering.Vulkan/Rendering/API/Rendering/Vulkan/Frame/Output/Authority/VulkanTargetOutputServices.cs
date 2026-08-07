using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Concrete native services used by presentationless and headless output targets.
/// </summary>
internal sealed unsafe class VulkanTargetOutputServices
{
    private readonly Vk _api;
    private readonly VulkanDeviceContext _deviceContext;
    private readonly VulkanCommandRuntime _commandRuntime;
    private readonly VulkanResourceRuntime _resourceRuntime;
    private readonly VulkanFrameTelemetry _telemetry;
    private readonly VulkanOutputRuntime _outputRuntime;

    internal VulkanTargetOutputServices(
        Vk api,
        VulkanDeviceContext deviceContext,
        VulkanCommandRuntime commandRuntime,
        VulkanResourceRuntime resourceRuntime,
        VulkanFrameTelemetry telemetry,
        VulkanOutputRuntime outputRuntime)
    {
        _api = api;
        _deviceContext = deviceContext;
        _commandRuntime = commandRuntime;
        _resourceRuntime = resourceRuntime;
        _telemetry = telemetry;
        _outputRuntime = outputRuntime;
    }

    internal Vk VulkanApi => _api;
    internal Instance Instance => _deviceContext.Instance;
    internal PhysicalDevice PhysicalDevice => _deviceContext.PhysicalDevice;
    internal Device Device => _deviceContext.Device;
    internal Queue GraphicsQueue => _deviceContext.GraphicsQueue;
    internal Queue PresentQueue => _deviceContext.PresentQueue;
    internal SurfaceKHR TargetSurface => _outputRuntime.Surface;
    internal VulkanDeviceContext DeviceContext => _deviceContext;

    internal KhrSurface RequireSurfaceApi()
        => _outputRuntime.SurfaceApi
            ?? throw new InvalidOperationException("Vulkan surface API is not initialized.");

    internal void ThrowIfVulkanDeviceOperationNotAdmitted(string operation)
    {
        if (!TryAdmitVulkanDeviceOperation(operation, out string failureReason))
            throw new InvalidOperationException(failureReason);
    }

    internal bool TryAdmitVulkanDeviceOperation(string operation, out string failureReason)
    {
        if (_deviceContext.IsOperational)
        {
            failureReason = string.Empty;
            return true;
        }

        failureReason = $"Cannot start Vulkan operation '{operation}' while device state is {_deviceContext.State}.";
        return false;
    }

    internal void NotifyVulkanFenceCompleted(Fence fence)
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

    internal Result CreateVulkanCommandPoolTracked(ref CommandPoolCreateInfo createInfo, out CommandPool pool, string owner)
    {
        pool = default;
        ThrowIfVulkanDeviceOperationNotAdmitted(owner);
        lock (_commandRuntime.Pools.Gate)
        {
            Result result = _api.CreateCommandPool(Device, ref createInfo, null, out pool);
            ObserveNativeResult(owner, result);
            if (result == Result.Success)
                RegisterResource(ObjectType.CommandPool, pool.Handle, owner);
            return result;
        }
    }

    internal Result AllocateVulkanCommandBufferTracked(ref CommandBufferAllocateInfo allocateInfo, out CommandBuffer commandBuffer, string owner)
    {
        commandBuffer = default;
        ThrowIfVulkanDeviceOperationNotAdmitted(owner);
        lock (_commandRuntime.Pools.Gate)
        {
            Result result = _api.AllocateCommandBuffers(Device, ref allocateInfo, out commandBuffer);
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

    internal Result ResetVulkanCommandPoolTracked(CommandPool pool, string owner)
    {
        if (pool.Handle == 0)
            return Result.Success;

        ThrowIfVulkanDeviceOperationNotAdmitted(owner);
        lock (_commandRuntime.Pools.Gate)
        {
            Result result = _api.ResetCommandPool(Device, pool, 0);
            ObserveNativeResult(owner, result);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResetCommandPoolCall();
            return result;
        }
    }

    internal void DestroyCommandPoolHostSynchronized(CommandPool pool)
    {
        if (pool.Handle == 0)
            return;

        lock (_commandRuntime.Pools.Gate)
        {
            _api.DestroyCommandPool(Device, pool, null);
            CompleteCommandPoolResources(pool);
        }
    }

    internal Result CreateVulkanImageTracked(ref ImageCreateInfo createInfo, out Image image, string owner)
    {
        image = default;
        ThrowIfVulkanDeviceOperationNotAdmitted("vkCreateImage." + owner);
        Result result = _api.CreateImage(Device, ref createInfo, null, out image);
        ObserveNativeResult("vkCreateImage." + owner, result);
        if (result == Result.Success)
            RegisterResource(ObjectType.Image, image.Handle, owner);
        return result;
    }

    internal void DestroyVulkanImageImmediateTracked(Image image, string owner)
    {
        if (image.Handle == 0 || !TryCompleteResource(ObjectType.Image, image.Handle, owner))
            return;

        _api.DestroyImage(Device, image, null);
        _resourceRuntime.Allocations.Images.Allocations.TryRemove(image.Handle, out _);
    }

    internal VulkanMemoryAllocation AllocateImageMemoryWithFallback(Image image, MemoryPropertyFlags requiredProperties)
    {
        IVulkanMemoryAllocator allocator = RequireMemoryAllocator();
        if (allocator.TryAllocateForImage(
                _api,
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
            if (allocator.TryAllocateForImage(_api, Device, image, fallback, out allocation, out result))
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

    internal VulkanMemoryAllocation AllocateBufferMemoryWithFallback(Buffer buffer, MemoryPropertyFlags requiredProperties)
    {
        IVulkanMemoryAllocator allocator = RequireMemoryAllocator();
        if (allocator.TryAllocateForBuffer(
                _api,
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
            if (allocator.TryAllocateForBuffer(_api, Device, buffer, fallback, out allocation, out result))
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

    internal void FreeMemoryAllocation(VulkanMemoryAllocation allocation)
    {
        if (allocation.Memory.Handle != 0)
            RequireMemoryAllocator().Free(_api, Device, allocation);
    }

    internal void TrackLiveBuffer(Buffer buffer, string owner)
    {
        if (buffer.Handle == 0)
            return;

        _resourceRuntime.Allocations.Buffers.LiveHandles[buffer.Handle] = 0;
        RegisterResource(ObjectType.Buffer, buffer.Handle, owner);
    }

    internal void TrackExternalBufferAllocation(Buffer buffer, in VulkanMemoryAllocation allocation)
    {
        if (buffer.Handle == 0)
            throw new ArgumentException("A tracked buffer allocation requires a live Vulkan buffer.", nameof(buffer));
        if (allocation.Memory.Handle == 0)
            throw new ArgumentException("A tracked buffer allocation requires bound Vulkan memory.", nameof(allocation));

        _resourceRuntime.Allocations.Buffers.Allocations[buffer.Handle] = allocation;
    }

    internal void DestroyBufferRaw(Buffer? buffer, DeviceMemory? memory)
    {
        if (buffer is { Handle: not 0 } liveBuffer &&
            TryCompleteResource(ObjectType.Buffer, liveBuffer.Handle, nameof(DestroyBufferRaw)))
        {
            _resourceRuntime.Allocations.Buffers.LiveHandles.TryRemove(liveBuffer.Handle, out _);
            _api.DestroyBuffer(Device, liveBuffer, null);
            if (_resourceRuntime.Allocations.Buffers.Allocations.TryRemove(
                    liveBuffer.Handle,
                    out VulkanMemoryAllocation allocation))
            {
                FreeMemoryAllocation(allocation);
                return;
            }
        }

        if (memory is { Handle: not 0 } liveMemory)
            _api.FreeMemory(Device, liveMemory, null);
    }

    internal bool TryBeginDestroyImageView(ImageView imageView, string owner)
        => imageView.Handle != 0 && TryCompleteResource(ObjectType.ImageView, imageView.Handle, owner);

    internal void TrackLiveImageView(ImageView imageView, in ImageViewCreateInfo createInfo, string owner)
    {
        if (imageView.Handle == 0)
            return;

        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        RegisterResource(ObjectType.ImageView, imageView.Handle, owner);
        lock (tracker.SyncRoot)
            tracker.ImageViewBackingImages[imageView.Handle] = createInfo.Image.Handle;
    }

    internal Result SubmitToQueueTracked(Queue queue, ref SubmitInfo submitInfo, Fence fence, string caller)
    {
        ThrowIfVulkanDeviceOperationNotAdmitted(caller);
        Result result = _api.QueueSubmit(queue, 1, ref submitInfo, fence);
        ObserveNativeResult(caller, result);
        return result;
    }

    internal bool TryMapMemoryAllocation(VulkanMemoryAllocation allocation, ulong offset, ulong length, out void* mapped)
    {
        bool mappedSuccessfully = RequireMemoryAllocator().TryMap(
            _api,
            Device,
            allocation,
            offset,
            length,
            out mapped,
            out Result result);
        ObserveNativeResult("vkMapMemory.TargetOutput", result);
        return mappedSuccessfully;
    }

    internal void UnmapMemoryAllocation(VulkanMemoryAllocation allocation)
        => RequireMemoryAllocator().Unmap(_api, Device, allocation);

    internal void MarkDeviceLost(string reason, string operation, Result result)
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

    private void ObserveNativeResult(string operation, Result result)
    {
        bool firstObservation =
            result == Result.ErrorDeviceLost &&
            _deviceContext.StateMachine.IsOperational;
        _deviceContext.ObserveNativeResult(operation, result);
        if (result != Result.ErrorDeviceLost)
            return;

        lock (_telemetry._deviceLostTransitionLock)
        {
            _resourceRuntime.Lifetime.Tracker.DeviceLost = true;
            if (firstObservation)
            {
                _deviceContext.DeviceFaultFacility.CompleteDeviceLoss(
                    $"{operation} returned {result}");
                _deviceContext.CompleteDeviceLossCollection();
            }
            else
            {
                _deviceContext.DeviceFaultFacility.RecordDeviceLossFallout();
            }
        }
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