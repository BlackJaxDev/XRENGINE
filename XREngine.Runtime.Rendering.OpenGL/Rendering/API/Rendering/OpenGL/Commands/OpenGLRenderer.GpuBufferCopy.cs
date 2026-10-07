namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    public override ERendererComputeEnqueueStatus TryEnqueueGpuBufferCopy(
        XRDataBuffer source,
        nint sourceOffset,
        XRDataBuffer destination,
        nint destinationOffset,
        nuint byteCount,
        string label)
    {
        if (!RuntimeEngine.IsRenderThread)
            return ERendererComputeEnqueueStatus.NoPassContext;

        if (source is null || destination is null ||
            sourceOffset < 0 || destinationOffset < 0 || byteCount == 0)
            return ERendererComputeEnqueueStatus.InvalidResource;

        ulong sourceStart = (ulong)sourceOffset;
        ulong destinationStart = (ulong)destinationOffset;
        ulong count = (ulong)byteCount;
        if (sourceStart > source.Length || count > source.Length - sourceStart ||
            destinationStart > destination.Length || count > destination.Length - destinationStart)
            return ERendererComputeEnqueueStatus.InvalidResource;

        if (!TryGetGpuCopyBuffer(source, out uint sourceId, out ulong sourceCapacity) ||
            !TryGetGpuCopyBuffer(destination, out uint destinationId, out ulong destinationCapacity) ||
            sourceStart > sourceCapacity || destinationStart > destinationCapacity ||
            count > sourceCapacity - sourceStart || count > destinationCapacity - destinationStart)
            return ERendererComputeEnqueueStatus.InvalidResource;

        if (sourceId == destinationId &&
            sourceStart < destinationStart + count && destinationStart < sourceStart + count)
            return ERendererComputeEnqueueStatus.InvalidResource;

        // Publish shader writes before the copy reads the source buffer.
        MemoryBarrier(EMemoryBarrierMask.ShaderStorage | EMemoryBarrierMask.BufferUpdate);
        RawGL.CopyNamedBufferSubData(sourceId, destinationId, sourceOffset, destinationOffset, byteCount);
        return ERendererComputeEnqueueStatus.Enqueued;
    }

    private bool TryGetGpuCopyBuffer(XRDataBuffer buffer, out uint bufferId, out ulong capacity)
    {
        bufferId = 0;
        capacity = 0;
        if (GetOrCreateAPIRenderObject(buffer, generateNow: true) is not GLDataBuffer glBuffer)
            return false;

        glBuffer.EnsureStorageAllocatedForGpuCopy();
        capacity = glBuffer.BackendAllocatedByteSize;
        return capacity != 0 && glBuffer.TryGetBindingId(out bufferId) && bufferId != GLObjectBase.InvalidBindingId;
    }
}
