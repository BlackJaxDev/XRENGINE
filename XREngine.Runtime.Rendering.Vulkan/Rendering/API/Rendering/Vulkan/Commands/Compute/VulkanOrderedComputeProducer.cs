using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Builds frozen ordered-compute operations without consulting a renderer facade.</summary>
internal static unsafe class VulkanOrderedComputeProducer
{
    internal static bool Supports(
        VulkanDeviceContext deviceContext,
        VulkanCommandRuntime commandRuntime)
        => deviceContext.IsOperational &&
           deviceContext.GraphicsQueue.Handle != 0 &&
           commandRuntime.Synchronization._graphicsTimelineSemaphore.Handle != 0 &&
           deviceContext.QueueFamilies.GraphicsFamilySupportsCompute;

    internal static ERendererComputeEnqueueStatus TryCreateIndirectDispatch(
        VkRenderProgram program,
        VkDataBuffer argumentOwner,
        Buffer argumentBuffer,
        ulong byteOffset,
        int passIndex,
        string label,
        in FrameOpContext context,
        out ComputeDispatchIndirectOp? operation)
    {
        operation = null;
        const ulong commandSize = sizeof(uint) * 3UL;
        if (byteOffset > argumentOwner.AllocatedByteSize ||
            commandSize > argumentOwner.AllocatedByteSize - byteOffset)
        {
            return ERendererComputeEnqueueStatus.InvalidResource;
        }

        ComputeDispatchSnapshot snapshot = program.CaptureComputeSnapshot();
        if (!program.ValidateComputeSnapshot(snapshot, out _))
            return ERendererComputeEnqueueStatus.DescriptorInvalid;

        // The sealed frame-plan preparation authority owns native pipeline
        // readiness. Preserve this immutable operation while compilation is
        // pending so it can be retried rather than silently dropped here.

        operation = new ComputeDispatchIndirectOp(
            passIndex,
            program,
            snapshot,
            argumentOwner,
            argumentBuffer,
            byteOffset,
            label,
            context);
        return ERendererComputeEnqueueStatus.Enqueued;
    }

    internal static ERendererComputeEnqueueStatus TryCreateBufferCopy(
        VkDataBuffer sourceOwner,
        Buffer sourceBuffer,
        ulong sourceOffset,
        VkDataBuffer destinationOwner,
        Buffer destinationBuffer,
        ulong destinationOffset,
        ulong byteCount,
        int passIndex,
        string label,
        bool requireGpuWriteVisibility,
        GpuDiagnosticSnapshotReceipt? diagnosticReceipt,
        in FrameOpContext context,
        out BufferCopyOp? operation)
    {
        operation = null;
        if (!IsRangeValid(sourceOwner.AllocatedByteSize, sourceOffset, byteCount) ||
            !IsRangeValid(destinationOwner.AllocatedByteSize, destinationOffset, byteCount) ||
            sourceBuffer.Handle == destinationBuffer.Handle &&
            sourceOffset < destinationOffset + byteCount &&
            destinationOffset < sourceOffset + byteCount)
        {
            return ERendererComputeEnqueueStatus.InvalidResource;
        }

        operation = new BufferCopyOp(
            passIndex,
            sourceOwner,
            sourceBuffer,
            sourceOffset,
            destinationOwner,
            destinationBuffer,
            destinationOffset,
            byteCount,
            requireGpuWriteVisibility,
            diagnosticReceipt,
            label,
            context);
        return ERendererComputeEnqueueStatus.Enqueued;
    }

    private static bool IsRangeValid(ulong capacity, ulong offset, ulong count)
        => offset <= capacity && count <= capacity - offset;
}
