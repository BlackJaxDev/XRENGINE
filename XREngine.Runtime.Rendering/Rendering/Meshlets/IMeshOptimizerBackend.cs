namespace XREngine.Rendering.Meshlets;

/// <summary>Provides optional mesh optimization without native imports in the rendering kernel.</summary>
public interface IMeshOptimizerBackend
{
    string VersionKey { get; }
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
}
