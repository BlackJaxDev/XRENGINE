using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Ordered backend operations shared by GPU compute clients.</summary>
public unsafe partial class VulkanRenderer
{
    /// <summary>Whether the active device and graphics queue can accept compute frame work.</summary>
    public bool SupportsOrderedComputeWork
        => IsDeviceOperational
        && graphicsQueue.Handle != 0
        && _graphicsTimelineSemaphore.Handle != 0
        && FamilyQueueIndices.GraphicsFamilySupportsCompute;

    public ERendererComputeEnqueueStatus TryDispatchComputeIndirect(
        XRRenderProgram program,
        XRDataBuffer arguments,
        nint byteOffset,
        string label)
    {
        if (!SupportsOrderedComputeWork)
            return IsDeviceLost ? ERendererComputeEnqueueStatus.DeviceLost : ERendererComputeEnqueueStatus.Unsupported;
        if (program is null || arguments is null || byteOffset < 0 || ((ulong)byteOffset & 3UL) != 0)
            return ERendererComputeEnqueueStatus.InvalidResource;
        if (GetOrCreateAPIRenderObject(program) is not VkRenderProgram vkProgram)
            return ERendererComputeEnqueueStatus.InvalidResource;

        vkProgram.Generate();
        if (!vkProgram.Link(program.AllowAsyncBackendCompile))
            return ERendererComputeEnqueueStatus.ProgramPending;
        if (!TryGetComputeBuffer(
                arguments,
                BufferUsageFlags.IndirectBufferBit,
                out VkDataBuffer argumentOwner,
                out Buffer argumentBuffer))
            return ERendererComputeEnqueueStatus.InvalidResource;

        const ulong commandSize = sizeof(uint) * 3UL;
        ulong offset = (ulong)byteOffset;
        if (offset > argumentOwner.AllocatedByteSize || commandSize > argumentOwner.AllocatedByteSize - offset)
            return ERendererComputeEnqueueStatus.InvalidResource;

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        int passIndex = ResolveOrderedPrimaryWorkPassIndex(label, context.PassMetadata);
        if (passIndex == int.MinValue)
            return ERendererComputeEnqueueStatus.NoPassContext;

        ComputeDispatchSnapshot snapshot = vkProgram.CaptureComputeSnapshot();
        if (!vkProgram.ValidateComputeSnapshot(snapshot, out _))
            return ERendererComputeEnqueueStatus.DescriptorInvalid;
        try
        {
            if (vkProgram.GetOrCreateComputePipeline(passIndex, context.PassMetadata).Handle == 0)
                return ERendererComputeEnqueueStatus.ProgramPending;
        }
        catch
        {
            return ERendererComputeEnqueueStatus.ProgramPending;
        }

        EnqueueFrameOp(new ComputeDispatchIndirectOp(
            passIndex,
            vkProgram,
            snapshot,
            argumentOwner,
            argumentBuffer,
            offset,
            label,
            context));
        return ERendererComputeEnqueueStatus.Enqueued;
    }

    public ERendererComputeEnqueueStatus TryEnqueueBufferCopy(
        XRDataBuffer source,
        nint sourceOffset,
        XRDataBuffer destination,
        nint destinationOffset,
        nuint byteCount,
        string label)
    {
        if (!SupportsOrderedComputeWork)
            return IsDeviceLost ? ERendererComputeEnqueueStatus.DeviceLost : ERendererComputeEnqueueStatus.Unsupported;
        if (source is null || destination is null || sourceOffset < 0 || destinationOffset < 0 || byteCount == 0)
            return ERendererComputeEnqueueStatus.InvalidResource;
        if (!TryGetComputeBuffer(source, BufferUsageFlags.TransferSrcBit, out VkDataBuffer sourceOwner, out Buffer sourceBuffer)
            || !TryGetComputeBuffer(destination, BufferUsageFlags.TransferDstBit, out VkDataBuffer destinationOwner, out Buffer destinationBuffer))
            return ERendererComputeEnqueueStatus.InvalidResource;

        ulong sourceStart = (ulong)sourceOffset;
        ulong destinationStart = (ulong)destinationOffset;
        ulong count = (ulong)byteCount;
        if (!IsBufferRangeValid(sourceOwner.AllocatedByteSize, sourceStart, count)
            || !IsBufferRangeValid(destinationOwner.AllocatedByteSize, destinationStart, count))
            return ERendererComputeEnqueueStatus.InvalidResource;
        if (sourceBuffer.Handle == destinationBuffer.Handle
            && sourceStart < destinationStart + count
            && destinationStart < sourceStart + count)
            return ERendererComputeEnqueueStatus.InvalidResource;

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        int passIndex = ResolveOrderedPrimaryWorkPassIndex(label, context.PassMetadata);
        if (passIndex == int.MinValue)
            return ERendererComputeEnqueueStatus.NoPassContext;

        EnqueueFrameOp(new BufferCopyOp(
            passIndex,
            sourceOwner,
            sourceBuffer,
            sourceStart,
            destinationOwner,
            destinationBuffer,
            destinationStart,
            count,
            label,
            context));
        return ERendererComputeEnqueueStatus.Enqueued;
    }

    /// <summary>
    /// Enqueues a compute dependency in the same ordered primary-command stream
    /// as direct dispatches, indirect dispatches, copies, and submission markers.
    /// </summary>
    public ERendererComputeEnqueueStatus TryCompleteOrderedComputePass(
        EMemoryBarrierMask mask,
        string label)
    {
        if (mask == EMemoryBarrierMask.None)
            return ERendererComputeEnqueueStatus.Enqueued;
        if (!SupportsOrderedComputeWork)
            return IsDeviceLost ? ERendererComputeEnqueueStatus.DeviceLost : ERendererComputeEnqueueStatus.Unsupported;

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        int passIndex = ResolveOrderedPrimaryWorkPassIndex(label, context.PassMetadata);
        if (passIndex == int.MinValue)
            return ERendererComputeEnqueueStatus.NoPassContext;

        EnqueueFrameOp(new MemoryBarrierOp(passIndex, mask, context));
        return ERendererComputeEnqueueStatus.Enqueued;
    }

    public override XRGpuFence? InsertGpuFence()
    {
        if (!SupportsOrderedComputeWork)
            return null;

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        int passIndex = ResolveOrderedPrimaryWorkPassIndex("SubmissionMarker", context.PassMetadata);
        if (passIndex == int.MinValue)
            return null;

        VulkanTimelineGpuFence fence = RentTimelineGpuFence();
        EnqueueFrameOp(new SubmissionMarkerOp(passIndex, fence, "SubmissionMarker", context));
        return fence;
    }

    public bool TryEnsureComputeBufferReady(XRDataBuffer buffer)
        => TryGetComputeBuffer(buffer, BufferUsageFlags.StorageBufferBit, out _, out _);

    public bool TryReadMappedBuffer(XRDataBuffer buffer, Span<byte> destination)
    {
        if (destination.IsEmpty)
            return true;
        if (GetOrCreateAPIRenderObject(buffer, generateNow: false) is not VkDataBuffer vkBuffer
            || vkBuffer.BufferHandle is not { } handle
            || vkBuffer.MemoryHandle is not { } memory
            || handle.Handle == 0
            || memory.Handle == 0
            || (ulong)destination.Length > vkBuffer.AllocatedByteSize)
            return false;

        if (!TryMapReadbackMemory(handle, memory, 0, (ulong)destination.Length, out void* mapped))
            return false;

        try
        {
            new ReadOnlySpan<byte>(mapped, destination.Length).CopyTo(destination);
            return true;
        }
        finally
        {
            UnmapBufferMemory(handle, memory);
        }
    }

    private bool TryGetComputeBuffer(
        XRDataBuffer data,
        BufferUsageFlags requiredUsage,
        out VkDataBuffer owner,
        out Buffer buffer)
    {
        owner = null!;
        buffer = default;
        bool allowSynchronousUpload = AllowSynchronousResourceUploads;
        if (GetOrCreateAPIRenderObject(data, generateNow: allowSynchronousUpload) is not VkDataBuffer vkBuffer)
            return false;

        vkBuffer.EnsureStorageAllocatedForGpuUse();
        if (!vkBuffer.TryEnsureReadyForRendering(allowSynchronousUpload)
            || vkBuffer.BufferHandle is not { } handle
            || handle.Handle == 0
            || (vkBuffer.LastUsageFlags & requiredUsage) != requiredUsage)
            return false;

        owner = vkBuffer;
        buffer = handle;
        return true;
    }

    private static bool IsBufferRangeValid(ulong capacity, ulong offset, ulong count)
        => offset <= capacity && count <= capacity - offset;

    /// <summary>
    /// Resolves ordered primary-command-buffer work to the active pass, or to the
    /// explicit pre-render bucket when it is submitted outside a render pass.
    /// Compute, transfer, and marker operations are intentionally legal between
    /// render passes; <see cref="int.MinValue"/> only means that no pass is active.
    /// </summary>
    private int ResolveOrderedPrimaryWorkPassIndex(
        string opName,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex == int.MinValue)
            passIndex = (int)EDefaultRenderPass.PreRender;

        return EnsureValidPassIndex(passIndex, opName, passMetadata);
    }

    private void RecordComputeDispatchIndirectOp(
        CommandBuffer commandBuffer,
        uint imageIndex,
        ComputeDispatchIndirectOp op)
    {
        if (!op.Program.Link())
            throw new InvalidOperationException($"Compute program '{op.Program.Data.Name ?? "UnnamedProgram"}' is not ready.");

        Pipeline pipeline = op.Program.GetOrCreateComputePipeline(op.PassIndex, op.Context.PassMetadata);
        if (pipeline.Handle == 0)
            throw new InvalidOperationException($"Compute pipeline '{op.Program.Data.Name ?? "UnnamedProgram"}' is unavailable.");

        BindPipelineTracked(commandBuffer, PipelineBindPoint.Compute, pipeline);
        EnsureComputeStorageImageLayoutsForDispatch(commandBuffer, op.Snapshot);
        PushConstantsTracked(
            commandBuffer,
            op.Program.PipelineLayout,
            CommonPushConstantStageFlags,
            0,
            new ComputeDispatchPushConstants(0u, 0u, 0u, 0u));

        if (!op.Program.TryBuildAndBindComputeDescriptorSets(commandBuffer, imageIndex, op.Snapshot, 0, out _, out var tempBuffers))
        {
            foreach ((Buffer buffer, DeviceMemory memory) in tempBuffers)
                DestroyBuffer(buffer, memory);

            throw new InvalidOperationException(
                $"Descriptor binding failed for indirect compute program '{op.Program.Data.Name ?? "UnnamedProgram"}'.");
        }

        RegisterComputeTransientUniformBuffers(imageIndex, tempBuffers);
        TrackVulkanCommandBufferResource(
            commandBuffer,
            ObjectType.Buffer,
            op.ArgumentBuffer.Handle,
            $"{op.Label}.Arguments");
        Api!.CmdDispatchIndirect(commandBuffer, op.ArgumentBuffer, op.ArgumentOffset);
    }

    private void RecordBufferCopyOp(CommandBuffer commandBuffer, BufferCopyOp op)
    {
        BufferCopy copy = new()
        {
            SrcOffset = op.SourceOffset,
            DstOffset = op.DestinationOffset,
            Size = op.ByteCount,
        };
        CmdCopyBufferTracked(commandBuffer, op.SourceBuffer, op.DestinationBuffer, 1, &copy);
    }
}
