using Silk.NET.Vulkan;
using XREngine.Rendering.Resources;
using XREngine.Rendering.RenderGraph;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Performs the small, synchronous native readbacks that are required by desktop
/// frame policy. This deliberately consumes the already-published Vulkan runtime
/// authorities rather than consulting a renderer facade.
/// </summary>
internal sealed unsafe class VulkanTextureReadbackService
{
    private const ulong AutoExposureByteCount = sizeof(float);

    private readonly VulkanDeviceContext _deviceContext;
    private readonly VulkanResourceRuntime _resourceRuntime;
    private readonly VulkanCommandRuntime _commandRuntime;
    private readonly VulkanFramePlanner _framePlanner;
    private readonly VulkanFrameTelemetry _telemetry;

    internal VulkanTextureReadbackService(
        VulkanDeviceContext deviceContext,
        VulkanResourceRuntime resourceRuntime,
        VulkanCommandRuntime commandRuntime,
        VulkanFramePlanner framePlanner,
        VulkanFrameTelemetry telemetry)
    {
        _deviceContext = deviceContext;
        _resourceRuntime = resourceRuntime;
        _commandRuntime = commandRuntime;
        _framePlanner = framePlanner;
        _telemetry = telemetry;
    }

    /// <summary>
    /// Reads the active desktop planner's 1x1 auto-exposure texture. The caller
    /// supplies its frame context, so no global renderer selection is involved.
    /// </summary>
    internal bool TryReadDesktopAutoExposure(
        in FrameOpContext context,
        out double exposure,
        out string diagnostic)
    {
        exposure = 0.0;
        diagnostic = string.Empty;
        if (!_deviceContext.IsOperational)
        {
            diagnostic = $"Desktop AutoExposureTex readback rejected while device state is {_deviceContext.State}.";
            return false;
        }

        if (context.ResourceRegistry is null ||
            !context.ResourceRegistry.TextureRecords.TryGetValue(
                DefaultRenderPipeline.AutoExposureTextureName,
                out RenderTextureResource? record) ||
            record.Instance is null)
        {
            diagnostic = "Desktop AutoExposureTex is not registered with a live texture instance.";
            return false;
        }

        // The frame loop calls this while the desktop frame context is active. Do
        // not manufacture another scope here: the published planner generation and
        // resource-registry identity are the authoritative readback boundary.
        if (!TryResolvePlannerScopedExposureImage(
                in context,
                out VulkanPhysicalImageGroup? exposureGroup,
                out ImageLayout sourceLayout,
                out diagnostic))
        {
            return false;
        }

        if (exposureGroup is not { } group ||
            group.Format is not (Format.R16Sfloat or Format.R32Sfloat) ||
            group.Samples != SampleCountFlags.Count1Bit ||
            group.ResolvedExtent.Width != 1 ||
            group.ResolvedExtent.Height != 1 ||
            (group.Usage & ImageUsageFlags.TransferSrcBit) == 0)
        {
            diagnostic = exposureGroup is null
                ? "Desktop AutoExposureTex has no physical image group in the active planner scope."
                : $"Desktop AutoExposureTex requires a single-sample 1x1 R16Sfloat or R32Sfloat image with transfer-source usage; found {exposureGroup.Format}/{exposureGroup.Samples}/{exposureGroup.ResolvedExtent.Width}x{exposureGroup.ResolvedExtent.Height}/{exposureGroup.Usage}.";
            return false;
        }

        if (!TryReadExposureSample(
                group.Image,
                group.Format,
                sourceLayout,
                out float sample,
                out diagnostic))
            return false;

        if (!float.IsFinite(sample))
        {
            diagnostic = "Desktop AutoExposureTex returned a non-finite sample.";
            return false;
        }

        exposure = sample;
        diagnostic = "Read 1x1 desktop AutoExposureTex from the active Vulkan planner generation.";
        return true;
    }

    private bool TryResolvePlannerScopedExposureImage(
        in FrameOpContext context,
        out VulkanPhysicalImageGroup? exposureGroup,
        out ImageLayout layout,
        out string diagnostic)
    {
        exposureGroup = null;
        layout = ImageLayout.Undefined;
        diagnostic = string.Empty;
        ResourcePlannerRuntimeState state;
        try
        {
            state = _framePlanner
                .GetPublishedResourcePlannerGeneration<ResourcePlannerRuntimeGeneration>()
                .State;
        }
        catch (InvalidOperationException ex)
        {
            diagnostic = $"Desktop AutoExposureTex planner state is unavailable: {ex.Message}";
            return false;
        }
        if (state.LastActiveFrameOpContext is not FrameOpContext activeContext ||
            !ReferenceEquals(activeContext.ResourceRegistry, context.ResourceRegistry) ||
            activeContext.PipelineIdentity != context.PipelineIdentity ||
            activeContext.ViewportIdentity != context.ViewportIdentity ||
            activeContext.ResourceGeneration != context.ResourceGeneration)
        {
            diagnostic = "Desktop AutoExposureTex is not owned by the currently published planner scope.";
            return false;
        }

        if (!state.ResourceAllocator.TryGetPhysicalGroupForResource(
                DefaultRenderPipeline.AutoExposureTextureName,
                out exposureGroup) ||
            exposureGroup is null ||
            !exposureGroup.IsAllocated ||
            exposureGroup.Image.Handle == 0)
        {
            diagnostic = "Desktop AutoExposureTex has no allocated physical image in the active planner scope.";
            return false;
        }

        layout = exposureGroup.GetKnownLayout(0, 1, 0, 1);
        if (layout == ImageLayout.Undefined)
        {
            diagnostic = "Desktop AutoExposureTex has no tracked subresource layout in the active planner scope.";
            return false;
        }

        return true;
    }

    private bool TryReadExposureSample(
        Image sourceImage,
        Format sourceFormat,
        ImageLayout sourceLayout,
        out float sample,
        out string diagnostic)
    {
        sample = 0.0f;
        diagnostic = string.Empty;
        Vk api = _deviceContext.Api;
        Buffer stagingBuffer = default;
        VulkanMemoryAllocation allocation = default;
        CommandBuffer commandBuffer = default;
        Fence fence = default;
        bool mapped = false;
        bool nativeResourcesMayBeReleased = true;

        try
        {
            ulong byteCount = sourceFormat == Format.R16Sfloat ? sizeof(ushort) : AutoExposureByteCount;
            if (!TryCreateStagingBuffer(api, byteCount, out stagingBuffer, out allocation, out diagnostic) ||
                !TryAllocateAndRecordCopy(api, sourceImage, sourceLayout, stagingBuffer, out commandBuffer, out diagnostic) ||
                !TrySubmitAndWait(
                    api,
                    commandBuffer,
                    sourceImage,
                    stagingBuffer,
                    out fence,
                    out nativeResourcesMayBeReleased,
                    out diagnostic))
            {
                return false;
            }

            if (!TryMapReadback(api, allocation, byteCount, out void* mappedPointer, out diagnostic))
                return false;

            mapped = true;
            sample = sourceFormat == Format.R16Sfloat
                ? (float)*(Half*)mappedPointer
                : *(float*)mappedPointer;
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = $"Desktop AutoExposureTex native readback failed: {ex.Message}";
            return false;
        }
        finally
        {
            if (mapped)
                _resourceRuntime.Allocations.Buffers.MemoryAllocator?.Unmap(api, _deviceContext.Device, allocation);
            if (nativeResourcesMayBeReleased && fence.Handle != 0)
                api.DestroyFence(_deviceContext.Device, fence, null);
            if (nativeResourcesMayBeReleased && commandBuffer.Handle != 0)
                DestroyTrackedCommandBuffer(api, ref commandBuffer);
            if (nativeResourcesMayBeReleased && stagingBuffer.Handle != 0)
                DestroyTrackedStagingBuffer(api, stagingBuffer, allocation);
        }
    }

    private bool TryCreateStagingBuffer(
        Vk api,
        ulong byteCount,
        out Buffer buffer,
        out VulkanMemoryAllocation allocation,
        out string diagnostic)
    {
        buffer = default;
        allocation = default;
        diagnostic = string.Empty;
        BufferCreateInfo createInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = byteCount,
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };
        Result result = api.CreateBuffer(_deviceContext.Device, ref createInfo, null, out buffer);
        _deviceContext.ObserveNativeResult("vkCreateBuffer.DesktopAutoExposureReadback", result);
        if (result != Result.Success || buffer.Handle == 0)
        {
            diagnostic = $"vkCreateBuffer failed ({result}).";
            return false;
        }

        _resourceRuntime.Allocations.Buffers.LiveHandles[buffer.Handle] = 0;
        _resourceRuntime.Lifetime.Tracker.RegisterResource(
            new VulkanResourceLifetimeKey(ObjectType.Buffer, buffer.Handle),
            "DesktopAutoExposureReadback.Staging",
            externallyOwned: false);

        IVulkanMemoryAllocator? allocator = _resourceRuntime.Allocations.Buffers.MemoryAllocator;
        if (allocator is null)
        {
            diagnostic = "Readback staging allocation requires an initialized Vulkan memory allocator.";
            return false;
        }

        if (!allocator.TryAllocateForBuffer(
                api,
                _deviceContext.Device,
                buffer,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit,
                out allocation,
                out result))
        {
            _deviceContext.ObserveNativeResult("vkAllocateMemory.DesktopAutoExposureReadback", result);
            diagnostic = $"Readback staging allocation failed ({result}).";
            return false;
        }

        _resourceRuntime.Allocations.Buffers.Allocations[buffer.Handle] = allocation;
        return true;
    }

    private bool TryAllocateAndRecordCopy(
        Vk api,
        Image sourceImage,
        ImageLayout sourceLayout,
        Buffer stagingBuffer,
        out CommandBuffer commandBuffer,
        out string diagnostic)
    {
        commandBuffer = default;
        diagnostic = string.Empty;
        CommandPool pool = _commandRuntime.Pools.PrimaryGraphics;
        if (pool.Handle == 0)
        {
            diagnostic = "Desktop AutoExposureTex readback has no graphics command pool.";
            return false;
        }

        CommandBufferAllocateInfo allocateInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        Result result;
        lock (_commandRuntime.Pools.Gate)
            result = api.AllocateCommandBuffers(_deviceContext.Device, ref allocateInfo, out commandBuffer);
        _deviceContext.ObserveNativeResult("vkAllocateCommandBuffers.DesktopAutoExposureReadback", result);
        if (result != Result.Success || commandBuffer.Handle == 0)
        {
            diagnostic = $"vkAllocateCommandBuffers failed ({result}).";
            return false;
        }
        _resourceRuntime.RegisterSynchronousCommandBuffer(
            commandBuffer,
            pool,
            CommandBufferLevel.Primary,
            "DesktopAutoExposureReadback.CommandBuffer");

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        result = api.BeginCommandBuffer(commandBuffer, ref beginInfo);
        _deviceContext.ObserveNativeResult("vkBeginCommandBuffer.DesktopAutoExposureReadback", result);
        if (result != Result.Success)
        {
            diagnostic = $"vkBeginCommandBuffer failed ({result}).";
            return false;
        }

        ImageSubresourceRange range = new()
        {
            AspectMask = ImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = 1,
            BaseArrayLayer = 0,
            LayerCount = 1,
        };
        ImageMemoryBarrier toTransfer = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = sourceLayout,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = sourceImage,
            SubresourceRange = range,
            SrcAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit | AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
        };
        api.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &toTransfer);

        BufferImageCopy copy = new()
        {
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                MipLevel = 0,
                BaseArrayLayer = 0,
                LayerCount = 1,
            },
            ImageExtent = new Extent3D(1, 1, 1),
        };
        api.CmdCopyImageToBuffer(
            commandBuffer,
            sourceImage,
            ImageLayout.TransferSrcOptimal,
            stagingBuffer,
            1,
            &copy);

        ImageMemoryBarrier restore = toTransfer;
        restore.OldLayout = ImageLayout.TransferSrcOptimal;
        restore.NewLayout = sourceLayout;
        restore.SrcAccessMask = AccessFlags.TransferReadBit;
        restore.DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit | AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit;
        api.CmdPipelineBarrier(
            commandBuffer,
            PipelineStageFlags.TransferBit,
            PipelineStageFlags.AllCommandsBit,
            0,
            0,
            null,
            0,
            null,
            1,
            &restore);

        result = _commandRuntime.EndCommandBufferTracked(commandBuffer);
        _deviceContext.ObserveNativeResult("vkEndCommandBuffer.DesktopAutoExposureReadback", result);
        if (result == Result.Success)
            return true;

        diagnostic = $"vkEndCommandBuffer failed ({result}).";
        return false;
    }

    private bool TrySubmitAndWait(
        Vk api,
        CommandBuffer commandBuffer,
        Image sourceImage,
        Buffer stagingBuffer,
        out Fence fence,
        out bool nativeResourcesMayBeReleased,
        out string diagnostic)
    {
        fence = default;
        nativeResourcesMayBeReleased = true;
        diagnostic = string.Empty;
        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
        Result result;
        fixed (Fence* fencePtr = &fence)
            result = api.CreateFence(_deviceContext.Device, ref fenceInfo, null, fencePtr);
        _deviceContext.ObserveNativeResult("vkCreateFence.DesktopAutoExposureReadback", result);
        if (result != Result.Success)
        {
            diagnostic = $"vkCreateFence failed ({result}).";
            return false;
        }

        SubmitInfo submit = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
        };
        lock (_commandRuntime.CommandBuffers.OneTimeSubmitGate)
            result = api.QueueSubmit(_deviceContext.GraphicsQueue, 1, ref submit, fence);
        _deviceContext.ObserveNativeResult("vkQueueSubmit.DesktopAutoExposureReadback", result);
        if (result != Result.Success)
        {
            diagnostic = $"vkQueueSubmit failed ({result}).";
            return false;
        }

        nativeResourcesMayBeReleased = false;
        try
        {
            _resourceRuntime.RecordSynchronousGraphicsSubmission(
                commandBuffer,
                fence,
                _deviceContext.GraphicsQueue,
                sourceImage,
                stagingBuffer);
        }
        catch (Exception ex)
        {
            Result recoveryWait;
            fixed (Fence* fencePtr = &fence)
                recoveryWait = api.WaitForFences(_deviceContext.Device, 1, fencePtr, true, ulong.MaxValue);
            _deviceContext.ObserveNativeResult("vkWaitForFences.DesktopAutoExposureReadbackReceiptRecovery", recoveryWait);
            nativeResourcesMayBeReleased = recoveryWait == Result.Success;
            diagnostic = $"Desktop AutoExposureTex submission receipt failed after native acceptance: {ex.Message}";
            return false;
        }

        using VulkanCpuStageScope waitStage = new(_telemetry, EVulkanCpuStage.AuxiliaryFenceWait);
        fixed (Fence* fencePtr = &fence)
            result = api.WaitForFences(_deviceContext.Device, 1, fencePtr, true, ulong.MaxValue);
        _deviceContext.ObserveNativeResult("vkWaitForFences.DesktopAutoExposureReadback", result);
        if (result == Result.Success)
        {
            _resourceRuntime.CompleteSynchronousFence(fence);
            nativeResourcesMayBeReleased = true;
            return true;
        }

        diagnostic = $"vkWaitForFences failed ({result}).";
        return false;
    }

    private bool TryMapReadback(
        Vk api,
        in VulkanMemoryAllocation allocation,
        ulong byteCount,
        out void* mapped,
        out string diagnostic)
    {
        mapped = null;
        diagnostic = string.Empty;
        IVulkanMemoryAllocator? allocator = _resourceRuntime.Allocations.Buffers.MemoryAllocator;
        if (allocator is null)
        {
            diagnostic = "Readback mapping requires an initialized Vulkan memory allocator.";
            return false;
        }

        if (!allocator.TryMap(api, _deviceContext.Device, allocation, 0, byteCount, out mapped, out Result result))
        {
            _deviceContext.ObserveNativeResult("vkMapMemory.DesktopAutoExposureReadback", result);
            diagnostic = $"vkMapMemory failed ({result}).";
            return false;
        }

        if (!allocation.IsCoherent)
        {
            ulong atom = Math.Max(_deviceContext.NonCoherentAtomSize, 1UL);
            ulong offset = (allocation.Offset / atom) * atom;
            ulong end = Math.Min(allocation.Offset + byteCount, allocation.Offset + allocation.Size);
            ulong size = Math.Max(((end + atom - 1UL) / atom) * atom - offset, atom);
            MappedMemoryRange range = new()
            {
                SType = StructureType.MappedMemoryRange,
                Memory = allocation.Memory,
                Offset = offset,
                Size = size,
            };
            Result invalidateResult = api.InvalidateMappedMemoryRanges(_deviceContext.Device, 1, ref range);
            _deviceContext.ObserveNativeResult("vkInvalidateMappedMemoryRanges.DesktopAutoExposureReadback", invalidateResult);
            if (invalidateResult != Result.Success)
            {
                allocator.Unmap(api, _deviceContext.Device, allocation);
                mapped = null;
                diagnostic = $"vkInvalidateMappedMemoryRanges failed ({invalidateResult}).";
                return false;
            }
        }

        RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuBufferMapped();
        RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes((long)byteCount);
        return true;
    }

    private void DestroyTrackedCommandBuffer(Vk api, ref CommandBuffer commandBuffer)
    {
        CommandPool pool = _commandRuntime.Pools.PrimaryGraphics;
        if (pool.Handle != 0)
        {
            fixed (CommandBuffer* commandBufferPtr = &commandBuffer)
                lock (_commandRuntime.Pools.Gate)
                    api.FreeCommandBuffers(_deviceContext.Device, pool, 1, commandBufferPtr);
        }
        _resourceRuntime.CompleteSynchronousCommandBuffer(commandBuffer);
        commandBuffer = default;
    }

    private void DestroyTrackedStagingBuffer(Vk api, Buffer buffer, in VulkanMemoryAllocation allocation)
    {
        ulong handle = buffer.Handle;
        _resourceRuntime.Allocations.Buffers.LiveHandles.TryRemove(handle, out _);
        _resourceRuntime.Allocations.Buffers.Allocations.TryRemove(handle, out _);
        api.DestroyBuffer(_deviceContext.Device, buffer, null);
        _resourceRuntime.Allocations.Buffers.MemoryAllocator?.Free(api, _deviceContext.Device, allocation);
        _resourceRuntime.CompleteDetachedExternalResourceDestruction(ObjectType.Buffer, handle, _resourceRuntime.GetPublishedGeneration(ObjectType.Buffer, handle), forced: false);
    }
}
