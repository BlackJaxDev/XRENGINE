using System.Numerics;

namespace XREngine.Rendering.Meshlets;

public static partial class MeshOptimizerIntegration
{
    /// <summary>Weight of each normal component in the hierarchy simplification.</summary>
    private const float ClusterHierarchyNormalWeight = 0.5f;
    /// <summary>Weight of each UV0 component in the hierarchy simplification.</summary>
    private const float ClusterHierarchyUvWeight = 1.0f;

    /// <summary>
    /// Cooks a cluster hierarchy payload for one mesh (see <see cref="MeshletClusterHierarchyBuilder"/>).
    /// The clusters of every level become meshlets in the normal descriptor and index streams, with
    /// the level-0 meshlets first, so a renderer without cut selection reads the prefix
    /// [0, <see cref="MeshletPayload.Level0MeshletCount"/>). Each meshlet keeps its own culling sphere
    /// and normal cone from its own triangles. The hierarchy records go into
    /// <see cref="MeshletPayload.ClusterLods"/> and <see cref="MeshletPayload.LodGroups"/>.
    /// Simplification uses the normal and UV0 as attributes and keeps the original vertices.
    /// </summary>
    private static MeshletBuildResult BuildClusterHierarchyPayloadForMesh(
        XRMesh mesh,
        MeshletGenerationSettings settings,
        MeshLodGenerationSettings? lodSettings,
        string? sourceMeshIdentity,
        uint[] sourceIndices,
        MeshletVertex[] vertices)
    {
        int vertexCount = mesh.VertexCount;
        float[] positions = GetPositionArray(mesh);
        const int attributeStride = 5;
        float[] attributes = new float[vertexCount * attributeStride];
        for (int i = 0; i < vertexCount; i++)
        {
            Vector4 normal = vertices[i].Normal;
            Vector2 uv = vertices[i].TexCoord;
            int o = i * attributeStride;
            attributes[o + 0] = normal.X;
            attributes[o + 1] = normal.Y;
            attributes[o + 2] = normal.Z;
            attributes[o + 3] = uv.X;
            attributes[o + 4] = uv.Y;
        }
        float[] weights = mesh.HasTexCoords
            ? [ClusterHierarchyNormalWeight, ClusterHierarchyNormalWeight, ClusterHierarchyNormalWeight, ClusterHierarchyUvWeight, ClusterHierarchyUvWeight]
            : [ClusterHierarchyNormalWeight, ClusterHierarchyNormalWeight, ClusterHierarchyNormalWeight, 0.0f, 0.0f];

        MeshletClusterHierarchyBuilder.Config config = new(
            MaxVertices: settings.MaxVertices,
            MinTriangles: Math.Clamp(Math.Max(settings.MinTriangles, settings.MaxTriangles / 3u), 1u, settings.MaxTriangles),
            MaxTriangles: settings.MaxTriangles,
            SplitFactor: settings.SplitFactor);

        Interlocked.Increment(ref s_meshletBuildInvocationCount);
        RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordMeshletNativeBuilderEntry();
        MeshletClusterHierarchyBuilder.Result hierarchy = MeshletClusterHierarchyBuilder.Build(
            sourceIndices, positions, vertexCount, attributes, attributeStride, weights, vertexLock: null, config);

        // Level-0 clusters first, each group of levels in output order after them.
        IReadOnlyList<MeshletClusterHierarchyBuilder.Cluster> clusters = hierarchy.Clusters;
        int[] order = new int[clusters.Count];
        int level0Count = 0;
        for (int i = 0; i < clusters.Count; i++)
            if (clusters[i].RefinedGroup < 0)
                order[level0Count++] = i;
        int write = level0Count;
        for (int i = 0; i < clusters.Count; i++)
            if (clusters[i].RefinedGroup >= 0)
                order[write++] = i;

        List<uint> meshletVertices = new(sourceIndices.Length);
        List<byte> meshletTriangles = new(sourceIndices.Length * 2);
        CpuMeshletDescriptor[] descriptors = new CpuMeshletDescriptor[clusters.Count];
        Meshlet[] gpuMeshlets = new Meshlet[clusters.Count];
        MeshletClusterLodRecord[] records = new MeshletClusterLodRecord[clusters.Count];
        Dictionary<uint, byte> local = new(64);
        int encodedByteCount = 0;

        uint[] localVertices = new uint[256];
        byte[] localTriangles = new byte[((int)settings.MaxTriangles * 3 + 3) & ~3];
        for (int m = 0; m < order.Length; m++)
        {
            MeshletClusterHierarchyBuilder.Cluster cluster = clusters[order[m]];
            local.Clear();
            int localVertexCount = 0;
            int triangleByteCount = cluster.Indices.Length;
            for (int k = 0; k < cluster.Indices.Length; k++)
            {
                uint index = cluster.Indices[k];
                if (!local.TryGetValue(index, out byte slot))
                {
                    slot = (byte)localVertexCount;
                    local.Add(index, slot);
                    localVertices[localVertexCount++] = index;
                }
                localTriangles[k] = slot;
            }

            uint triangleCount = (uint)(cluster.Indices.Length / 3);
            Span<uint> clusterVertices = localVertices.AsSpan(0, localVertexCount);
            Span<byte> clusterTriangles = localTriangles.AsSpan(0, triangleByteCount);
            if (settings.OptimizeMeshlets)
                MeshOptimizerNative.OptimizeMeshletLevel(clusterVertices, clusterTriangles, settings.OptimizeLevel);

            MeshOptimizerBounds bounds = MeshOptimizerNative.ComputeMeshletBounds(clusterVertices, clusterTriangles, positions, vertexCount);
            MeshOptimizerMeshlet meshlet = new()
            {
                VertexOffset = (uint)meshletVertices.Count,
                TriangleOffset = (uint)meshletTriangles.Count,
                VertexCount = (uint)localVertexCount,
                TriangleCount = triangleCount,
            };

            foreach (uint v in clusterVertices)
                meshletVertices.Add(v);
            foreach (byte t in clusterTriangles)
                meshletTriangles.Add(t);
            // Each meshlet's triangle bytes start on a four-byte boundary.
            while ((meshletTriangles.Count & 3) != 0)
                meshletTriangles.Add(0);

            descriptors[m] = CreateDescriptor(meshlet, bounds);
            gpuMeshlets[m] = descriptors[m].ToGpuMeshlet();
            records[m] = new MeshletClusterLodRecord(cluster.Group, cluster.RefinedGroup);
        }

        uint[] vertexIndexArray = [.. meshletVertices];
        byte[] triangleArray = [.. meshletTriangles];
        MeshletLodGroupRecord[] groups = [.. hierarchy.Groups];
        MeshOptimizerMeshletStats stats = new(descriptors.Length, vertexIndexArray.Length, triangleArray.Length, encodedByteCount);
        MeshletPayload payload = CreatePayload(
            mesh,
            settings,
            lodSettings,
            sourceMeshIdentity,
            descriptors,
            vertexIndexArray,
            triangleArray,
            stats,
            level0Count,
            records,
            groups);

        LastClusterHierarchyStats = new ClusterHierarchyCookStats(
            descriptors.Length,
            level0Count,
            groups.Length,
            groups.Length == 0 ? 0 : groups.Max(static g => g.Depth) + 1,
            hierarchy.SloppyFallbackGroupCount,
            sourceIndices.Length / 3,
            (int)descriptors.Sum(static d => (long)d.TriangleCount));

        return new MeshletBuildResult
        {
            Meshlets = gpuMeshlets,
            VertexIndices = vertexIndexArray,
            TriangleIndices = triangleArray,
            Vertices = vertices,
            Stats = stats,
            Payload = payload,
        };
    }

    /// <summary>Counts of one cluster hierarchy cook.</summary>
    /// <param name="ClusterCount">Meshlets of every level.</param>
    /// <param name="Level0Count">Level-0 meshlets.</param>
    /// <param name="GroupCount">Groups.</param>
    /// <param name="LevelCount">Hierarchy levels (maximum group depth + 1).</param>
    /// <param name="SloppyGroupCount">Groups that only the sloppy fallback could simplify.</param>
    /// <param name="SourceTriangleCount">Triangles of the source mesh.</param>
    /// <param name="TotalTriangleCount">Triangles of all meshlets of every level.</param>
    public readonly record struct ClusterHierarchyCookStats(
        int ClusterCount,
        int Level0Count,
        int GroupCount,
        int LevelCount,
        int SloppyGroupCount,
        int SourceTriangleCount,
        int TotalTriangleCount);

    /// <summary>The counts of the last cluster hierarchy cook on this thread (for cook diagnostics).</summary>
    [ThreadStatic]
    public static ClusterHierarchyCookStats LastClusterHierarchyStats;
}
