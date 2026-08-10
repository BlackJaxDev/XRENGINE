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
        => VulkanOrderedComputeProducer.Supports(_deviceContext, _commandRuntime);

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

        ulong offset = (ulong)byteOffset;
        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        int passIndex = ResolveOrderedPrimaryWorkPassIndex(label, context.PassMetadata);
        if (passIndex == int.MinValue)
            return ERendererComputeEnqueueStatus.NoPassContext;

        ERendererComputeEnqueueStatus status = VulkanOrderedComputeProducer.TryCreateIndirectDispatch(
            vkProgram,
            argumentOwner,
            argumentBuffer,
            offset,
            passIndex,
            label,
            context,
            out ComputeDispatchIndirectOp? operation);
        if (operation is not null)
            EnqueueFrameOp(operation);
        return status;
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
        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        int passIndex = ResolveOrderedPrimaryWorkPassIndex(label, context.PassMetadata);
        if (passIndex == int.MinValue)
            return ERendererComputeEnqueueStatus.NoPassContext;

        ERendererComputeEnqueueStatus status = VulkanOrderedComputeProducer.TryCreateBufferCopy(
            sourceOwner,
            sourceBuffer,
            sourceStart,
            destinationOwner,
            destinationBuffer,
            destinationStart,
            count,
            passIndex,
            label,
            context,
            out BufferCopyOp? operation);
        if (operation is not null)
            EnqueueFrameOp(operation);
        return status;
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

        EnqueueFrameOp(MemoryBarrierOp.Rent(passIndex, mask, context));
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

}
