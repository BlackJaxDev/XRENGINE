using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Completion-owned whole-primitive visibility and arguments over the exact original index stream.</summary>
internal sealed class WebGpuIndirectWork(WebGpuRendererHost renderer) : IDisposable
{
    private WebGpuMeshDraw? _draw;
    private XRMesh? _mesh;
    private XRDataBuffer? _indices;
    private WebGpuMaterial? _material;
    private WebGpuRasterState _raster;
    private WebGpuFrameBuffer? _framebuffer;
    private ulong _attachmentRevision;
    private ulong _outputGeneration;
    private XRMeshRenderer? _streamOwner;
    private WebGpuMeshDeformation? _deformation;
    private long _geometryRevision;
    private long _meshBufferRevision;
    private long _rendererBufferRevision;
    private WebGpuAuthoredRasterSnapshot? _orderedRaster;
    internal WebGpuOwnedStorageBuffer Arguments { get; } = new(renderer,
        "Authored whole-primitive indexed arguments", BrowserBufferUsage.Indirect);

    internal bool TryRecord(in GpuMeshSubmissionRecord record, in WebGpuPreparedMeshDraw prepared,
        WebGpuAuthoredIndexedLodSelection selection, WebGpuRenderProgram cull, ref bool cullEnabled, float expansion,
        WebGpuAuthoredOrderingBatch? ordering = null, int orderSourceIndex = 0, WebGpuRenderProgram? orderMask = null)
    {
        if (renderer.CurrentFrameOutput is not { } output) return false;
        if (record.Mesh.Type != EPrimitiveType.Triangles)
            throw new NotSupportedException("WebGPU.Indirect.PrimitiveProfile: authored indexed submission requires triangle geometry.");
        XRDataBuffer? indices = record.SourceBindings.IndexBuffer;
        var indexSize = record.SourceBindings.IndexSize;
        if (indices is null) return false;
        if (!record.SourceBindings.AreIndexBindingsCurrent)
            throw new NotSupportedException("WebGPU.Indirect.IndexPublicationChanged: original index ownership or contents changed after publication.");
        Arguments.EnsureCapacity(checked((int)WebGpuIndirectProgramContract.ArgumentByteSize));
        WebGpuFrameBuffer? framebuffer = renderer.GetBoundEngineFrameBuffer();
        if (_draw is null || !ReferenceEquals(_mesh, record.Mesh) || !ReferenceEquals(_indices, indices) ||
            !ReferenceEquals(_material, prepared.Material) || _raster != renderer.RasterState ||
            !ReferenceEquals(_framebuffer, framebuffer) || _attachmentRevision != (framebuffer?.Revision ?? 0) ||
            _outputGeneration != output.TargetGeneration || !ReferenceEquals(_streamOwner, record.Renderer) ||
            !ReferenceEquals(_deformation, prepared.Deformation) || _geometryRevision != record.GeometryRevision ||
            _meshBufferRevision != record.SourceBindings.MeshBufferRevision ||
            _rendererBufferRevision != record.SourceBindings.RendererBufferRevision)
        {
            _draw?.Dispose();
            _mesh = record.Mesh;
            _indices = indices;
            _material = prepared.Material;
            _raster = renderer.RasterState;
            _framebuffer = framebuffer;
            _attachmentRevision = framebuffer?.Revision ?? 0;
            _outputGeneration = output.TargetGeneration;
            _streamOwner = record.Renderer;
            _deformation = prepared.Deformation;
            _geometryRevision = record.GeometryRevision;
            _meshBufferRevision = record.SourceBindings.MeshBufferRevision;
            _rendererBufferRevision = record.SourceBindings.RendererBufferRevision;
            _draw = new(renderer, prepared.Material.Program, record.Mesh, indices, indexSize, _raster,
                in output, framebuffer, null, null, 0, prepared.Deformation,
                sources: record.SourceBindings, indirectFirstInstanceFeature: false);
        }
        if (!_draw.IsReady) return false;
        RenderFrameViewSelection view = renderer.RequireFrozenView();
        XRDataBuffer? positions = null;
        uint positionOffset = 0;
        if (cullEnabled && !prepared.Instances.HasValue)
        {
            // Resolve the very same frozen or deformed attribute used by raster.
            // A GPU reduction supplies current bounds; mesh.Bounds may be stale
            // after an authored vertex write and is never used for rejection.
            (XRDataBuffer source, int offset, _) = WebGpuMeshDraw.ResolveAttribute(record.Mesh,
                "position", prepared.Deformation, sources: record.SourceBindings);
            positions = source;
            positionOffset = checked((uint)offset);
            if (!CanRefitPositions(positions, positionOffset, renderer.MaximumAdvancedStorageBytes))
                cullEnabled = false;
        }
        bool enabled = (record.Metadata.LayerMask & view.View.CameraCullingMask) != 0 &&
            (!view.ShadowPass || (record.Metadata.Flags & (uint)GPUIndirectRenderFlags.CastShadow) != 0);
        try
        {
            cull.SetNativeBindingCacheOwner(Arguments);
            cull.BindStorageBuffer(0, selection.Selected);
            cull.BindStorageBuffer(1, Arguments);
            if (cullEnabled && positions is not null) cull.Data.BindBuffer(positions, 2);
            else cull.BindStorageBuffer(2, selection.Selected);
            if (prepared.Instances is { } instanceSource) cull.Data.BindBuffer(instanceSource.Buffer, 3);
            else cull.BindStorageBuffer(3, selection.Selected);
            cull.Data.Uniform("ModelMatrix", WebGpuImpostorBounds.CullMatrix(in record, prepared.Material.Data, in view));
            cull.Data.Uniform("ViewProjection", view.ViewProjectionMatrix);
            cull.Data.Uniform("IndexCount", record.SourceBindings.IndexCount);
            cull.Data.Uniform("CandidateMeshId", record.Metadata.MeshID);
            cull.Data.Uniform("CandidateLod", record.Metadata.LodPolicy);
            cull.Data.Uniform("DrawEnabled", enabled ? 1u : 0u);
            cull.Data.Uniform("CullEnabled", cullEnabled ? 1u : 0u);
            cull.Data.Uniform("SphereExpansion", expansion);
            cull.Data.Uniform("VertexCount", positions?.ElementCount ?? 0u);
            cull.Data.Uniform("PositionWordCount", positions?.Length / 4 ?? 0u);
            cull.Data.Uniform("PositionStrideWords", positions?.ElementSize / 4 ?? 0u);
            cull.Data.Uniform("PositionOffsetWords", cullEnabled ? positionOffset / 4 : 0u);
            cull.Data.Uniform("Reserved0", 0u);
            cull.Data.Uniform("Reserved1", 0u);
            cull.Data.Uniform("Reserved2", 0u);
            cull.Data.Uniform("Reserved3", 0u);
            WebGpuAuthoredInstanceContract.SetCullParameters(cull.Data, record.InstanceCount, prepared.Instances);
            cull.RecordCompute(1, 1, 1);
            if (ordering is null) _draw.RecordOwnedIndexedIndirect(prepared.Bindings, Arguments, record.InstanceCount);
            else _draw.CaptureRankedRaster(prepared.Bindings, Arguments, record.InstanceCount,
                _orderedRaster ??= new(renderer), ordering, orderSourceIndex, record.PrimitiveIndex, orderMask!);
            return true;
        }
        finally { cull.ClearTransientComputeBindings(); }
    }

    private static bool CanRefitPositions(XRDataBuffer positions, uint offset, int maximumStorageBytes)
        => positions.ElementCount is > 0 and <= WebGpuIndirectProgramContract.MaximumRefitVertexCount &&
           positions.Length >= 12 && positions.Length <= maximumStorageBytes &&
           (offset | positions.ElementSize | positions.Length) % 4 == 0 && positions.ElementSize >= 12 &&
           offset <= positions.ElementSize - 12;

    internal void ReleaseDrawUsing(AbstractRenderAPIObject resource)
    {
        if (_draw?.DependsOn(resource) != true) return;
        _orderedRaster?.InvalidateRaster();
        _draw.Dispose();
        _draw = null;
    }

    internal void ReleaseCommandsUsingHandle(AbstractRenderAPIObject resource, int handle)
    {
        _orderedRaster?.ReleaseCommandsUsingHandle(resource, handle);
        _draw?.ReleaseCommandsUsingHandle(resource, handle);
    }

    internal void ReleaseCommandUsing(WebGpuRenderProgram program, WebGpuBindingSet bindings)
    {
        _orderedRaster?.ReleaseCommandUsing(bindings);
        _draw?.ReleaseCommandUsing(program, bindings);
    }

    public void Dispose()
    {
        _orderedRaster?.Dispose();
        _orderedRaster = null;
        _draw?.Dispose();
        _draw = null;
        Arguments.Dispose();
    }
}
