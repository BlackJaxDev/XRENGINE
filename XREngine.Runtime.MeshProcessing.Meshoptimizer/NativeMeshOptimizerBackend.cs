namespace XREngine.Rendering.Meshlets;

/// <summary>Adapts the meshoptimizer native binding to the engine mesh contract.</summary>
internal sealed class NativeMeshOptimizerBackend : IMeshOptimizerBackend
{
    public string VersionKey => NativeMeshOptimizer.VersionKey;
    public nuint BuildMeshletsBound(nuint indexCount, uint maxVertices, uint maxTriangles)
        => NativeMeshOptimizer.BuildMeshletsBound(indexCount, maxVertices, maxTriangles);
    public nuint BuildNativeMeshletClusters(MeshletBuildMode buildMode, MeshOptimizerMeshlet[] meshlets, uint[] meshletVertices, byte[] meshletTriangles, uint[] indices, float[] vertexPositions, nuint vertexCount, uint maxVertices, uint minTriangles, uint maxTriangles, float coneWeight, float splitFactor, float fillWeight)
        => NativeMeshOptimizer.BuildNativeMeshletClusters(buildMode, meshlets, meshletVertices, meshletTriangles, indices, vertexPositions, vertexCount, maxVertices, minTriangles, maxTriangles, coneWeight, splitFactor, fillWeight);
    public void OptimizeMeshletLevel(Span<uint> meshletVertices, Span<byte> meshletTriangles, int level)
        => NativeMeshOptimizer.OptimizeMeshletLevel(meshletVertices, meshletTriangles, level);
    public MeshOptimizerBounds ComputeMeshletBounds(ReadOnlySpan<uint> meshletVertices, ReadOnlySpan<byte> meshletTriangles, float[] vertexPositions, int vertexCount)
        => NativeMeshOptimizer.ComputeMeshletBounds(meshletVertices, meshletTriangles, vertexPositions, vertexCount);
    public int EncodeMeshlet(uint[]? vertices, byte[] triangles, int maxVertices, int maxTriangles)
        => NativeMeshOptimizer.EncodeMeshlet(vertices, triangles, maxVertices, maxTriangles);
    public int Simplify(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, out float resultError)
        => NativeMeshOptimizer.Simplify(indices, positions, vertexCount, targetIndexCount, targetError, options, out resultError);
    public int SimplifyWithAttributes(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError)
        => NativeMeshOptimizer.SimplifyWithAttributes(indices, positions, vertexCount, attributeBuffer, attributeStride, attributeWeights, targetIndexCount, targetError, options, vertexLock, out resultError);
    public int SimplifyWithUpdate(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError)
        => NativeMeshOptimizer.SimplifyWithUpdate(indices, positions, vertexCount, attributeBuffer, attributeStride, attributeWeights, targetIndexCount, targetError, options, vertexLock, out resultError);
    public int SimplifySloppy(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, byte[]? vertexLock, out float resultError)
        => NativeMeshOptimizer.SimplifySloppy(indices, positions, vertexCount, targetIndexCount, targetError, vertexLock, out resultError);
    public float SimplifyScale(float[] positions, int vertexCount)
        => NativeMeshOptimizer.SimplifyScale(positions, vertexCount);
    public int EncodeVertexBufferBound(int vertexCount, int vertexSize)
        => NativeMeshOptimizer.EncodeVertexBufferBound(vertexCount, vertexSize);
    public int EncodeVertexBuffer(Span<byte> destination, ReadOnlySpan<byte> vertices, int vertexCount, int vertexSize, int level = 3, int version = 1)
        => NativeMeshOptimizer.EncodeVertexBuffer(destination, vertices, vertexCount, vertexSize, level, version);
    public void DecodeVertexBuffer(Span<byte> destination, int vertexCount, int vertexSize, ReadOnlySpan<byte> encoded)
        => NativeMeshOptimizer.DecodeVertexBuffer(destination, vertexCount, vertexSize, encoded);
    public int DecodeVertexVersion(ReadOnlySpan<byte> encoded)
        => NativeMeshOptimizer.DecodeVertexVersion(encoded);
    public int EncodeIndexBufferBound(int indexCount, int vertexCount)
        => NativeMeshOptimizer.EncodeIndexBufferBound(indexCount, vertexCount);
    public int EncodeIndexBuffer(Span<byte> destination, ReadOnlySpan<uint> indices)
        => NativeMeshOptimizer.EncodeIndexBuffer(destination, indices);
    public void DecodeIndexBuffer(Span<uint> destination, ReadOnlySpan<byte> encoded)
        => NativeMeshOptimizer.DecodeIndexBuffer(destination, encoded);
    public void OptimizeVertexCache(Span<uint> destination, ReadOnlySpan<uint> indices, int vertexCount)
        => NativeMeshOptimizer.OptimizeVertexCache(destination, indices, vertexCount);
    public void EncodeFilterQuat(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> quaternionsXyzw)
        => NativeMeshOptimizer.EncodeFilterQuat(destination, count, stride, bits, quaternionsXyzw);
    public void EncodeFilterExp(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> data, MeshOptimizerExpMode mode)
        => NativeMeshOptimizer.EncodeFilterExp(destination, count, stride, bits, data, mode);
    public void EncodeFilterColor(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> rgba)
        => NativeMeshOptimizer.EncodeFilterColor(destination, count, stride, bits, rgba);
    public void DecodeFilterQuat(Span<byte> buffer, int count, int stride)
        => NativeMeshOptimizer.DecodeFilterQuat(buffer, count, stride);
    public void DecodeFilterExp(Span<byte> buffer, int count, int stride)
        => NativeMeshOptimizer.DecodeFilterExp(buffer, count, stride);
    public void DecodeFilterColor(Span<byte> buffer, int count, int stride)
        => NativeMeshOptimizer.DecodeFilterColor(buffer, count, stride);
    public void SpatialSortRemap(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride)
        => NativeMeshOptimizer.SpatialSortRemap(destination, positions, vertexCount, positionsStride);
    public void SpatialClusterPoints(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride, int clusterSize)
        => NativeMeshOptimizer.SpatialClusterPoints(destination, positions, vertexCount, positionsStride, clusterSize);
    public void GeneratePositionRemap(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride)
        => NativeMeshOptimizer.GeneratePositionRemap(destination, positions, vertexCount, positionsStride);
    public int FilterIndexBuffer(Span<uint> destination, ReadOnlySpan<uint> indices, ReadOnlySpan<float> vertices, int vertexCount, int vertexSize, int vertexStride)
        => NativeMeshOptimizer.FilterIndexBuffer(destination, indices, vertices, vertexCount, vertexSize, vertexStride);
}
