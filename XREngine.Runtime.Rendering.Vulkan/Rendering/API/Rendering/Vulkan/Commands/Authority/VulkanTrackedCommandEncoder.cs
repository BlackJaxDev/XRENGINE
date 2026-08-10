using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen native command services consumed by prepared secondary workers.
/// It deliberately owns no renderer or planner reference.
/// </summary>
internal unsafe sealed class VulkanTrackedCommandEncoder(
    Vk api,
    VulkanDeviceContext deviceContext,
    VulkanCommandRuntime commandRuntime,
    VulkanResourceRuntime resourceRuntime,
    VulkanFrameTelemetry? telemetry = null)
{
    internal Vk Api { get; } = api;
    internal VulkanDeviceContext DeviceContext { get; } = deviceContext;
    internal VulkanCommandRuntime CommandRuntime { get; } = commandRuntime;
    internal VulkanResourceRuntime ResourceRuntime { get; } = resourceRuntime;
    internal VulkanFrameTelemetry? Telemetry { get; } = telemetry;

    internal Result Reset(CommandBuffer commandBuffer)
    {
        if (!ResourceRuntime.CanResetCommandBuffer(CommandRuntime, commandBuffer))
            throw new InvalidOperationException($"Command buffer 0x{unchecked((ulong)commandBuffer.Handle):X} cannot be reset.");

        Result result = Api.ResetCommandBuffer(commandBuffer, 0);
        RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanResetCommandBufferCall();
        if (result == Result.Success)
            ResourceRuntime.CompleteCommandBufferReset(unchecked((ulong)commandBuffer.Handle));
        return result;
    }

    internal void BeginTracking(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            return;

        VulkanCommandBufferTrackingBatch batch = CommandRuntime.CommandBuffers.TrackingBatches.GetOrAdd(handle, static _ => new());
        lock (batch)
        {
            if (batch.QueuedSubmissionCount != 0)
                throw new InvalidOperationException($"Command buffer 0x{handle:X} is queued for submission.");
            batch.Reset(CommandRuntime.CommandBuffers.ResolveRecordingGeneration(commandBuffer));
        }
        CommandRuntime.ResetCommandBufferImageLayoutJournal(commandBuffer);
    }

    internal Result End(CommandBuffer commandBuffer, bool cacheVariant = true)
    {
        Result result = Api.EndCommandBuffer(commandBuffer);
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        bool published = result != Result.Success;
        string reason = string.Empty;
        if (result == Result.Success && handle != 0 &&
            CommandRuntime.CommandBuffers.TrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
        {
            published = Telemetry is null
                ? ResourceRuntime.TryPublishCommandBufferTrackingBatch(
                    CommandRuntime,
                    commandBuffer,
                    batch,
                    out reason)
                : CommandRuntime.TryFlushTrackingBatchForRetirement(
                    ResourceRuntime,
                    commandBuffer,
                    batch,
                    Telemetry,
                    out reason);
            lock (batch)
                batch.IsRecording = false;
        }

        if (result == Result.Success && published)
        {
            ResourceRuntime.CompleteCommandBufferRecording(commandBuffer, cacheVariant);
            return result;
        }

        ResourceRuntime.AbandonCommandBufferRecording(commandBuffer);
        if (handle != 0)
            CommandRuntime.CommandBuffers.TrackingBatches.TryRemove(handle, out _);
        if (result == Result.Success)
            throw new InvalidOperationException($"Vulkan command-buffer tracking publication failed: {reason}");
        return result;
    }

    internal void Abandon(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        ResourceRuntime.AbandonCommandBufferRecording(commandBuffer);
        if (handle != 0)
            CommandRuntime.CommandBuffers.TrackingBatches.TryRemove(handle, out _);
        CommandRuntime.Synchronization.RemoveRecordedImageLayouts(commandBuffer);
    }

    internal void Track(CommandBuffer commandBuffer, ObjectType type, ulong handle)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || handle == 0 ||
            !CommandRuntime.CommandBuffers.TrackingBatches.TryGetValue(commandBufferHandle, out VulkanCommandBufferTrackingBatch? batch))
            return;

        lock (batch)
        {
            if (batch.QueuedSubmissionCount != 0)
                throw new InvalidOperationException($"Command buffer 0x{commandBufferHandle:X} cannot record dependencies while queued.");
            batch.RecordDependency(new VulkanResourceLifetimeKey(type, handle));
        }
    }

    /// <summary>
    /// Publishes a recorded image-access delta for the current command-buffer
    /// generation. The synchronization authority consumes the delta when the
    /// encoder ends the recording.
    /// </summary>
    internal void RecordImageAccess(
        CommandBuffer commandBuffer,
        Image image,
        in ImageSubresourceRange range,
        in VulkanImageAccessState state)
    {
        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || image.Handle == 0 ||
            !CommandRuntime.CommandBuffers.TrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return;
        }

        lock (batch)
        {
            if (batch.QueuedSubmissionCount != 0)
                throw new InvalidOperationException(
                    $"Command buffer 0x{commandBufferHandle:X} cannot record image access while queued.");
            batch.RecordImageAccess(new VulkanImageAccessRangeDelta(
                image.Handle,
                range,
                state));
        }
    }

    internal void BindPipeline(CommandBuffer commandBuffer, Pipeline pipeline)
    {
        Track(commandBuffer, ObjectType.Pipeline, pipeline.Handle);
        Api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
    }

    internal void BindVertexBuffer(CommandBuffer commandBuffer, uint binding, Silk.NET.Vulkan.Buffer buffer)
    {
        Track(commandBuffer, ObjectType.Buffer, buffer.Handle);
        ulong offset = 0;
        Api.CmdBindVertexBuffers(commandBuffer, binding, 1, &buffer, &offset);
    }

    internal void BindIndexBuffer(CommandBuffer commandBuffer, Silk.NET.Vulkan.Buffer buffer, IndexType indexType)
    {
        Track(commandBuffer, ObjectType.Buffer, buffer.Handle);
        Api.CmdBindIndexBuffer(commandBuffer, buffer, 0, indexType);
    }

    /// <summary>
    /// Emits a legacy pipeline barrier while publishing every referenced native
    /// resource to the command-buffer lifetime batch.
    /// </summary>
    internal void PipelineBarrier(
        CommandBuffer commandBuffer,
        PipelineStageFlags srcStageMask,
        PipelineStageFlags dstStageMask,
        DependencyFlags dependencyFlags,
        uint memoryBarrierCount,
        MemoryBarrier* memoryBarriers,
        uint bufferMemoryBarrierCount,
        BufferMemoryBarrier* bufferMemoryBarriers,
        uint imageMemoryBarrierCount,
        ImageMemoryBarrier* imageMemoryBarriers)
    {
        for (uint index = 0; index < bufferMemoryBarrierCount; index++)
            Track(commandBuffer, ObjectType.Buffer, bufferMemoryBarriers[index].Buffer.Handle);
        for (uint index = 0; index < imageMemoryBarrierCount; index++)
            Track(commandBuffer, ObjectType.Image, imageMemoryBarriers[index].Image.Handle);

        Api.CmdPipelineBarrier(
            commandBuffer,
            srcStageMask,
            dstStageMask,
            dependencyFlags,
            memoryBarrierCount,
            memoryBarriers,
            bufferMemoryBarrierCount,
            bufferMemoryBarriers,
            imageMemoryBarrierCount,
            imageMemoryBarriers);
    }

    /// <summary>Clears an image and records its lifetime dependency.</summary>
    internal void ClearColorImage(
        CommandBuffer commandBuffer,
        Image image,
        ImageLayout layout,
        ref ClearColorValue color,
        uint rangeCount,
        ref ImageSubresourceRange ranges)
    {
        Track(commandBuffer, ObjectType.Image, image.Handle);
        Api.CmdClearColorImage(commandBuffer, image, layout, ref color, rangeCount, ref ranges);
    }

    /// <summary>Copies a tracked staging buffer into a tracked image.</summary>
    internal void CopyBufferToImage(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer buffer,
        Image image,
        ImageLayout layout,
        uint regionCount,
        BufferImageCopy* regions)
    {
        Track(commandBuffer, ObjectType.Buffer, buffer.Handle);
        Track(commandBuffer, ObjectType.Image, image.Handle);
        Api.CmdCopyBufferToImage(commandBuffer, buffer, image, layout, regionCount, regions);
    }

    internal void CopyBuffer(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Silk.NET.Vulkan.Buffer destination,
        uint regionCount,
        BufferCopy* regions)
    {
        Track(commandBuffer, ObjectType.Buffer, source.Handle);
        Track(commandBuffer, ObjectType.Buffer, destination.Handle);
        Api.CmdCopyBuffer(commandBuffer, source, destination, regionCount, regions);
    }

    /// <summary>Blits between images and records both lifetime dependencies.</summary>
    internal void BlitImage(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        ref ImageBlit region,
        Filter filter)
    {
        Track(commandBuffer, ObjectType.Image, source.Handle);
        Track(commandBuffer, ObjectType.Image, destination.Handle);
        Api.CmdBlitImage(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            1,
            ref region,
            filter);
    }

    internal void PushConstants<T>(CommandBuffer commandBuffer, PipelineLayout layout, ShaderStageFlags stages, in T value) where T : unmanaged
    {
        Track(commandBuffer, ObjectType.PipelineLayout, layout.Handle);
        T copy = value;
        Api.CmdPushConstants(commandBuffer, layout, stages, 0, (uint)sizeof(T), &copy);
    }

    internal void BindDescriptorSet(CommandBuffer commandBuffer, PipelineLayout layout, uint setIndex, DescriptorSet descriptorSet, ReadOnlySpan<uint> offsets)
    {
        Track(commandBuffer, ObjectType.PipelineLayout, layout.Handle);
        Track(commandBuffer, ObjectType.DescriptorSet, descriptorSet.Handle);
        DescriptorSet set = descriptorSet;
        fixed (uint* offsetsPtr = offsets)
            Api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, layout, setIndex, 1, &set, (uint)offsets.Length, offsetsPtr);
    }

    internal bool TryAcquireFrameDataLease(
        CommandBuffer commandBuffer,
        VkMeshRenderer owner,
        int drawSlot,
        ulong sealedGeneration,
        out string reason)
        => ResourceRuntime.TryAcquirePreparedFrameDataLease(
            commandBuffer,
            owner,
            drawSlot,
            sealedGeneration,
            out reason);

    internal bool TryPushDescriptorHeapProgramData(
        CommandBuffer commandBuffer,
        VkRenderProgram program,
        uint[]? dwords,
        int dwordCount)
    {
        VulkanDescriptorHeapState heap = ResourceRuntime.Descriptors.Heap;
        if (heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap)
            return true;

        DescriptorHeapProgramLayout? layout = program.DescriptorHeapLayout;
        if (layout is null || layout.PushByteCount == 0)
            return true;
        if (dwords is null || dwordCount < layout.PushDwordCount || dwords.Length < dwordCount ||
            heap.NativeFunctions is null || !heap.SamplerStorage.IsReady || !heap.ResourceStorage.IsReady)
        {
            return false;
        }

        Track(commandBuffer, ObjectType.Buffer, heap.SamplerStorage.Buffer.Handle);
        Track(commandBuffer, ObjectType.Buffer, heap.ResourceStorage.Buffer.Handle);
        BindHeapInfoEXTNative samplerHeap = new()
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            HeapRange = new DeviceAddressRangeEXTNative
            {
                Address = heap.SamplerStorage.DeviceAddress,
                Size = heap.SamplerStorage.Size,
            },
            ReservedRangeSize = Math.Max(
                heap.Properties.MinSamplerHeapReservedRange,
                heap.Properties.MinSamplerHeapReservedRangeWithEmbedded),
        };
        BindHeapInfoEXTNative resourceHeap = new()
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            HeapRange = new DeviceAddressRangeEXTNative
            {
                Address = heap.ResourceStorage.DeviceAddress,
                Size = heap.ResourceStorage.Size,
            },
            ReservedRangeSize = heap.Properties.MinResourceHeapReservedRange,
        };
        heap.NativeFunctions.CmdBindSamplerHeap(commandBuffer, &samplerHeap);
        heap.NativeFunctions.CmdBindResourceHeap(commandBuffer, &resourceHeap);
        fixed (uint* data = dwords)
        {
            PushDataInfoEXTNative push = new()
            {
                SType = VulkanDescriptorHeapExt.PushDataInfoSType,
                Data = new HostAddressRangeConstEXTNative
                {
                    Address = data,
                    Size = layout.PushByteCount,
                },
            };
            heap.NativeFunctions.CmdPushData(commandBuffer, &push);
        }

        return true;
    }

    /// <summary>
    /// Binds the active descriptor heaps and pushes an ImGui texture payload.
    /// Unlike program-owned descriptor data this has no render-program dependency.
    /// </summary>
    internal bool TryPushDescriptorHeapData(
        CommandBuffer commandBuffer,
        uint offset,
        void* data,
        uint byteCount,
        out string reason)
    {
        reason = string.Empty;
        VulkanDescriptorHeapState heap = ResourceRuntime.Descriptors.Heap;
        if (heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap ||
            heap.NativeFunctions is null || !heap.SamplerStorage.IsReady ||
            !heap.ResourceStorage.IsReady)
        {
            reason = "descriptor heap state is not active and ready";
            return false;
        }
        if (data is null || byteCount == 0 ||
            (heap.Properties.MaxPushDataSize > 0 && offset + byteCount > heap.Properties.MaxPushDataSize))
        {
            reason = "descriptor heap push-data payload is invalid";
            return false;
        }

        Track(commandBuffer, ObjectType.Buffer, heap.SamplerStorage.Buffer.Handle);
        Track(commandBuffer, ObjectType.Buffer, heap.ResourceStorage.Buffer.Handle);
        BindHeapInfoEXTNative samplerHeap = new()
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            HeapRange = new DeviceAddressRangeEXTNative
            {
                Address = heap.SamplerStorage.DeviceAddress,
                Size = heap.SamplerStorage.Size,
            },
            ReservedRangeSize = Math.Max(
                heap.Properties.MinSamplerHeapReservedRange,
                heap.Properties.MinSamplerHeapReservedRangeWithEmbedded),
        };
        BindHeapInfoEXTNative resourceHeap = new()
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            HeapRange = new DeviceAddressRangeEXTNative
            {
                Address = heap.ResourceStorage.DeviceAddress,
                Size = heap.ResourceStorage.Size,
            },
            ReservedRangeSize = heap.Properties.MinResourceHeapReservedRange,
        };
        heap.NativeFunctions.CmdBindSamplerHeap(commandBuffer, &samplerHeap);
        heap.NativeFunctions.CmdBindResourceHeap(commandBuffer, &resourceHeap);
        PushDataInfoEXTNative push = new()
        {
            SType = VulkanDescriptorHeapExt.PushDataInfoSType,
            Offset = offset,
            Data = new HostAddressRangeConstEXTNative { Address = data, Size = byteCount },
        };
        heap.NativeFunctions.CmdPushData(commandBuffer, &push);
        return true;
    }

    internal bool TryAppendDescriptorHeapInheritance(
        ref CommandBufferInheritanceInfo inheritanceInfo,
        CommandBufferInheritanceDescriptorHeapInfoEXTNative* heapInfo,
        BindHeapInfoEXTNative* samplerHeapInfo,
        BindHeapInfoEXTNative* resourceHeapInfo)
    {
        VulkanDescriptorHeapState heap = ResourceRuntime.Descriptors.Heap;
        if (heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap ||
            heap.NativeFunctions is null || !heap.SamplerStorage.IsReady || !heap.ResourceStorage.IsReady ||
            heapInfo is null || samplerHeapInfo is null || resourceHeapInfo is null)
        {
            return false;
        }

        *samplerHeapInfo = new BindHeapInfoEXTNative
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            HeapRange = new DeviceAddressRangeEXTNative
            {
                Address = heap.SamplerStorage.DeviceAddress,
                Size = heap.SamplerStorage.Size,
            },
            ReservedRangeSize = Math.Max(heap.Properties.MinSamplerHeapReservedRange, heap.Properties.MinSamplerHeapReservedRangeWithEmbedded),
        };
        *resourceHeapInfo = new BindHeapInfoEXTNative
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            HeapRange = new DeviceAddressRangeEXTNative
            {
                Address = heap.ResourceStorage.DeviceAddress,
                Size = heap.ResourceStorage.Size,
            },
            ReservedRangeSize = heap.Properties.MinResourceHeapReservedRange,
        };
        *heapInfo = new CommandBufferInheritanceDescriptorHeapInfoEXTNative
        {
            SType = VulkanDescriptorHeapExt.CommandBufferInheritanceDescriptorHeapInfoSType,
            PNext = inheritanceInfo.PNext,
            SamplerHeapBindInfo = samplerHeapInfo,
            ResourceHeapBindInfo = resourceHeapInfo,
        };
        inheritanceInfo.PNext = heapInfo;
        return true;
    }

    internal bool TryAppendDynamicRenderingLocalReadInheritance(
        in DynamicRenderingLocalReadSignature signature,
        uint colorAttachmentCount,
        ref void* pNext,
        RenderingAttachmentLocationInfo* attachmentLocationInfo,
        RenderingInputAttachmentIndexInfo* inputAttachmentIndexInfo,
        uint* colorAttachmentLocations,
        uint* colorInputAttachmentIndices,
        uint* depthInputAttachmentIndex,
        uint* stencilInputAttachmentIndex)
    {
        if (!DeviceContext.MutableCapabilities._supportsDynamicRenderingLocalRead || !signature.Enabled)
            return false;

        int locationCount = signature.ColorAttachmentLocationCount;
        int inputCount = signature.ColorInputAttachmentIndexCount;
        bool hasLocations = locationCount > 0;
        bool hasInputs = inputCount > 0 || signature.DepthInputAttachmentIndex.HasValue || signature.StencilInputAttachmentIndex.HasValue;
        if ((!hasLocations && !hasInputs) ||
            (hasLocations && (uint)locationCount != colorAttachmentCount) ||
            (inputCount > 0 && (uint)inputCount != colorAttachmentCount))
            return false;

        void* next = pNext;
        if (hasLocations)
        {
            Span<uint> locations = new(colorAttachmentLocations, locationCount);
            signature.CopyColorAttachmentLocations(locations);
            *attachmentLocationInfo = new RenderingAttachmentLocationInfo
            {
                SType = StructureType.RenderingAttachmentLocationInfo,
                PNext = next,
                ColorAttachmentCount = colorAttachmentCount,
                PColorAttachmentLocations = colorAttachmentLocations,
            };
            next = attachmentLocationInfo;
        }

        if (hasInputs)
        {
            uint* colorInputs = null;
            if (inputCount > 0)
            {
                Span<uint> inputs = new(colorInputAttachmentIndices, inputCount);
                signature.CopyColorInputAttachmentIndices(inputs);
                colorInputs = colorInputAttachmentIndices;
            }

            uint* depthInput = null;
            if (signature.DepthInputAttachmentIndex.HasValue)
            {
                *depthInputAttachmentIndex = signature.DepthInputAttachmentIndex.Value;
                depthInput = depthInputAttachmentIndex;
            }

            uint* stencilInput = null;
            if (signature.StencilInputAttachmentIndex.HasValue)
            {
                *stencilInputAttachmentIndex = signature.StencilInputAttachmentIndex.Value;
                stencilInput = stencilInputAttachmentIndex;
            }

            *inputAttachmentIndexInfo = new RenderingInputAttachmentIndexInfo
            {
                SType = StructureType.RenderingInputAttachmentIndexInfo,
                PNext = next,
                ColorAttachmentCount = inputCount > 0 ? colorAttachmentCount : 0,
                PColorAttachmentInputIndices = colorInputs,
                PDepthInputAttachmentIndex = depthInput,
                PStencilInputAttachmentIndex = stencilInput,
            };
            next = inputAttachmentIndexInfo;
        }

        pNext = next;
        return true;
    }

    internal void SetViewportScissor(CommandBuffer commandBuffer, in Viewport viewport, in Rect2D scissor)
    {
        Viewport viewportCopy = viewport;
        Rect2D scissorCopy = scissor;
        Api.CmdSetViewport(commandBuffer, 0, 1, &viewportCopy);
        Api.CmdSetScissor(commandBuffer, 0, 1, &scissorCopy);
    }

    internal void SetViewportScissor(CommandBuffer commandBuffer, Viewport[] viewports, Rect2D[] scissors, uint count)
    {
        fixed (Viewport* viewportsPtr = viewports)
        fixed (Rect2D* scissorsPtr = scissors)
        {
            Api.CmdSetViewport(commandBuffer, 0, count, viewportsPtr);
            Api.CmdSetScissor(commandBuffer, 0, count, scissorsPtr);
        }
    }
}
