using System.Numerics;

namespace XREngine.Rendering.Meshlets;

/// <summary>Provides optional mesh optimization without native imports in the rendering kernel.</summary>
public interface IMeshOptimizerBackend
{
    string VersionKey { get; }
    /// <summary>Writes one Mikk-compatible tangent for each triangle corner.</summary>
    void GenerateTangents(Span<Vector4> destination, ReadOnlySpan<uint> indices, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals, ReadOnlySpan<Vector2> texCoords)
        => throw new NotSupportedException("The installed mesh processing backend does not support tangent generation.");
    nuint BuildMeshletsBound(nuint indexCount, uint maxVertices, uint maxTriangles);
    nuint BuildNativeMeshletClusters(MeshletBuildMode buildMode, MeshOptimizerMeshlet[] meshlets, uint[] meshletVertices, byte[] meshletTriangles, uint[] indices, float[] vertexPositions, nuint vertexCount, uint maxVertices, uint minTriangles, uint maxTriangles, float coneWeight, float splitFactor, float fillWeight);
    void OptimizeMeshletLevel(Span<uint> meshletVertices, Span<byte> meshletTriangles, int level);
    MeshOptimizerBounds ComputeMeshletBounds(ReadOnlySpan<uint> meshletVertices, ReadOnlySpan<byte> meshletTriangles, float[] vertexPositions, int vertexCount);
    int EncodeMeshlet(uint[]? vertices, byte[] triangles, int maxVertices, int maxTriangles);
    int Simplify(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, out float resultError);
    int SimplifyWithAttributes(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError);
    int SimplifyWithUpdate(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError);
    int SimplifySloppy(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, byte[]? vertexLock, out float resultError);
    float SimplifyScale(float[] positions, int vertexCount);

    // Vertex codec. Sizes and strides are in bytes.
    int EncodeVertexBufferBound(int vertexCount, int vertexSize);
    int EncodeVertexBuffer(Span<byte> destination, ReadOnlySpan<byte> vertices, int vertexCount, int vertexSize, int level = 3, int version = 1);
    void DecodeVertexBuffer(Span<byte> destination, int vertexCount, int vertexSize, ReadOnlySpan<byte> encoded);
    int DecodeVertexVersion(ReadOnlySpan<byte> encoded);

    // Index codec and vertex cache order.
    int EncodeIndexBufferBound(int indexCount, int vertexCount);
    int EncodeIndexBuffer(Span<byte> destination, ReadOnlySpan<uint> indices);
    void DecodeIndexBuffer(Span<uint> destination, ReadOnlySpan<byte> encoded);
    void OptimizeVertexCache(Span<uint> destination, ReadOnlySpan<uint> indices, int vertexCount);

    // Vertex filters.
    void EncodeFilterQuat(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> quaternionsXyzw);
    void EncodeFilterExp(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> data, MeshOptimizerExpMode mode);
    void EncodeFilterColor(Span<byte> destination, int count, int stride, int bits, ReadOnlySpan<float> rgba);
    void DecodeFilterQuat(Span<byte> buffer, int count, int stride);
    void DecodeFilterExp(Span<byte> buffer, int count, int stride);
    void DecodeFilterColor(Span<byte> buffer, int count, int stride);

    // Points and welding.
    void SpatialSortRemap(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride);
    void SpatialClusterPoints(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride, int clusterSize);
    void GeneratePositionRemap(Span<uint> destination, ReadOnlySpan<float> positions, int vertexCount, int positionsStride);
    int FilterIndexBuffer(Span<uint> destination, ReadOnlySpan<uint> indices, ReadOnlySpan<float> vertices, int vertexCount, int vertexSize, int vertexStride);

    // Cluster hierarchy. A backend without these functions keeps the default bodies, which
    // throw, so a cluster hierarchy cook fails with a clear message instead of a wrong result.
    /// <summary>Groups clusters that share vertices or lie close together; returns the group count and writes each cluster's group id.</summary>
    int PartitionClusters(Span<uint> destination, ReadOnlySpan<uint> clusterIndices, ReadOnlySpan<uint> clusterIndexCounts, ReadOnlySpan<float> positions, int vertexCount, int positionsStride, int targetPartitionSize)
        => throw new NotSupportedException($"{GetType().Name} does not implement cluster partitioning.");
    /// <summary>Returns a sphere around points, or around spheres when radii are given.</summary>
    MeshOptimizerBounds ComputeSphereBounds(ReadOnlySpan<float> positions, int count, int positionsStride, ReadOnlySpan<float> radii, int radiiStride)
        => throw new NotSupportedException($"{GetType().Name} does not implement sphere bounds.");
    /// <summary>Returns the sphere and normal cone of a cluster given as a triangle index list.</summary>
    MeshOptimizerBounds ComputeClusterBounds(ReadOnlySpan<uint> indices, ReadOnlySpan<float> positions, int vertexCount, int positionsStride)
        => throw new NotSupportedException($"{GetType().Name} does not implement cluster bounds.");
}
