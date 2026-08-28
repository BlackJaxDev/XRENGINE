using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Concrete native services used by presentationless and headless output targets.
/// </summary>
internal sealed partial class VulkanFrameLoop : IVulkanTargetOutputHost
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
            Api,
            _deviceContext,
            _resourceRuntime,
            imageCount,
            _outputRuntime.Desktop.Swapchain.Handle);
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

    public unsafe Result CreateVulkanCommandPoolTracked(ref CommandPoolCreateInfo createInfo, out CommandPool pool, string owner)
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

    public Result AllocateVulkanCommandBufferTracked(
        ref CommandBufferAllocateInfo allocateInfo,
        out CommandBuffer commandBuffer,
        string owner)
        => _commandRuntime.AllocateCommandBufferWithLifetime(
            ref allocateInfo,
            out commandBuffer,
            owner);

    public Result ResetVulkanCommandPoolTracked(CommandPool pool, string owner)
    {
        if (pool.Handle == 0)
            return Result.Success;

        ThrowIfVulkanDeviceOperationNotAdmitted(owner);
        return _commandRuntime.ResetVulkanCommandPoolTracked(pool, owner);
    }

    public Result BeginCommandBufferTracked(
        CommandBuffer commandBuffer,
        ref CommandBufferBeginInfo beginInfo,
        string owner)
        => _commandRuntime.BeginTrackedCommandBuffer(
            commandBuffer,
            ref beginInfo,
            owner);

    public Result EndCommandBufferTracked(CommandBuffer commandBuffer)
        => _commandRuntime.EndCommandBufferTracked(commandBuffer);

    public void TrackCommandBufferResource(
        CommandBuffer commandBuffer,
        ObjectType type,
        ulong handle,
        string owner)
        => _commandRuntime.TrackVulkanCommandBufferResource(
            commandBuffer,
            type,
            handle,
            owner);

    public unsafe void DestroyCommandPoolHostSynchronized(CommandPool pool)
    {
        if (pool.Handle == 0)
            return;

        _commandRuntime.DestroyCommandPoolHostSynchronized(pool);
    }

    public unsafe Result CreateVulkanImageTracked(ref ImageCreateInfo createInfo, out Image image, string owner)
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

    public unsafe void DestroyVulkanImageImmediateTracked(Image image, string owner)
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
            requiredProperties,
            result);
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
            requiredProperties,
            result);
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

    public unsafe void DestroyBufferRaw(Buffer? buffer, DeviceMemory? memory)
    {
        if (buffer is { Handle: not 0 } liveBuffer)
        {
            if (!TryCompleteResource(ObjectType.Buffer, liveBuffer.Handle, nameof(DestroyBufferRaw)))
            {
                // Buffer and allocation are one retirement unit. Freeing the memory
                // after deferring only the buffer corrupts pooled/VMA allocations and
                // can invalidate unrelated resources that share the backing block.
                _resourceRuntime.Buffers.Retire(
                    liveBuffer,
                    memory.GetValueOrDefault(),
                    nameof(DestroyBufferRaw));
                return;
            }

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

        if (memory is { Handle: not 0 } liveMemory &&
            _resourceRuntime.Allocations.Buffers.MemoryAllocator is VulkanLegacyAllocator)
        {
            Api.FreeMemory(Device, liveMemory, null);
            return;
        }

        if (memory is { Handle: not 0 })
        {
            Debug.VulkanWarningEvery(
                $"Vulkan.TargetOutput.UntrackedBufferMemory.{memory.Value.Handle}",
                TimeSpan.FromSeconds(5),
                "[Vulkan] Refusing to raw-free untracked buffer memory 0x{0:X}; the active allocator must release its allocation record.",
                memory.Value.Handle);
        }
    }

    public bool TryBeginDestroyImageView(ImageView imageView, string owner)
        => imageView.Handle != 0 && TryCompleteResource(ObjectType.ImageView, imageView.Handle, owner);

    public bool TryBeginReleaseExternalImage(Image image, string owner)
        => image.Handle != 0 && TryCompleteResource(
            ObjectType.Image,
            image.Handle,
            owner,
            requireExternal: true);

    public void TrackLiveImageView(ImageView imageView, in ImageViewCreateInfo createInfo, string owner)
    {
        if (imageView.Handle == 0)
            return;

        _resourceRuntime.RegisterImageViewResource(
            imageView,
            createInfo.Image,
            owner,
            IsExternalImageOwner(owner));
    }

    public Result SubmitToQueueTracked(Queue queue, ref SubmitInfo submitInfo, Fence fence, string caller)
    {
        ThrowIfVulkanDeviceOperationNotAdmitted(caller);
        return _commandRuntime.SubmitToQueueTracked(
            Api,
            _deviceContext,
            _telemetry,
            queue,
            ref submitInfo,
            fence,
            caller);
    }

    public unsafe bool TryReadMappedMemory<TState>(VulkanMemoryAllocation allocation, ulong offset, ulong length, TState state, VulkanMappedMemoryReadCallback<TState> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!TryAcquireTargetOutputMapping(allocation, offset, length, out void* mapped))
            return false;

        try
        {
            if (!TrySynchronizeTargetOutputMapping(allocation, offset, length, flush: false))
                return false;
            callback(new ReadOnlySpan<byte>(mapped, checked((int)length)), state);
            return true;
        }
        finally
        {
            RequireMemoryAllocator().Unmap(Api, Device, allocation);
        }
    }

    public unsafe bool TryWriteMappedMemory<TState>(VulkanMemoryAllocation allocation, ulong offset, ulong length, TState state, VulkanMappedMemoryWriteCallback<TState> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!TryAcquireTargetOutputMapping(allocation, offset, length, out void* mapped))
            return false;

        try
        {
            callback(new Span<byte>(mapped, checked((int)length)), state);
            return TrySynchronizeTargetOutputMapping(allocation, offset, length, flush: true);
        }
        finally
        {
            RequireMemoryAllocator().Unmap(Api, Device, allocation);
        }
    }

    /// <summary>
    /// Maps from the allocation's checked base rather than an arbitrary subrange. This keeps
    /// <c>vkMapMemory</c>'s offset aligned while the returned pointer remains bounded to the
    /// requested output range for the callback lifetime.
    /// </summary>
    private unsafe bool TryAcquireTargetOutputMapping(VulkanMemoryAllocation allocation, ulong offset, ulong length, out void* mapped)
    {
        mapped = null;
        if (!_deviceContext.IsOperational || !allocation.IsHostVisible || allocation.Memory.Handle == 0 || length is 0 or > int.MaxValue ||
            offset > allocation.Size || length > allocation.Size - offset)
        {
            return _resourceRuntime.Buffers.RecordMappingFailure();
        }

        ulong minimumMapAlignment = Math.Max(_deviceContext.MinMemoryMapAlignment, 1UL);
        if (allocation.BlockId == -1 && allocation.Offset % minimumMapAlignment != 0 && !allocation.IsNativeBacked)
            return _resourceRuntime.Buffers.RecordMappingFailure();

        bool mappedSuccessfully = RequireMemoryAllocator().TryMap(
            Api,
            Device,
            allocation,
            offset: 0,
            allocation.Size,
            out void* allocationBase,
            out Result result);
        ObserveNativeResult("vkMapMemory.TargetOutput", result);
        if (!mappedSuccessfully)
            return _resourceRuntime.Buffers.RecordMappingFailure();

        try
        {
            mapped = (byte*)allocationBase + checked((nint)offset);
            _resourceRuntime.Buffers.RecordExternalMappingReservation(length);
            return true;
        }
        catch (OverflowException)
        {
            RequireMemoryAllocator().Unmap(Api, Device, allocation);
            mapped = null;
            return _resourceRuntime.Buffers.RecordMappingFailure();
        }
    }

    /// <summary>Flushes or invalidates an atom-aligned range contained within this allocation.</summary>
    private bool TrySynchronizeTargetOutputMapping(in VulkanMemoryAllocation allocation, ulong offset, ulong length, bool flush)
    {
        if (allocation.IsCoherent)
            return true;

        ulong atomSize = Math.Max(_deviceContext.NonCoherentAtomSize, 1UL);
        ulong absoluteOffset;
        ulong allocationEnd;
        ulong absoluteEnd;
        try
        {
            absoluteOffset = checked(allocation.Offset + offset);
            allocationEnd = checked(allocation.Offset + allocation.Size);
            absoluteEnd = checked(absoluteOffset + length);
        }
        catch (OverflowException)
        {
            return false;
        }

        ulong rangeOffset;
        ulong rangeEnd;
        try
        {
            rangeOffset = absoluteOffset / atomSize * atomSize;
            rangeEnd = AlignUpForTargetOutput(absoluteEnd, atomSize);
        }
        catch (OverflowException)
        {
            return _resourceRuntime.Buffers.RecordMappingFailure();
        }
        rangeOffset = Math.Max(rangeOffset, allocation.Offset);
        rangeEnd = Math.Min(rangeEnd, allocationEnd);
        if (rangeEnd <= rangeOffset)
            return _resourceRuntime.Buffers.RecordMappingFailure();

        ulong expandedLength = rangeEnd - rangeOffset;
        _resourceRuntime.Buffers.RecordExternalVisibilityExpansion(flush, length, expandedLength);

        MappedMemoryRange range = new()
        {
            SType = StructureType.MappedMemoryRange,
            Memory = allocation.Memory,
            Offset = rangeOffset,
            Size = expandedLength,
        };
        Result result = flush
            ? Api.FlushMappedMemoryRanges(Device, 1, ref range)
            : Api.InvalidateMappedMemoryRanges(Device, 1, ref range);
        ObserveNativeResult(flush ? "vkFlushMappedMemoryRanges.TargetOutput" : "vkInvalidateMappedMemoryRanges.TargetOutput", result);
        return result == Result.Success;
    }

    private static ulong AlignUpForTargetOutput(ulong value, ulong alignment)
        => checked(((value + alignment - 1UL) / alignment) * alignment);

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

    private static bool IsExternalImageOwner(string owner)
        => owner.StartsWith("OpenXR.Swapchain", StringComparison.Ordinal) ||
           owner.StartsWith("Swapchain.Color", StringComparison.Ordinal);

    private bool TryCompleteResource(
        ObjectType type,
        ulong handle,
        string owner,
        bool requireExternal = false)
    {
        if (handle == 0)
            return false;

        VulkanResourceLifetimeKey key = ResourceKey(type, handle);
        VulkanResourceLifetimeTracker tracker = _resourceRuntime.Lifetime.Tracker;
        lock (tracker.SyncRoot)
        {
            if (tracker.ResourceLifetimes.TryGetValue(
                    key,
                    out VulkanResourceLifetimeRecord? record))
            {
                if (requireExternal &&
                    (record.State & EVulkanResourceLifetimeState.External) == 0)
                {
                    throw new InvalidOperationException(
                        $"Target output cannot release engine-owned Vulkan resource {key} through the external-image path ({owner}).");
                }

                if (!record.Pins.IsRetirementReady(
                        tracker.CompletedGraphicsSequence,
                        tracker.CompletedTransferSequence,
                        tracker.CompletedOtherSequence))
                {
                    Debug.VulkanWarning(
                        "[Vulkan] Target output deferred destruction of {0} 0x{1:X} in {2}; generation {3} is still in flight.",
                        type,
                        handle,
                        owner,
                        record.Generation);
                    return false;
                }
            }

            tracker.RemoveResourceNoLock(key);
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
                    tracker.RemoveResourceNoLock(childKey);
                }
            }

            tracker.RemoveResourceNoLock(poolKey);
        }
    }

    private static VulkanResourceLifetimeKey ResourceKey(ObjectType type, ulong handle)
        => new(type, handle);
}
