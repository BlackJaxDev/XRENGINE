using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>Owns frame-local admission and context capture for legacy renderer command translations.</summary>
internal sealed partial class VulkanFrameLoop
{
    internal void EnqueueIndirectDraw(string operationName, uint drawCount, uint stride, nuint byteOffset)
        => _commandRuntime.EnqueueIndirectDraw(_frameOperationQueue, operationName, drawCount, stride, byteOffset, 0, false, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContext());

    internal void EnqueueIndirectCountDraw(uint maxDrawCount, uint stride, nuint byteOffset, nuint countByteOffset)
        => _commandRuntime.EnqueueIndirectCountDraw(_frameOperationQueue, maxDrawCount, stride, byteOffset, countByteOffset, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContext());

    internal bool TryEnqueueQueryOperation(XRRenderQuery query, ERenderQueryOperation operation)
        => query.Descriptor.Kind == ERenderQueryKind.Occlusion && _commandRuntime.TryEnqueueQueryOperation(_frameOperationQueue, RuntimeEngine.Rendering.State.CurrentRenderingPipeline is not null, _resourceRuntime.WrapperLookup.GetOrCreate(query) as VkRenderQuery, query.Descriptor, operation, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContext());

    internal ERenderQueryReadStatus TryWriteTimestamp(XRRenderQuery query)
        => query.Descriptor.Kind is ERenderQueryKind.Timestamp or ERenderQueryKind.ElapsedTime && _commandRuntime.TryEnqueueQueryOperation(_frameOperationQueue, RuntimeEngine.Rendering.State.CurrentRenderingPipeline is not null, _resourceRuntime.WrapperLookup.GetOrCreate(query) as VkRenderQuery, query.Descriptor, ERenderQueryOperation.WriteTimestamp, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContext()) ? ERenderQueryReadStatus.Ready : ERenderQueryReadStatus.InvalidState;

    internal void PublishFrameBufferAttachmentsForSampling(XRFrameBuffer frameBuffer)
    {
        ArgumentNullException.ThrowIfNull(frameBuffer);
        if (_commandRuntime.TryGetLastFrameOperation(_frameOperationQueue, frameBuffer, out FrameOp lastWriter))
        {
            _commandRuntime.EnqueueFrameOperation(_frameOperationQueue, VulkanCommandRuntime.CreatePublishFramebufferOperation(lastWriter.PassIndex, frameBuffer, lastWriter.Context), lastWriter.PassIndex);
            return;
        }

        FrameOpContext context = CaptureFrameOpContextOrLastActive();
        int passIndex = VulkanCommandRuntime.EnsureValidPassIndex(RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, "PublishFrameBufferAttachmentsForSampling", context.PassMetadata);
        _commandRuntime.EnqueueFrameOperation(_frameOperationQueue, VulkanCommandRuntime.CreatePublishFramebufferOperation(passIndex, frameBuffer, context), passIndex);
    }
}
