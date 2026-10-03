using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private readonly record struct VertexlessIndirectKey(WebGpuRenderProgram Program,
        WebGpuRasterState State, WebGpuFrameBuffer? FrameBuffer, ulong AttachmentRevision, bool Native);
    private readonly Dictionary<VertexlessIndirectKey, WebGpuMeshDraw> _vertexlessIndirectDraws = [];
    private int _nativeVertexlessDrawCount;
    private ulong _vertexlessIndirectSurfaceGeneration;

    /// <summary>Records one native GPU-written draw whose shader pulls geometry from canonical storage bindings.</summary>
    public void DrawVertexlessIndirect(XRRenderProgram program, XRDataBuffer arguments, nuint byteOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        RequireReady();
        if (!_engineRecording)
            throw new InvalidOperationException("WebGPU.Indirect.FrameRequired: vertexless drawing requires an ordered engine frame.");
        if (arguments.Target != EBufferTarget.DrawIndirectBuffer || byteOffset % 4 != 0 ||
            byteOffset > uint.MaxValue || (ulong)byteOffset + 16 > arguments.Length)
            throw new ArgumentOutOfRangeException(nameof(byteOffset), "Vertexless indirect draws require four aligned GPU-written uint arguments.");
        WebGpuDataBuffer buffer = (WebGpuDataBuffer)GetOrCreateAPIRenderObject(arguments, generateNow: true)!;
        buffer.StagePendingUpload();
        RecordVertexlessIndirect(program, buffer, buffer.ResourceHandle, byteOffset);
    }

    /// <summary>Consumes slot-owned GPU argument storage without creating a parallel CPU buffer image.</summary>
    internal void DrawVertexlessIndirect(XRRenderProgram program, WebGpuOwnedStorageBuffer arguments, nuint byteOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(arguments);
        RequireReady();
        if (arguments.Owner != this || arguments.IsRetired || !arguments.IsGenerated ||
            byteOffset % 4 != 0 || byteOffset > uint.MaxValue || (ulong)byteOffset + 16 > arguments.ByteLength)
            throw new ArgumentOutOfRangeException(nameof(arguments), "Vertexless indirect arguments require live, owned, aligned storage for four uint values.");
        RecordVertexlessIndirect(program, arguments, arguments.ResourceHandle, byteOffset);
    }

    private void RecordVertexlessIndirect(XRRenderProgram program, AbstractRenderAPIObject arguments,
        int argumentHandle, nuint byteOffset)
    {
        if (!TryGetVertexlessDraw(program, out WebGpuMeshDraw? draw, out WebGpuBindingSet? bindings)) return;
        // firstInstance is zero; the shader pulls the stable scene identity.
        draw!.RecordVertexlessIndirect(bindings!, arguments, argumentHandle, (uint)byteOffset);
    }

    /// <summary>Records an explicitly CPU-authored storage-pulled draw without an indirect or count-buffer substitution.</summary>
    public void DrawVertexless(XRRenderProgram program, uint vertexCount, uint firstVertex = 0, uint instanceCount = 1)
    {
        ArgumentNullException.ThrowIfNull(program);
        RequireReady();
        if ((ulong)firstVertex + vertexCount > (ulong)uint.MaxValue + 1)
            throw new ArgumentOutOfRangeException(nameof(vertexCount));
        if (vertexCount == 0 || instanceCount == 0) return;
        if (!TryGetVertexlessDraw(program, out WebGpuMeshDraw? draw, out WebGpuBindingSet? bindings)) return;
        draw!.RecordVertexlessDirect(bindings!, vertexCount, firstVertex, instanceCount);
    }

    private bool TryGetVertexlessDraw(XRRenderProgram program, out WebGpuMeshDraw? draw, out WebGpuBindingSet? bindings)
    {
        draw = null;
        bindings = null;
        if (!_engineRecording || CurrentFrameOutput is not { } output)
            throw new InvalidOperationException("WebGPU.Indirect.FrameRequired: vertexless drawing requires an ordered engine frame.");
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        if (!api.TryPrepareForRendering())
        {
            MarkEngineDrawPending();
            return false;
        }
        if (api.Artifact.VertexEntryPoint is null || api.Artifact.ComputeEntryPoint is not null ||
            !api.Artifact.VertexBuffers.IsDefaultOrEmpty)
            throw UnsupportedEngineOperation(nameof(DrawVertexlessIndirect), "a cooked raster program with no vertex-buffer inputs is required");
        if (_vertexlessIndirectSurfaceGeneration != output.TargetGeneration)
        {
            DestroyVertexlessIndirectDraws();
            SetField(ref _vertexlessIndirectSurfaceGeneration, output.TargetGeneration, publishNotifications: false);
        }
        WebGpuFrameBuffer? framebuffer = GetBoundEngineFrameBuffer();
        VertexlessIndirectKey key = new(api, RasterState, framebuffer, framebuffer?.Revision ?? 0, api.IsNativeRasterBindingPublication);
        if (!_vertexlessIndirectDraws.TryGetValue(key, out draw))
        {
            if (key.Native ? _nativeVertexlessDrawCount >= WebGpuAdvancedVisibilityFrame.MaximumRetainedRasterStates :
                _vertexlessIndirectDraws.Count - _nativeVertexlessDrawCount >= 128)
                throw UnsupportedEngineOperation(nameof(DrawVertexlessIndirect), "the output exceeds its reserved native or generic raster-state capacity");
            draw = new WebGpuMeshDraw(this, api, key.State, output, framebuffer);
            _vertexlessIndirectDraws.Add(key, draw);
            if (key.Native) SetField(ref _nativeVertexlessDrawCount, _nativeVertexlessDrawCount + 1, publishNotifications: false);
        }
        if (!draw.IsReady || !api.TrySnapshotBindings(false, out bindings))
        {
            MarkEngineDrawPending();
            return false;
        }
        return true;
    }

    private void DestroyVertexlessIndirectDraws()
    {
        foreach (WebGpuMeshDraw draw in _vertexlessIndirectDraws.Values) draw.Dispose();
        _vertexlessIndirectDraws.Clear();
        SetField(ref _nativeVertexlessDrawCount, 0, publishNotifications: false);
    }

    private void ReleaseVertexlessIndirectDrawsUsing(AbstractRenderAPIObject resource)
    {
        List<VertexlessIndirectKey>? obsolete = null;
        foreach ((VertexlessIndirectKey key, WebGpuMeshDraw draw) in _vertexlessIndirectDraws)
        {
            if (!draw.DependsOn(resource)) continue;
            draw.Dispose();
            (obsolete ??= []).Add(key);
        }
        if (obsolete is not null)
            foreach (VertexlessIndirectKey key in obsolete)
            {
                _vertexlessIndirectDraws.Remove(key);
                if (key.Native) SetField(ref _nativeVertexlessDrawCount, _nativeVertexlessDrawCount - 1, publishNotifications: false);
            }
    }
}
