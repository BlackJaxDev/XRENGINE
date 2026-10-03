using XREngine.Data;
using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>One queue-owned generated index range, fault-checked argument record and exact authored raster pipeline.</summary>
internal sealed class WebGpuMeshletWork : IDisposable
{
    private readonly WebGpuRendererHost _renderer;
    private WebGpuMeshletGeometry? _geometry;
    private WebGpuMeshDraw? _draw;
    private WebGpuMaterial? _material;
    private WebGpuRasterState _raster;
    private WebGpuFrameBuffer? _framebuffer;
    private ulong _attachmentRevision;
    private ulong _outputGeneration;
    private XRMeshRenderer? _streamOwner;
    private WebGpuMeshDeformation? _deformation;
    private int _indexHandle;
    private long _meshBufferRevision;
    private long _rendererBufferRevision;
    private WebGpuAuthoredIndexedLodSelection? _lodSelection;
    internal WebGpuOwnedStorageBuffer Indices { get; }
    internal WebGpuOwnedStorageBuffer State { get; }
    internal WebGpuOwnedStorageBuffer Bounds { get; }

    internal void SetLodSelection(WebGpuAuthoredIndexedLodSelection selection) => _lodSelection = selection;

    internal WebGpuMeshletWork(WebGpuRendererHost renderer)
    {
        _renderer = renderer;
        Indices = new(renderer, "Meshlet expanded uint32 indices", BrowserBufferUsage.Index);
        State = new(renderer, "Meshlet checked indexed arguments", BrowserBufferUsage.Indirect);
        Bounds = new(renderer, "Meshlet current deformation bounds");
    }

    internal bool TryRecord(in GpuMeshSubmissionRecord record, in WebGpuPreparedMeshDraw prepared,
        WebGpuMeshletGeometry geometry, XRCamera camera, WebGpuRenderProgram cull,
        WebGpuRenderProgram finalize, WebGpuRenderProgram refit, bool cullEnabled, float expansion)
    {
        if (_renderer.CurrentFrameOutput is not { } output) return false;
        RenderFrameViewSelection view = _renderer.RequireFrozenView();
        uint indexCount = checked(geometry.SourceTriangleCount * 3u);
        Indices.EnsureCapacity(checked((int)Math.Max(indexCount * 4u, 16u)));
        State.EnsureCapacity(32);
        Bounds.EnsureCapacity(checked((int)Math.Max(geometry.MeshletCount * 16u, 16u)));
        WebGpuFrameBuffer? framebuffer = _renderer.GetBoundEngineFrameBuffer();
        bool geometryChanged = !ReferenceEquals(_geometry, geometry);
        if (geometryChanged)
        {
            if (_geometry is not null) _geometry.LeaseCount--;
            _geometry = geometry;
            geometry.LeaseCount++;
        }
        if (_draw is null || geometryChanged || !ReferenceEquals(_material, prepared.Material) ||
            _raster != _renderer.RasterState || !ReferenceEquals(_framebuffer, framebuffer) ||
            _attachmentRevision != (framebuffer?.Revision ?? 0) || _outputGeneration != output.TargetGeneration ||
            !ReferenceEquals(_streamOwner, record.Renderer) || !ReferenceEquals(_deformation, prepared.Deformation) ||
            _indexHandle != Indices.ResourceHandle || _meshBufferRevision != record.SourceBindings.MeshBufferRevision ||
            _rendererBufferRevision != record.SourceBindings.RendererBufferRevision)
        {
            _draw?.Dispose();
            _material = prepared.Material;
            _raster = _renderer.RasterState;
            _framebuffer = framebuffer;
            _attachmentRevision = framebuffer?.Revision ?? 0;
            _outputGeneration = output.TargetGeneration;
            _streamOwner = record.Renderer;
            _deformation = prepared.Deformation;
            _indexHandle = Indices.ResourceHandle;
            _meshBufferRevision = record.SourceBindings.MeshBufferRevision;
            _rendererBufferRevision = record.SourceBindings.RendererBufferRevision;
            _draw = new(_renderer, prepared.Material.Program, record.Mesh, Indices, _raster,
                in output, framebuffer, prepared.Deformation, record.SourceBindings);
        }
        if (!_draw.IsReady) return false;
        Span<byte> clearedState = stackalloc byte[32];
        clearedState.Clear();
        State.StageUpload(clearedState);
        uint dispatchX = Math.Min(geometry.MeshletCount, 65535u);
        uint dispatchY = (geometry.MeshletCount + 65534u) / 65535u;
        try
        {
            if (prepared.Deformation is { } deformation)
            {
                refit.SetNativeBindingCacheOwner(State);
                refit.BindStorageBuffer(0, geometry.Arena);
                refit.Data.BindBuffer(deformation.Positions, 1);
                refit.BindStorageBuffer(2, Bounds);
                refit.BindStorageBuffer(3, State);
                refit.Data.Uniform("MeshletCount", geometry.MeshletCount);
                refit.Data.Uniform("VertexCount", geometry.VertexCount);
                refit.Data.Uniform("PositionWordCount", deformation.Positions.Length / 4);
                refit.Data.Uniform("DescriptorWordOffset", geometry.DescriptorWordOffset);
                refit.Data.Uniform("RemapWordOffset", geometry.RemapWordOffset);
                refit.Data.Uniform("RemapCount", geometry.RemapCount);
                refit.Data.Uniform("BoundsWordOffset", 0u);
                refit.Data.Uniform("BoundsWordCount", Bounds.ByteLength / 4);
                refit.Data.Uniform("Reserved0", 0u);
                refit.Data.Uniform("Reserved1", 0u);
                refit.Data.Uniform("Reserved2", 0u);
                refit.Data.Uniform("Reserved3", 0u);
                refit.RecordCompute(dispatchX, dispatchY, 1);
            }
            cull.SetNativeBindingCacheOwner(State);
            cull.BindStorageBuffer(0, geometry.Arena);
            cull.BindStorageBuffer(1, Bounds);
            cull.BindStorageBuffer(2, Indices);
            cull.BindStorageBuffer(3, State);
            cull.BindStorageBuffer(4, _lodSelection?.Selected
                ?? throw new InvalidOperationException("Meshlet expansion requires its recorded GPU LOD selection."));
            if (prepared.Instances is { } instanceSource) cull.Data.BindBuffer(instanceSource.Buffer, 5);
            else cull.BindStorageBuffer(5, _lodSelection.Selected);
            cull.Data.Uniform("ModelMatrix", record.CurrentWorld);
            cull.Data.Uniform("ViewProjection", view.ViewProjectionMatrix);
            cull.Data.Uniform("MeshletCount", geometry.MeshletCount);
            cull.Data.Uniform("SourceTriangleCount", geometry.SourceTriangleCount);
            cull.Data.Uniform("VertexCount", geometry.VertexCount);
            cull.Data.Uniform("IndexCapacity", Indices.ByteLength / 4);
            cull.Data.Uniform("DescriptorWordOffset", geometry.DescriptorWordOffset);
            cull.Data.Uniform("RemapWordOffset", geometry.RemapWordOffset);
            cull.Data.Uniform("RemapCount", geometry.RemapCount);
            cull.Data.Uniform("TriangleWordOffset", geometry.TriangleWordOffset);
            cull.Data.Uniform("TriangleByteCount", geometry.TriangleByteCount);
            cull.Data.Uniform("PrimitiveWordOffset", geometry.PrimitiveWordOffset);
            cull.Data.Uniform("PrimitiveCount", geometry.PrimitiveCount);
            cull.Data.Uniform("BoundsWordOffset", 0u);
            cull.Data.Uniform("CullEnabled", cullEnabled ? 1u : 0u);
            cull.Data.Uniform("UseRefitBounds", prepared.Deformation is null ? 0u : 1u);
            cull.Data.Uniform("SphereExpansion", expansion);
            uint layerMask = view.View.CameraCullingMask;
            bool enabled = (record.Metadata.LayerMask & layerMask) != 0 &&
                (!view.ShadowPass ||
                 (record.Metadata.Flags & (uint)GPUIndirectRenderFlags.CastShadow) != 0);
            cull.Data.Uniform("DrawEnabled", enabled ? 1u : 0u);
            cull.Data.Uniform("CandidateMeshId", record.Metadata.MeshID);
            cull.Data.Uniform("CandidateLod", record.Metadata.LodPolicy);
            WebGpuAuthoredInstanceContract.SetCullParameters(cull.Data, record.InstanceCount, prepared.Instances);
            cull.RecordCompute(dispatchX, dispatchY, 1);
            finalize.SetNativeBindingCacheOwner(State);
            finalize.BindStorageBuffer(0, State);
            finalize.Data.Uniform("MeshletCount", geometry.MeshletCount);
            finalize.Data.Uniform("IndexCapacity", Indices.ByteLength / 4);
            finalize.Data.Uniform("SourceTriangleCount", geometry.SourceTriangleCount);
            finalize.Data.Uniform("InstanceCount", record.InstanceCount);
            finalize.RecordCompute(1, 1, 1);
            _draw.RecordOwnedIndexedIndirect(prepared.Bindings, State, record.InstanceCount);
            return true;
        }
        finally
        {
            cull.ClearTransientComputeBindings();
            finalize.ClearTransientComputeBindings();
            refit.ClearTransientComputeBindings();
        }
    }

    internal void ReleaseDrawUsing(AbstractRenderAPIObject resource)
    {
        if (_draw?.DependsOn(resource) != true) return;
        _draw.Dispose();
        _draw = null;
    }

    internal void ReleaseCommandsUsingHandle(AbstractRenderAPIObject resource, int handle)
        => _draw?.ReleaseCommandsUsingHandle(resource, handle);

    internal void ReleaseCommandUsing(WebGpuRenderProgram program, WebGpuBindingSet bindings)
        => _draw?.ReleaseCommandUsing(program, bindings);

    public void Dispose()
    {
        _draw?.Dispose();
        _draw = null;
        if (_geometry is not null) _geometry.LeaseCount--;
        _geometry = null;
        Indices.Dispose();
        State.Dispose();
        Bounds.Dispose();
    }
}
