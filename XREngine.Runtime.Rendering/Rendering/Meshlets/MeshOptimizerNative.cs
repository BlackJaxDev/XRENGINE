namespace XREngine.Rendering.Meshlets;

/// <summary>Routes existing mesh operations to the explicitly installed native backend.</summary>
internal static class MeshOptimizerNative
{
    public static string VersionKey => MeshOptimizerBackendServices.Required.VersionKey;
    public static nuint BuildMeshletsBound(nuint indexCount, uint maxVertices, uint maxTriangles)
        => MeshOptimizerBackendServices.Required.BuildMeshletsBound(indexCount, maxVertices, maxTriangles);
    public static nuint BuildNativeMeshletClusters(MeshletBuildMode buildMode, MeshOptimizerMeshlet[] meshlets, uint[] meshletVertices, byte[] meshletTriangles, uint[] indices, float[] vertexPositions, nuint vertexCount, uint maxVertices, uint minTriangles, uint maxTriangles, float coneWeight, float splitFactor, float fillWeight)
        => MeshOptimizerBackendServices.Required.BuildNativeMeshletClusters(buildMode, meshlets, meshletVertices, meshletTriangles, indices, vertexPositions, vertexCount, maxVertices, minTriangles, maxTriangles, coneWeight, splitFactor, fillWeight);
    public static void OptimizeMeshletLevel(Span<uint> meshletVertices, Span<byte> meshletTriangles, int level)
        => MeshOptimizerBackendServices.Required.OptimizeMeshletLevel(meshletVertices, meshletTriangles, level);
    public static MeshOptimizerBounds ComputeMeshletBounds(ReadOnlySpan<uint> meshletVertices, ReadOnlySpan<byte> meshletTriangles, float[] vertexPositions, int vertexCount)
        => MeshOptimizerBackendServices.Required.ComputeMeshletBounds(meshletVertices, meshletTriangles, vertexPositions, vertexCount);
    public static int EncodeMeshlet(uint[]? vertices, byte[] triangles, int maxVertices, int maxTriangles)
        => MeshOptimizerBackendServices.Required.EncodeMeshlet(vertices, triangles, maxVertices, maxTriangles);
    public static int Simplify(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, out float resultError)
        => MeshOptimizerBackendServices.Required.Simplify(indices, positions, vertexCount, targetIndexCount, targetError, options, out resultError);
    public static int SimplifyWithAttributes(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError)
        => MeshOptimizerBackendServices.Required.SimplifyWithAttributes(indices, positions, vertexCount, attributeBuffer, attributeStride, attributeWeights, targetIndexCount, targetError, options, vertexLock, out resultError);
    public static int SimplifyWithUpdate(uint[] indices, float[] positions, int vertexCount, float[] attributeBuffer, int attributeStride, float[] attributeWeights, int targetIndexCount, float targetError, MeshOptimizerSimplifyOptions options, byte[]? vertexLock, out float resultError)
        => MeshOptimizerBackendServices.Required.SimplifyWithUpdate(indices, positions, vertexCount, attributeBuffer, attributeStride, attributeWeights, targetIndexCount, targetError, options, vertexLock, out resultError);
    public static int SimplifySloppy(uint[] indices, float[] positions, int vertexCount, int targetIndexCount, float targetError, byte[]? vertexLock, out float resultError)
        => MeshOptimizerBackendServices.Required.SimplifySloppy(indices, positions, vertexCount, targetIndexCount, targetError, vertexLock, out resultError);
    public static float SimplifyScale(float[] positions, int vertexCount)
        => MeshOptimizerBackendServices.Required.SimplifyScale(positions, vertexCount);
}
