using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command-owned lowering of resolved ordered-compute API inputs.</summary>
internal sealed partial class VulkanCommandRuntime
{
    internal bool SupportsOrderedComputeWork
        => VulkanOrderedComputeProducer.Supports(DeviceContext, this);

    internal ERendererComputeEnqueueStatus TryEnqueueIndirectComputeDispatch(
        VulkanWrapperLookupPort wrapperLookup,
        VulkanFrameOperationQueue queue,
        XRRenderProgram program,
        XRDataBuffer arguments,
        nint byteOffset,
        string label,
        int currentPassIndex,
        in FrameOpContext context,
        bool allowSynchronousUpload,
        bool isDeviceLost)
    {
        if (!SupportsOrderedComputeWork)
            return isDeviceLost
                ? ERendererComputeEnqueueStatus.DeviceLost
                : ERendererComputeEnqueueStatus.Unsupported;
        if (program is null || arguments is null || byteOffset < 0 || ((ulong)byteOffset & 3UL) != 0)
            return ERendererComputeEnqueueStatus.InvalidResource;
        if (wrapperLookup.GetOrCreate(program) is not VkRenderProgram vkProgram)
            return ERendererComputeEnqueueStatus.InvalidResource;

        vkProgram.Generate();
        if (!vkProgram.Link(program.AllowAsyncBackendCompile))
            return ERendererComputeEnqueueStatus.ProgramPending;
        if (!TryGetComputeBuffer(
                wrapperLookup,
                arguments,
                BufferUsageFlags.IndirectBufferBit,
                allowSynchronousUpload,
                out VkDataBuffer argumentOwner,
                out Buffer argumentBuffer))
            return ERendererComputeEnqueueStatus.InvalidResource;

        int passIndex = ResolveOrderedPrimaryWorkPassIndex(
            label,
            currentPassIndex,
            context.PassMetadata);
        return passIndex == int.MinValue
            ? ERendererComputeEnqueueStatus.NoPassContext
            : EnqueueIndirectComputeDispatch(
                queue,
                vkProgram,
                argumentOwner,
                argumentBuffer,
                (ulong)byteOffset,
                passIndex,
                label,
                context);
    }

    internal ERendererComputeEnqueueStatus TryEnqueueBufferCopy(
        VulkanWrapperLookupPort wrapperLookup,
        VulkanFrameOperationQueue queue,
        XRDataBuffer source,
        nint sourceOffset,
        XRDataBuffer destination,
        nint destinationOffset,
        nuint byteCount,
        string label,
        bool requireGpuWriteVisibility,
        GpuDiagnosticSnapshotReceipt? diagnosticReceipt,
        int currentPassIndex,
        in FrameOpContext context,
        bool allowSynchronousUpload,
        bool isDeviceLost)
    {
        if (!SupportsOrderedComputeWork)
            return isDeviceLost
                ? ERendererComputeEnqueueStatus.DeviceLost
                : ERendererComputeEnqueueStatus.Unsupported;
        if (source is null || destination is null || sourceOffset < 0 || destinationOffset < 0 || byteCount == 0)
            return ERendererComputeEnqueueStatus.InvalidResource;
        if (!TryGetComputeBuffer(
                wrapperLookup,
                source,
                BufferUsageFlags.TransferSrcBit,
                allowSynchronousUpload,
                out VkDataBuffer sourceOwner,
                out Buffer sourceBuffer)
            || !TryGetComputeBuffer(
                wrapperLookup,
                destination,
                BufferUsageFlags.TransferDstBit,
                allowSynchronousUpload,
                out VkDataBuffer destinationOwner,
                out Buffer destinationBuffer))
            return ERendererComputeEnqueueStatus.InvalidResource;

        int passIndex = ResolveOrderedPrimaryWorkPassIndex(
            label,
            currentPassIndex,
            context.PassMetadata);
        return passIndex == int.MinValue
            ? ERendererComputeEnqueueStatus.NoPassContext
            : EnqueueBufferCopy(
                queue,
                sourceOwner,
                sourceBuffer,
                (ulong)sourceOffset,
                destinationOwner,
                destinationBuffer,
                (ulong)destinationOffset,
                (ulong)byteCount,
                passIndex,
                label,
                requireGpuWriteVisibility,
                diagnosticReceipt,
                context);
    }

    internal ERendererComputeEnqueueStatus TryEnqueueOrderedComputeBarrier(
        VulkanFrameOperationQueue queue,
        EMemoryBarrierMask mask,
        string label,
        int currentPassIndex,
        in FrameOpContext context,
        bool isDeviceLost)
    {
        if (mask == EMemoryBarrierMask.None)
            return ERendererComputeEnqueueStatus.Enqueued;
        if (!SupportsOrderedComputeWork)
            return isDeviceLost
                ? ERendererComputeEnqueueStatus.DeviceLost
                : ERendererComputeEnqueueStatus.Unsupported;

        int passIndex = ResolveOrderedPrimaryWorkPassIndex(
            label,
            currentPassIndex,
            context.PassMetadata);
        if (passIndex == int.MinValue)
            return ERendererComputeEnqueueStatus.NoPassContext;

        EnqueueOrderedComputeBarrier(queue, passIndex, mask, context);
        return ERendererComputeEnqueueStatus.Enqueued;
    }

    internal XRGpuFence? TryEnqueueOrderedComputeFence(
        VulkanFrameOperationQueue queue,
        int currentPassIndex,
        in FrameOpContext context)
    {
        if (!SupportsOrderedComputeWork)
            return null;

        int passIndex = ResolveOrderedPrimaryWorkPassIndex(
            "SubmissionMarker",
            currentPassIndex,
            context.PassMetadata);
        return passIndex == int.MinValue
            ? null
            : EnqueueOrderedComputeFence(queue, passIndex, context);
    }

    internal bool TryEnsureComputeBufferReady(
        VulkanWrapperLookupPort wrapperLookup,
        XRDataBuffer buffer,
        bool allowSynchronousUpload)
        => TryGetComputeBuffer(
            wrapperLookup,
            buffer,
            BufferUsageFlags.StorageBufferBit,
            allowSynchronousUpload,
            out _,
            out _);

    /// <summary>Publishes a validated mesh-task indirect-count operation into the immutable frame stream.</summary>
    internal bool TryEnqueueMeshTaskIndirectCount(
        VulkanWrapperLookupPort wrapperLookup,
        VulkanDescriptorManager descriptors,
        VulkanFrameOperationQueue queue,
        XRRenderProgram program,
        XRDataBuffer indirect,
        XRDataBuffer count,
        uint maxDrawCount,
        uint stride,
        nuint byteOffset,
        nuint countByteOffset,
        int currentPassIndex,
        in FrameOpContext context,
        bool allowSynchronousUpload,
        out string failureReason)
    {
        failureReason = string.Empty;
        if (!DeviceContext.SupportsMeshTaskIndirectCount)
        {
            failureReason = "VK_EXT_mesh_shader indirect-count dispatch is unavailable.";
            return false;
        }
        if (program is null || indirect is null || count is null || maxDrawCount == 0 || stride == 0 ||
            ((ulong)byteOffset & 3UL) != 0 || ((ulong)countByteOffset & 3UL) != 0)
        {
            failureReason = "Mesh-task indirect-count dispatch has invalid buffers or alignment.";
            return false;
        }
        if (wrapperLookup.GetOrCreate(program, generateNow: false) is not VkRenderProgram vkProgram ||
            !vkProgram.IsLinked || vkProgram.PipelineLayout.Handle == 0)
        {
            failureReason = "Mesh-task graphics program is not linked with a Vulkan pipeline layout.";
            return false;
        }
        if (!TryGetComputeBuffer(wrapperLookup, indirect, BufferUsageFlags.IndirectBufferBit, allowSynchronousUpload, out VkDataBuffer indirectOwner, out _) ||
            !TryGetComputeBuffer(wrapperLookup, count, BufferUsageFlags.IndirectBufferBit, allowSynchronousUpload, out VkDataBuffer countOwner, out _))
        {
            failureReason = "Mesh-task indirect-count buffers are not ready for indirect use.";
            return false;
        }

        int passIndex = ResolveOrderedPrimaryWorkPassIndex(
            "MeshTaskDispatchIndirectCount",
            currentPassIndex,
            context.PassMetadata);
        if (passIndex == int.MinValue)
        {
            failureReason = "Mesh-task indirect-count dispatch has no valid render pass context.";
            return false;
        }

        VulkanMeshProducerSnapshot producer = CaptureIndirectProducerSnapshot(ResolveCurrentDrawTarget());
        VulkanBindlessMaterialDescriptorBinding? bindlessMaterialTextures =
            descriptors.CaptureGlobalMaterialTextureDescriptorBindingForNextFrameOp();
        if (bindlessMaterialTextures is { } binding &&
            !ReferenceEquals(binding.Program, vkProgram))
        {
            failureReason = "The active bindless material descriptor scope belongs to a different mesh-task program.";
            return false;
        }

        ComputeDispatchSnapshot programBindingSnapshot =
            vkProgram.CaptureComputeSnapshot();
        programBindingSnapshot.SetMaterialTablePublication(bindlessMaterialTextures?.Publication);
        if (!vkProgram.ValidateComputeSnapshot(
                programBindingSnapshot,
                out string? bindingFailure))
        {
            failureReason =
                $"Mesh-task program bindings are incomplete: {bindingFailure ?? "unknown binding failure"}.";
            return false;
        }
        programBindingSnapshot = programBindingSnapshot.CreateSealedCopy();

        MeshTaskDispatchIndirectCountOp operation =
            // Mesh-task work is recorded later, after the render pass has moved on.
            // Capture the material table now, while the caller's program/binding is
            // still authoritative, exactly as traditional indirect draws do. The
            // primary-plan admission phase resolves the exact target signature and
            // replaces this empty pipeline before command-buffer recording begins.
            new(passIndex, vkProgram, vkProgram.LinkGeneration, programBindingSnapshot, producer, default, indirectOwner, countOwner, maxDrawCount, stride, byteOffset, countByteOffset, bindlessMaterialTextures, context);
        operation.OwnAuthoringSnapshot(programBindingSnapshot);
        try
        {
            queue.EnqueuePrepared(VulkanFrameOperationSemantics.Prepare(operation, passIndex));
        }
        catch
        {
            operation.ReleaseAuthoringSnapshot();
            throw;
        }
        return true;
    }

    internal unsafe bool TryReadMappedBuffer(
        VulkanWrapperLookupPort wrapperLookup,
        XRDataBuffer buffer,
        Span<byte> destination)
    {
        if (destination.IsEmpty)
            return true;
        if (wrapperLookup.GetOrCreate(buffer) is not VkDataBuffer vkBuffer
            || vkBuffer.BufferHandle is not { } handle
            || vkBuffer.MemoryHandle is not { } memory
            || handle.Handle == 0
            || memory.Handle == 0
            || (ulong)destination.Length > vkBuffer.AllocatedByteSize)
            return false;

        VulkanBackendObjectContext backendContext = RequireBackendObjectContext();
        if (!ResourceRuntime.Buffers.TryCreateMappedSlice(
                backendContext, handle, memory, 0, (ulong)destination.Length, out VulkanMappedMemorySlice slice) ||
            !ResourceRuntime.Buffers.TryAcquireRead(backendContext, in slice, out VulkanMappedMemoryReadLease lease))
            return false;

        using (lease)
        {
            lease.Bytes.CopyTo(destination);
            return true;
        }
    }

    internal ERendererComputeEnqueueStatus EnqueueIndirectComputeDispatch(
        VulkanFrameOperationQueue queue, VkRenderProgram program, VkDataBuffer argumentsOwner,
        Buffer arguments, ulong offset, int passIndex, string label, in FrameOpContext context)
    {
        ERendererComputeEnqueueStatus status = VulkanOrderedComputeProducer.TryCreateIndirectDispatch(
            program, argumentsOwner, arguments, offset, passIndex, label, context,
            out ComputeDispatchIndirectOp? operation);
        if (operation is not null)
            queue.EnqueuePrepared(VulkanFrameOperationSemantics.Prepare(operation, passIndex));
        return status;
    }

    internal ERendererComputeEnqueueStatus EnqueueBufferCopy(
        VulkanFrameOperationQueue queue, VkDataBuffer sourceOwner, Buffer source, ulong sourceOffset,
        VkDataBuffer destinationOwner, Buffer destination, ulong destinationOffset, ulong byteCount,
        int passIndex, string label, bool requireGpuWriteVisibility,
        GpuDiagnosticSnapshotReceipt? diagnosticReceipt, in FrameOpContext context)
    {
        ERendererComputeEnqueueStatus status = VulkanOrderedComputeProducer.TryCreateBufferCopy(
            sourceOwner, source, sourceOffset, destinationOwner, destination, destinationOffset,
            byteCount, passIndex, label, requireGpuWriteVisibility, diagnosticReceipt, context,
            out BufferCopyOp? operation);
        if (operation is not null)
            queue.EnqueuePrepared(VulkanFrameOperationSemantics.Prepare(operation, passIndex));
        return status;
    }

    internal void EnqueueOrderedComputeBarrier(
        VulkanFrameOperationQueue queue, int passIndex, EMemoryBarrierMask mask, in FrameOpContext context)
        => queue.EnqueuePrepared(VulkanFrameOperationSemantics.Prepare(
            CreateMemoryBarrierOperation(passIndex, mask, context), passIndex));

    internal VulkanTimelineGpuFence EnqueueOrderedComputeFence(
        VulkanFrameOperationQueue queue, int passIndex, in FrameOpContext context)
    {
        VulkanTimelineGpuFence fence = RentTimelineGpuFence();
        queue.EnqueuePrepared(VulkanFrameOperationSemantics.Prepare(
            new SubmissionMarkerOp(passIndex, fence, "SubmissionMarker", context), passIndex));
        return fence;
    }

    private static bool TryGetComputeBuffer(
        VulkanWrapperLookupPort wrapperLookup,
        XRDataBuffer data,
        BufferUsageFlags requiredUsage,
        bool allowSynchronousUpload,
        out VkDataBuffer owner,
        out Buffer buffer)
    {
        owner = null!;
        buffer = default;
        if (wrapperLookup.GetOrCreate(data, allowSynchronousUpload) is not VkDataBuffer vkBuffer)
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

    internal static int ResolveOrderedPrimaryWorkPassIndex(
        string operationName,
        int currentPassIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        int passIndex = currentPassIndex == int.MinValue
            ? (int)EDefaultRenderPass.PreRender
            : currentPassIndex;
        return EnsureValidPassIndex(passIndex, operationName, passMetadata);
    }
}
