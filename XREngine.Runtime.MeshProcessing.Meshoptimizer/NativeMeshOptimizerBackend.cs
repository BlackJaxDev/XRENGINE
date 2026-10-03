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
}
