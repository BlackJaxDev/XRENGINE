using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuMeshRenderer
{
    private readonly record struct IndirectDrawKey(WebGpuRenderProgram Program, WebGpuRasterState State,
        WebGpuFrameBuffer? FrameBuffer, ulong AttachmentRevision);
    private readonly Dictionary<IndirectDrawKey, WebGpuMeshDraw> _indirectDraws = [];
    private XRDataBuffer? _indirectIndices;
    private IndexSize _indirectIndexSize;
    private long _indirectGeometryRevision = -1;
    private ulong _indirectSurfaceGeneration;
    private XRMesh? _indirectMesh;

    internal void SetIndirectIndices(XRDataBuffer indices, IndexSize size)
    {
        if (ReferenceEquals(_indirectIndices, indices) && _indirectIndexSize == size) return;
        DestroyIndirectDraws();
        SetField(ref _indirectIndices, indices, publishNotifications: false);
        SetField(ref _indirectIndexSize, size, publishNotifications: false);
    }

    internal bool TryGetIndirectIndices(out XRDataBuffer? indices, out IndexSize size)
    {
        if (_indirectIndices is { IsDestroyed: false } bound)
        {
            indices = bound;
            size = _indirectIndexSize;
            return true;
        }
        size = default;
        indices = Data.Parent.Mesh?.GetIndexBuffer(EPrimitiveType.Triangles, out size);
        return indices is not null && size is IndexSize.TwoBytes or IndexSize.FourBytes;
    }

    internal void RecordIndirect(WebGpuRenderProgram program, WebGpuDataBuffer arguments,
        WebGpuDataBuffer? count, uint drawCount, uint stride, uint offset, uint countOffset)
    {
        ValidateOwnerGeneration();
        XRMesh? mesh = Data.Parent.Mesh;
        if (Renderer.CurrentFrameOutput is not { } output ||
            !program.TryPrepareForRendering())
        {
            Renderer.MarkEngineDrawPending();
            return;
        }
        if (mesh is not null && mesh.Type != EPrimitiveType.Triangles)
            throw Unsupported("indirect geometry requires indexed triangles");
        if (!TryGetIndirectIndices(out XRDataBuffer? indices, out IndexSize size) || indices is null)
            throw Unsupported("indirect geometry requires a live index buffer");
        if (!ReferenceEquals(_indirectMesh, mesh) || _indirectGeometryRevision != (mesh?.GeometryRevision ?? 0) ||
            _indirectSurfaceGeneration != output.TargetGeneration)
        {
            DestroyIndirectDraws();
            SetField(ref _indirectMesh, mesh, publishNotifications: false);
            SetField(ref _indirectGeometryRevision, mesh?.GeometryRevision ?? 0, publishNotifications: false);
            SetField(ref _indirectSurfaceGeneration, output.TargetGeneration, publishNotifications: false);
        }
        WebGpuFrameBuffer? framebuffer = Renderer.GetBoundEngineFrameBuffer();
        IndirectDrawKey key = new(program, Renderer.RasterState, framebuffer, framebuffer?.Revision ?? 0);
        if (_indirectDraws.TryGetValue(key, out WebGpuMeshDraw? draw) && !draw.MatchesIndirectStreams(mesh, Data.Parent))
        {
            draw.Dispose();
            _indirectDraws.Remove(key);
            draw = null;
        }
        if (draw is null)
        {
            if (_indirectDraws.Count >= 32)
                throw Unsupported("indirect geometry exceeds 32 retained pipeline variants");
            draw = new WebGpuMeshDraw(Renderer, program, mesh, indices, size, key.State,
                output, framebuffer, null, null, 0, streamOwner: Data.Parent);
            _indirectDraws.Add(key, draw);
        }
        if (!draw.IsReady || !program.TrySnapshotBindings(false, out WebGpuBindingSet? bindings))
        {
            Renderer.MarkEngineDrawPending();
            return;
        }
        draw.RecordIndirect(bindings!, arguments, count, drawCount, stride, offset, countOffset);
    }

    private void DestroyIndirectDraws()
    {
        foreach (WebGpuMeshDraw draw in _indirectDraws.Values) draw.Dispose();
        _indirectDraws.Clear();
    }
}
