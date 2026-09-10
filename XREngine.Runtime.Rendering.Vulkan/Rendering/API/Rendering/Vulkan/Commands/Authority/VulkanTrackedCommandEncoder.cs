using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen native command services consumed by prepared secondary workers.
/// It deliberately owns no renderer or planner reference.
/// </summary>
internal readonly unsafe struct VulkanTrackedCommandEncoder
{
    // The encoder is deliberately an operation-bound view over the command
    // runtime. It must not become another retained path to device, resource, or
    // telemetry authority.
    internal VulkanCommandRuntime Runtime { get; }
    internal VulkanLaneRecordingContext? LaneContext { get; }
    private Vk Api => Runtime.Api;

    internal VulkanTrackedCommandEncoder(VulkanCommandRuntime runtime)
    {
        Runtime = runtime;
        LaneContext = null;
    }

    internal VulkanTrackedCommandEncoder(VulkanCommandRuntime runtime, VulkanLaneRecordingContext? laneContext)
    {
        Runtime = runtime;
        LaneContext = laneContext;
    }

    internal Result Reset(CommandBuffer commandBuffer)
        => Runtime.ResetCommandBufferWithLifetime(commandBuffer, "TrackedCommandEncoder.Reset");

    internal Result End(CommandBuffer commandBuffer, bool cacheVariant = true)
    {
        bool published = TryEnd(commandBuffer, cacheVariant, out Result result, out string reason);
        if (result == Result.Success && !published)
            throw new InvalidOperationException($"Vulkan command-buffer tracking publication failed: {reason}");
        return result;
    }

    /// <summary>
    /// Ends native recording and attempts to publish the frozen dependency batch.
    /// A successful native end whose dependencies crossed a retirement boundary is
    /// recoverable for output paths that can discard and rebuild the command buffer.
    /// </summary>
    internal bool TryEnd(
        CommandBuffer commandBuffer,
        bool cacheVariant,
        out Result result,
        out string reason)
    {
        result = Api.EndCommandBuffer(commandBuffer);
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        bool published = result != Result.Success;
        reason = string.Empty;

        if (LaneContext is not null && LaneContext.CommandBuffer.Handle == commandBuffer.Handle)
        {
            VulkanSealedRecordingReceipt receipt = LaneContext.CreateReceipt(result == Result.Success);
            Runtime.LaneRecordingContexts.EndContext(LaneContext);

            if (result == Result.Success && handle != 0 &&
                Runtime.CommandBuffers.TrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
            {
                lock (batch)
                {
                    ReadOnlySpan<VulkanResourceLifetimeKey> deps = receipt.Dependencies.Span;
                    for (int i = 0; i < deps.Length; i++)
                        batch.RecordDependency(deps[i]);

                    ReadOnlySpan<VulkanImageAccessRangeDelta> deltas = receipt.ImageAccessDeltas.Span;
                    for (int i = 0; i < deltas.Length; i++)
                        batch.RecordImageAccess(deltas[i]);

                    ReadOnlySpan<VulkanQueueOwnershipTransferRequirement> transfers = receipt.QueueOwnershipTransfers.Span;
                    for (int i = 0; i < transfers.Length; i++)
                        batch.QueueOwnershipTransfers.Add(transfers[i]);
                }

                published = Runtime.TryFlushTrackingBatchForRetirement(
                    Runtime.ResourceRuntime,
                    commandBuffer,
                    batch,
                    Runtime.FrameTelemetry,
                    out reason);
                lock (batch)
                    batch.IsRecording = false;
            }
        }
        else if (result == Result.Success && handle != 0 &&
            Runtime.CommandBuffers.TrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
        {
            published = Runtime.TryFlushTrackingBatchForRetirement(
                Runtime.ResourceRuntime,
                commandBuffer,
                batch,
                Runtime.FrameTelemetry,
                out reason);
            lock (batch)
                batch.IsRecording = false;
        }

        if (result == Result.Success && published)
        {
            Runtime.ResourceRuntime.CompleteCommandBufferRecording(commandBuffer, cacheVariant);
            return true;
        }

        Runtime.ResourceRuntime.AbandonCommandBufferRecording(commandBuffer);
        if (handle != 0)
            Runtime.CommandBuffers.TrackingBatches.TryRemove(handle, out _);
        return false;
    }

    internal void Abandon(CommandBuffer commandBuffer)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (LaneContext is not null && LaneContext.CommandBuffer.Handle == commandBuffer.Handle)
            Runtime.LaneRecordingContexts.EndContext(LaneContext);

        Runtime.ResourceRuntime.AbandonCommandBufferRecording(commandBuffer);
        if (handle != 0)
            Runtime.CommandBuffers.TrackingBatches.TryRemove(handle, out _);
        Runtime.Synchronization.RemoveRecordedImageLayouts(commandBuffer);
    }

    internal void Track(CommandBuffer commandBuffer, ObjectType type, ulong handle)
    {
        if (LaneContext is not null && LaneContext.CommandBuffer.Handle == commandBuffer.Handle)
        {
            LaneContext.RecordDependency(new VulkanResourceLifetimeKey(type, handle));
            return;
        }

        Runtime.TrackCommandBufferResource(
            commandBuffer,
            new VulkanResourceLifetimeKey(type, handle),
            "TrackedCommandEncoder.Track");
    }

    internal void Track(
        CommandBuffer commandBuffer,
        ObjectType type,
        ulong handle,
        ulong expectedGeneration)
    {
        if (expectedGeneration == 0)
        {
            Track(commandBuffer, type, handle);
            return;
        }

        // Lane receipts carry keys without generations. Route frozen descriptor
        // closures through the runtime batch so the expected-generation
        // handshake remains atomic with retirement even on worker lanes.
        Runtime.TrackCommandBufferResource(
            commandBuffer,
            new VulkanResourceLifetimeKey(type, handle),
            "TrackedCommandEncoder.TrackExactGeneration",
            expectedGeneration);
    }

    /// <summary>
    /// Records the secondary command buffers executed by one primary command in
    /// a single tracking transaction. Primary assembly can execute hundreds of
    /// reusable secondaries, so taking the same batch monitor once per handle is
    /// avoidable serialization on the render thread.
    /// </summary>
    internal void TrackCommandBuffers(
        CommandBuffer commandBuffer,
        ReadOnlySpan<CommandBuffer> secondaryCommandBuffers)
        => Runtime.TrackExecutedCommandBuffers(
            commandBuffer,
            secondaryCommandBuffers,
            "TrackedCommandEncoder.ExecuteCommands");

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
        if (LaneContext is not null && LaneContext.CommandBuffer.Handle == commandBuffer.Handle)
        {
            LaneContext.RecordImageAccess(new VulkanImageAccessRangeDelta(
                image.Handle,
                range,
                state));
            return;
        }

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        if (commandBufferHandle == 0 || image.Handle == 0 ||
            !Runtime.CommandBuffers.TrackingBatches.TryGetValue(
                commandBufferHandle,
                out VulkanCommandBufferTrackingBatch? batch))
        {
            return;
        }

        lock (batch)
        {
            if (!batch.IsRecording || batch.QueuedSubmissionCount != 0)
                throw new InvalidOperationException(
                    $"Command buffer 0x{commandBufferHandle:X} cannot record image access outside an active, unqueued recording.");
            batch.RecordImageAccess(new VulkanImageAccessRangeDelta(
                image.Handle,
                range,
                state));
        }
    }

    internal void BindPipeline(CommandBuffer commandBuffer, Pipeline pipeline)
    {
        Track(commandBuffer, ObjectType.Pipeline, pipeline.Handle);
        if (LaneContext is not null && !LaneContext.ShouldBindPipeline(PipelineBindPoint.Graphics, pipeline))
            return;
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
        if (LaneContext is not null && !LaneContext.ShouldBindIndexBuffer(buffer, 0, indexType))
            return;
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

        for (uint index = 0; index < imageMemoryBarrierCount; index++)
        {
            ref ImageMemoryBarrier barrier = ref imageMemoryBarriers[index];
            VulkanImageAccessState next = VulkanCommandSynchronizationState.ResolveVulkanImageAccessState(
                barrier.NewLayout,
                barrier.SubresourceRange.AspectMask) with
            {
                StageMask = (PipelineStageFlags2)(ulong)dstStageMask,
                AccessMask = (AccessFlags2)(ulong)barrier.DstAccessMask,
                QueueFamilyIndex = barrier.DstQueueFamilyIndex,
                ResourceGeneration = Runtime.ResourceRuntime.GetPublishedGeneration(
                    ObjectType.Image,
                    barrier.Image.Handle),
            };
            RecordImageAccess(
                commandBuffer,
                barrier.Image,
                in barrier.SubresourceRange,
                in next);
        }
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

    internal void CopyBuffer(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Silk.NET.Vulkan.Buffer destination,
        uint regionCount,
        ref BufferCopy region)
    {
        Track(commandBuffer, ObjectType.Buffer, source.Handle);
        Track(commandBuffer, ObjectType.Buffer, destination.Handle);
        Api.CmdCopyBuffer(commandBuffer, source, destination, regionCount, ref region);
    }

    /// <summary>Copies matching image subresources and records both lifetime dependencies.</summary>
    internal void CopyImage(
        CommandBuffer commandBuffer,
        Image source,
        Image destination,
        ref ImageCopy region)
    {
        Track(commandBuffer, ObjectType.Image, source.Handle);
        Track(commandBuffer, ObjectType.Image, destination.Handle);
        Api.CmdCopyImage(commandBuffer, source, ImageLayout.TransferSrcOptimal,
            destination, ImageLayout.TransferDstOptimal, 1, ref region);
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
        if (Runtime.ResourceRuntime.Descriptors.Heap.ActiveBackend == EVulkanDescriptorBackend.DescriptorHeap)
        {
            T heapValue = value;
            if (!TryPushDescriptorHeapData(commandBuffer, 0, &heapValue, (uint)sizeof(T), null, out string reason))
                throw new InvalidOperationException($"Descriptor heap push-data failed: {reason}");
            return;
        }

        Track(commandBuffer, ObjectType.PipelineLayout, layout.Handle);
        T copy = value;
        Api.CmdPushConstants(commandBuffer, layout, stages, 0, (uint)sizeof(T), &copy);
        Runtime.InvalidateDescriptorHeapBindingState(commandBuffer);
    }

    internal void BindDescriptorSet(CommandBuffer commandBuffer, PipelineLayout layout, uint setIndex, DescriptorSet descriptorSet, ReadOnlySpan<uint> offsets)
    {
        Track(commandBuffer, ObjectType.PipelineLayout, layout.Handle);
        Track(commandBuffer, ObjectType.DescriptorSet, descriptorSet.Handle);
        DescriptorSet set = descriptorSet;
        fixed (uint* offsetsPtr = offsets)
            Api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, layout, setIndex, 1, &set, (uint)offsets.Length, offsetsPtr);
        Runtime.InvalidateDescriptorHeapBindingState(commandBuffer);
    }

    internal bool TryAcquireFrameDataLease(
        CommandBuffer commandBuffer,
        int drawSlot,
        ulong sealedGeneration,
        out string reason)
        => Runtime.ResourceRuntime.TryAcquirePreparedFrameDataLease(
            commandBuffer,
            drawSlot,
            sealedGeneration,
            out reason);

    internal bool TryPushDescriptorHeapProgramData(
        CommandBuffer commandBuffer,
        VkRenderProgram program,
        DescriptorHeapPushDataPayload payload)
    {
        if (!payload.TryTrackResourceGenerations(this, commandBuffer, out _))
            return false;
        return TryPushDescriptorHeapProgramData(
            commandBuffer,
            program.DescriptorHeapLayout?.ShaderConstantByteCount ?? 0u,
            program.DescriptorHeapLayout?.PushByteCount ?? 0u,
            payload.Dwords,
            payload.Dwords.Length,
            []);
    }

    /// <summary>Pushes an already-prepared program heap payload without retaining the program object in a worker-side draw record.</summary>
    internal bool TryPushDescriptorHeapProgramData(
        CommandBuffer commandBuffer,
        uint shaderConstantByteCount,
        uint pushByteCount,
        ReadOnlySpan<uint> dwords,
        int dwordCount,
        ReadOnlySpan<VulkanPinnedResourceGeneration> resourceGenerations)
    {
        VulkanDescriptorHeapState heap = Runtime.ResourceRuntime.Descriptors.Heap;
        if (heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap)
            return true;

        uint descriptorOffset = AlignPushDataOffset(shaderConstantByteCount);
        if (pushByteCount < descriptorOffset)
            return false;

        uint descriptorByteCount = pushByteCount - descriptorOffset;
        if (descriptorByteCount == 0)
            return true;

        int descriptorDwordOffset = checked((int)(descriptorOffset / sizeof(uint)));
        int requiredDwordCount = checked((int)((descriptorByteCount + sizeof(uint) - 1) / sizeof(uint)));
        if (dwordCount < descriptorDwordOffset + requiredDwordCount || dwords.Length < dwordCount ||
            heap.NativeFunctions is null || !heap.SamplerStorage.IsReady || !heap.ResourceStorage.IsReady)
        {
            return false;
        }

        if (!TryTrackDescriptorHeapResources(commandBuffer, resourceGenerations, out _))
            return false;
        if (!Runtime.TryEnsureDescriptorHeapsBound(commandBuffer, out _))
            return false;
        fixed (uint* data = dwords)
        {
            PushDataInfoEXTNative push = new()
            {
                SType = VulkanDescriptorHeapExt.PushDataInfoSType,
                Data = new HostAddressRangeConstEXTNative
                {
                    Address = data + descriptorDwordOffset,
                    Size = descriptorByteCount,
                },
                Offset = descriptorOffset,
            };
            heap.NativeFunctions.CmdPushData(commandBuffer, &push);
        }

        Runtime.NotifyDescriptorHeapDataPushed(commandBuffer);

        return true;
    }

    private static uint AlignPushDataOffset(uint value)
        => checked((value + sizeof(uint) - 1u) & ~(sizeof(uint) - 1u));

    /// <summary>
    /// Binds the active descriptor heaps and pushes an ImGui texture payload.
    /// Unlike program-owned descriptor data this has no render-program dependency.
    /// </summary>
    internal bool TryPushDescriptorHeapData(
        CommandBuffer commandBuffer,
        uint offset,
        void* data,
        uint byteCount,
        DescriptorHeapPushDataPayload? payload,
        out string reason)
    {
        reason = string.Empty;
        VulkanDescriptorHeapState heap = Runtime.ResourceRuntime.Descriptors.Heap;
        if (heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap ||
            heap.NativeFunctions is null || !heap.SamplerStorage.IsReady ||
            !heap.ResourceStorage.IsReady)
        {
            reason = "descriptor heap state is not active and ready";
            return false;
        }
        ulong maxPushDataSize = heap.Properties.MaxPushDataSize;
        if (data is null || byteCount == 0 || maxPushDataSize == 0 ||
            (offset & 3u) != 0 || (byteCount & 3u) != 0 ||
            offset > maxPushDataSize || byteCount > maxPushDataSize - offset)
        {
            reason = "descriptor heap push-data payload is invalid";
            return false;
        }

        if (payload is not null &&
            !payload.TryTrackResourceGenerations(this, commandBuffer, out reason))
        {
            return false;
        }
        if (!Runtime.TryEnsureDescriptorHeapsBound(commandBuffer, out reason))
            return false;
        PushDataInfoEXTNative push = new()
        {
            SType = VulkanDescriptorHeapExt.PushDataInfoSType,
            Offset = offset,
            Data = new HostAddressRangeConstEXTNative { Address = data, Size = byteCount },
        };
        heap.NativeFunctions.CmdPushData(commandBuffer, &push);
        Runtime.NotifyDescriptorHeapDataPushed(commandBuffer);
        return true;
    }

    private bool TryTrackDescriptorHeapResources(
        CommandBuffer commandBuffer,
        ReadOnlySpan<VulkanPinnedResourceGeneration> resourceGenerations,
        out string reason)
    {
        for (int index = 0; index < resourceGenerations.Length; index++)
        {
            VulkanPinnedResourceGeneration expected = resourceGenerations[index];
            ulong actual = Runtime.GetResourceGeneration(expected.Key.Type, expected.Key.Handle);
            if (actual == 0 || actual != expected.Generation)
            {
                reason = $"descriptor heap dependency {expected.Key} generation changed (expected={expected.Generation}, actual={actual}).";
                return false;
            }
            Track(commandBuffer, expected.Key.Type, expected.Key.Handle, expected.Generation);
        }

        reason = string.Empty;
        return true;
    }

    internal bool TryAppendDescriptorHeapInheritance(
        ref CommandBufferInheritanceInfo inheritanceInfo,
        CommandBufferInheritanceDescriptorHeapInfoEXTNative* heapInfo,
        BindHeapInfoEXTNative* samplerHeapInfo,
        BindHeapInfoEXTNative* resourceHeapInfo,
        out DescriptorHeapBindingIdentity identity)
    {
        identity = default;
        VulkanDescriptorHeapState heap = Runtime.ResourceRuntime.Descriptors.Heap;
        if (heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap ||
            heap.NativeFunctions is null || !heap.SamplerStorage.IsReady ||
            !heap.ResourceStorage.IsReady)
        {
            return false;
        }

        identity = Runtime.CaptureDescriptorHeapBindingIdentity();
        if (!identity.IsComplete)
            return false;

        *samplerHeapInfo = CreateSamplerHeapBindInfo(in identity);
        *resourceHeapInfo = CreateResourceHeapBindInfo(in identity);
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

    private static BindHeapInfoEXTNative CreateSamplerHeapBindInfo(in DescriptorHeapBindingIdentity identity)
        => new()
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            HeapRange = new DeviceAddressRangeEXTNative
            {
                Address = identity.SamplerAddress,
                Size = identity.SamplerSize,
            },
            ReservedRangeOffset = identity.SamplerReservedOffset,
            ReservedRangeSize = identity.SamplerReservedSize,
        };

    private static BindHeapInfoEXTNative CreateResourceHeapBindInfo(in DescriptorHeapBindingIdentity identity)
        => new()
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            HeapRange = new DeviceAddressRangeEXTNative
            {
                Address = identity.ResourceAddress,
                Size = identity.ResourceSize,
            },
            ReservedRangeOffset = identity.ResourceReservedOffset,
            ReservedRangeSize = identity.ResourceReservedSize,
        };

    internal static void BindDescriptorHeaps(
        CommandBuffer commandBuffer,
        VulkanDescriptorHeapState heap,
        in DescriptorHeapBindingIdentity identity)
    {
        BindHeapInfoEXTNative samplerHeap = new()
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            HeapRange = new DeviceAddressRangeEXTNative { Address = identity.SamplerAddress, Size = identity.SamplerSize },
            ReservedRangeOffset = identity.SamplerReservedOffset,
            ReservedRangeSize = identity.SamplerReservedSize,
        };
        BindHeapInfoEXTNative resourceHeap = new()
        {
            SType = VulkanDescriptorHeapExt.BindHeapInfoSType,
            HeapRange = new DeviceAddressRangeEXTNative { Address = identity.ResourceAddress, Size = identity.ResourceSize },
            ReservedRangeOffset = identity.ResourceReservedOffset,
            ReservedRangeSize = identity.ResourceReservedSize,
        };
        heap.NativeFunctions!.CmdBindSamplerHeap(commandBuffer, &samplerHeap);
        heap.NativeFunctions.CmdBindResourceHeap(commandBuffer, &resourceHeap);
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
        if (!Runtime.DeviceContext.MutableCapabilities._supportsDynamicRenderingLocalRead || !signature.Enabled)
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

    internal void SetViewportScissor(CommandBuffer commandBuffer, ReadOnlySpan<Viewport> viewports, ReadOnlySpan<Rect2D> scissors, uint count)
    {
        fixed (Viewport* viewportsPtr = viewports)
        fixed (Rect2D* scissorsPtr = scissors)
        {
            Api.CmdSetViewport(commandBuffer, 0, count, viewportsPtr);
            Api.CmdSetScissor(commandBuffer, 0, count, scissorsPtr);
        }
    }
}
