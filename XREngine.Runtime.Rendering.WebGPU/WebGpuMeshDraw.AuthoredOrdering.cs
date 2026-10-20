using XREngine.Data.Geometry;

namespace XREngine.Rendering.WebGPU;

internal sealed partial class WebGpuMeshDraw
{
    internal void CaptureRankedRaster(WebGpuBindingSet bindings, WebGpuOwnedStorageBuffer arguments,
        uint instances, WebGpuAuthoredRasterSnapshot snapshot, WebGpuAuthoredOrderingBatch ordering,
        int sourceIndex, int primitiveIndex, WebGpuRenderProgram mask)
    {
        if (!IsReady) throw new InvalidOperationException("Ordered raster requires its prepared pipeline.");
        ValidateInstanceRange(instances);
        StageGeometryUploads();
        snapshot.BeginCapture(ordering.SourceCount);
        foreach (WebGpuVertexStream stream in _streams)
            snapshot.CaptureBuffer(stream.Buffer, stream.Buffer.ResourceHandle,
                checked((uint)stream.Buffer.BackendAllocatedByteSize));
        if (_indices is { } indices)
            snapshot.CaptureBuffer(indices, indices.ResourceHandle, checked((uint)indices.BackendAllocatedByteSize));
        WebGpuBindingSet frozen = _program.SnapshotAuthoredRasterBindings(bindings, snapshot);
        snapshot.RecordCopies();
        try
        {
            mask.SetNativeBindingCacheOwner(snapshot.Arguments);
            mask.BindStorageBuffer(0, ordering.Ranks);
            mask.BindStorageBuffer(1, arguments);
            mask.BindStorageBuffer(2, snapshot.Arguments);
            mask.Data.Uniform("SourceIndex", checked((uint)sourceIndex));
            mask.Data.Uniform("SourceCount", checked((uint)ordering.SourceCount));
            mask.Data.Uniform("RankCount", checked((uint)ordering.SourceCount));
            mask.Data.Uniform("Reserved", 0u);
            mask.RecordCompute(1, 1, 1);
        }
        finally { mask.ClearTransientComputeBindings(); }
        int count = _program.SnapshotUniforms(snapshot.UniformOffsets);
        (BoundingRectangle? viewport, BoundingRectangle? scissor) = _renderer.ResolveEngineDrawArea();
        snapshot.PrepareRaster(this, frozen, count, viewport, scissor);
        ordering.AddCandidate(snapshot, sourceIndex, primitiveIndex);
    }

    internal int PrepareRankedRaster(WebGpuBindingSet bindings, WebGpuAuthoredRasterSnapshot snapshot, uint offset)
        => _renderer.PrepareEngineCommands(this, DescribeDraw(bindings, snapshot.Arguments.ResourceHandle, 1, 20, offset,
            rasterSnapshot: snapshot));

    internal int PrepareRankedDirectRaster(WebGpuBindingSet bindings, WebGpuAuthoredRasterSnapshot snapshot)
        => _renderer.PrepareEngineCommands(this, DescribeDraw(bindings, rasterSnapshot: snapshot));

    internal void CaptureRankedDirectRaster(WebGpuBindingSet bindings, uint instances,
        WebGpuAuthoredRasterSnapshot snapshot, WebGpuAuthoredOrderingBatch ordering, int sourceIndex, int primitiveIndex)
    {
        if (!IsReady || _indices is null || _instanceStorage is not null)
            throw new NotSupportedException("WebGPU.AuthoredOrdering.DirectGeometryProfile: ordered direct raster requires its exact indexed triangle and ordinary instance contract.");
        ValidateInstanceRange(instances);
        StageGeometryUploads();
        snapshot.BeginCapture(ordering.SourceCount);
        foreach (WebGpuVertexStream stream in _streams)
            snapshot.CaptureBuffer(stream.Buffer, stream.Buffer.ResourceHandle,
                checked((uint)stream.Buffer.BackendAllocatedByteSize));
        snapshot.CaptureBuffer(_indices, _indices.ResourceHandle, checked((uint)_indices.BackendAllocatedByteSize));
        WebGpuBindingSet frozen = _program.SnapshotAuthoredRasterBindings(bindings, snapshot, ordering.Ranks);
        snapshot.RecordCopies();
        int uniformCount = 0;
        for (int rank = 0; rank < ordering.SourceCount; rank++)
        {
            _program.Data.Uniform("AuthoredActiveRank", checked((uint)rank));
            int count = _program.SnapshotUniforms(snapshot.UniformOffsetsForRank(rank));
            if (rank != 0 && count != uniformCount)
                throw new InvalidOperationException("Ordered direct raster changed its uniform layout during rank capture.");
            uniformCount = count;
        }
        (BoundingRectangle? viewport, BoundingRectangle? scissor) = _renderer.ResolveEngineDrawArea();
        snapshot.PrepareRaster(this, frozen, uniformCount, viewport, scissor, instances);
        ordering.AddCandidate(snapshot, sourceIndex, primitiveIndex);
    }

    internal void MarkRankedRasterRecorded(WebGpuBindingSet bindings, BoundingRectangle? scissor, uint? directInstances = null)
    {
        if (directInstances.HasValue)
            _renderer.MarkEngineViewHistoryDrawWrite(_frameBuffer, in _output,
                _program.Artifact.FragmentEntryPoint is not null, _state.ColorWriteMask,
                _indexCount, directInstances.Value, scissor);
        _generatedIndices?.MarkRecorded();
        bindings.MarkRecorded();
        if (scissor is not ({ Width: 0 } or { Height: 0 }))
        {
            _frameBuffer?.MarkRecorded();
            _renderer.CountEngineMeshDraw();
        }
    }
}
