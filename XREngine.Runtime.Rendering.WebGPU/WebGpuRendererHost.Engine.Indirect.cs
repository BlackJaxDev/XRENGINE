using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private XRMeshRenderer.BaseVersion? _indirectMeshVersion;
    private WebGpuRenderProgram? _indirectProgram;
    private WebGpuDataBuffer? _indirectArguments;
    private WebGpuDataBuffer? _indirectCount;
    private WebGpuIndirectCountKernel? _indirectCountKernel;

    internal WebGpuIndirectCountKernel GetIndirectCountKernel()
    {
        if (_indirectCountKernel is { } existing) return existing;
        WebGpuIndirectCountKernel kernel = new(this);
        SetField(ref _indirectCountKernel, kernel, publishNotifications: false);
        return kernel;
    }

    /// <summary>Every retained compute/raster operation ends its pass before the next operation begins.</summary>
    public override void MemoryBarrier(EMemoryBarrierMask mask)
    {
        RequireReady();
        if (mask == EMemoryBarrierMask.None) return;
        if (!_engineRecording)
            throw new InvalidOperationException("WebGPU.MemoryBarrier.FrameRequired: visibility barriers require an ordered engine frame.");
        // WebGPU supplies buffer/texture visibility at these pass and copy boundaries.
        // This is not a completion wait or permission to synchronously map GPU data.
        const EMemoryBarrierMask known = (EMemoryBarrierMask)0x1ffff;
        if (mask != EMemoryBarrierMask.All && (mask & ~known) != 0)
            throw new ArgumentOutOfRangeException(nameof(mask));
    }

    public override void BindVAOForRenderer(XRMeshRenderer.BaseVersion? version)
    {
        RequireReady();
        SetField(ref _indirectMeshVersion, version, publishNotifications: false);
        SetField(ref _indirectProgram, null, publishNotifications: false);
    }

    public override bool ValidateIndexedVAO(XRMeshRenderer.BaseVersion? version)
        => TryGetIndexBufferInfo(version, out _, out uint count) && count != 0;

    public override bool TryGetIndexBufferInfo(XRMeshRenderer.BaseVersion? version,
        out IndexSize indexElementSize, out uint indexCount)
    {
        indexElementSize = default;
        indexCount = 0;
        if (version is null) return false;
        WebGpuMeshRenderer mesh = (WebGpuMeshRenderer)GetOrCreateAPIRenderObject(version)!;
        if (!mesh.TryGetIndirectIndices(out XRDataBuffer? indices, out indexElementSize) || indices is null)
            return false;
        indexCount = indices.ElementCount;
        return true;
    }

    public override bool TrySyncMeshRendererIndexBuffer(XRMeshRenderer meshRenderer,
        XRDataBuffer indexBuffer, IndexSize elementSize)
    {
        ArgumentNullException.ThrowIfNull(meshRenderer);
        ArgumentNullException.ThrowIfNull(indexBuffer);
        if (indexBuffer.Target != EBufferTarget.ElementArrayBuffer ||
            elementSize is not (IndexSize.TwoBytes or IndexSize.FourBytes)) return false;
        WebGpuMeshRenderer mesh = (WebGpuMeshRenderer)GetOrCreateAPIRenderObject(meshRenderer.GetDefaultVersion())!;
        mesh.SetIndirectIndices(indexBuffer, elementSize);
        return true;
    }

    public override void ConfigureVAOAttributesForProgram(XRRenderProgram program, XRMeshRenderer.BaseVersion? version)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (version is null || !ReferenceEquals(version, _indirectMeshVersion))
            throw new InvalidOperationException("WebGPU.Indirect.MeshBindingRequired: configure the currently bound mesh version.");
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        SetField(ref _indirectProgram, api, publishNotifications: false);
        if (!api.TryPrepareForRendering()) MarkEngineDrawPending();
    }

    public override void BindDrawIndirectBuffer(XRDataBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Target != EBufferTarget.DrawIndirectBuffer)
            throw new ArgumentException("Indexed indirect arguments require DrawIndirectBuffer storage.", nameof(buffer));
        SetField(ref _indirectArguments,
            (WebGpuDataBuffer)GetOrCreateAPIRenderObject(buffer, generateNow: true)!, publishNotifications: false);
    }

    public override void UnbindDrawIndirectBuffer()
        => SetField(ref _indirectArguments, null, publishNotifications: false);

    public override void BindParameterBuffer(XRDataBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Target is not (EBufferTarget.ParameterBuffer or EBufferTarget.ShaderStorageBuffer or EBufferTarget.DrawIndirectBuffer))
            throw new ArgumentException("GPU draw counts require storage-capable parameter buffers.", nameof(buffer));
        SetField(ref _indirectCount,
            (WebGpuDataBuffer)GetOrCreateAPIRenderObject(buffer, generateNow: true)!, publishNotifications: false);
    }

    public override void UnbindParameterBuffer()
        => SetField(ref _indirectCount, null, publishNotifications: false);

    public override void MultiDrawElementsIndirect(uint drawCount, uint stride)
        => RecordIndexedIndirect(drawCount, stride, 0, null, 0);

    public override void MultiDrawElementsIndirectWithOffset(uint drawCount, uint stride, nuint byteOffset)
        => RecordIndexedIndirect(drawCount, stride, byteOffset, null, 0);

    public override void MultiDrawElementsIndirectCount(uint maxDrawCount, uint stride,
        nuint byteOffset = 0, nuint countByteOffset = 0)
        => RecordIndexedIndirect(maxDrawCount, stride, byteOffset,
            _indirectCount ?? throw new InvalidOperationException("WebGPU.Indirect.CountBufferRequired: bind the GPU-written count buffer."), countByteOffset);

    private void RecordIndexedIndirect(uint drawCount, uint stride, nuint byteOffset,
        WebGpuDataBuffer? count, nuint countByteOffset)
    {
        RequireReady();
        if (!_engineRecording)
            throw new InvalidOperationException("WebGPU.Indirect.FrameRequired: indirect submission requires an active engine frame.");
        if (drawCount == 0) return;
        if (!SupportsIndirectCountDraw())
            throw UnsupportedEngineOperation("IndexedIndirect", "GPU scene draw identities require the enabled indirect-first-instance device feature");
        XRMeshRenderer.BaseVersion version = _indirectMeshVersion
            ?? throw new InvalidOperationException("WebGPU.Indirect.MeshBindingRequired: bind the scene geometry streams.");
        WebGpuRenderProgram program = _indirectProgram
            ?? throw new InvalidOperationException("WebGPU.Indirect.ProgramRequired: configure the cooked graphics program.");
        WebGpuDataBuffer arguments = _indirectArguments
            ?? throw new InvalidOperationException("WebGPU.Indirect.ArgumentsRequired: bind the GPU-written indexed arguments.");
        uint actualStride = stride == 0 ? 20u : stride;
        if (drawCount > 65536 || actualStride < 20 || actualStride % 4 != 0 || byteOffset % 4 != 0 ||
            byteOffset > uint.MaxValue || (ulong)byteOffset + (ulong)(drawCount - 1) * actualStride + 20 > arguments.Data.Length)
            throw new ArgumentOutOfRangeException(nameof(drawCount), "WebGPU.Indirect.RangeInvalid: aligned indexed arguments must fit the buffer and bounded 65536-slot batch profile.");
        if (count is not null && (countByteOffset % 4 != 0 || countByteOffset > uint.MaxValue ||
            (ulong)countByteOffset + 4 > count.Data.Length))
            throw new ArgumentOutOfRangeException(nameof(countByteOffset));
        arguments.StagePendingUpload();
        count?.StagePendingUpload();
        WebGpuMeshRenderer mesh = (WebGpuMeshRenderer)GetOrCreateAPIRenderObject(version)!;
        mesh.RecordIndirect(program, arguments, count, drawCount, actualStride,
            (uint)byteOffset, (uint)countByteOffset);
    }
}
