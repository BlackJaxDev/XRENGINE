using System.Numerics;

namespace XREngine.Rendering.Meshlets;

public static partial class MeshOptimizerIntegration
{
    /// <summary>Weight of each normal component in the hierarchy simplification.</summary>
    private const float ClusterHierarchyNormalWeight = 0.5f;
    /// <summary>Weight of each UV0 component in the hierarchy simplification.</summary>
    private const float ClusterHierarchyUvWeight = 1.0f;
    /// <summary>
    /// Weight of each component of the skinning centroid in the hierarchy simplification of a skinned
    /// mesh. The centroid is the weighted mean of the bind-space joint positions of a vertex's bones,
    /// so two vertices that bend with different bones differ in it, and simplification keeps cluster
    /// borders along the skinning.
    /// </summary>
    private const float ClusterHierarchySkinWeight = 0.5f;

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
        SkinnedHierarchyInput? skin = SkinnedHierarchyInput.TryRead(mesh);
        int attributeStride = skin is null ? 5 : 8;
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
            if (skin is not null)
            {
                Vector3 centroid = skin.Centroids[i];
                attributes[o + 5] = centroid.X;
                attributes[o + 6] = centroid.Y;
                attributes[o + 7] = centroid.Z;
            }
        }
        float uvWeight = mesh.HasTexCoords ? ClusterHierarchyUvWeight : 0.0f;
        float[] weights = skin is null
            ? [ClusterHierarchyNormalWeight, ClusterHierarchyNormalWeight, ClusterHierarchyNormalWeight, uvWeight, uvWeight]
            : [ClusterHierarchyNormalWeight, ClusterHierarchyNormalWeight, ClusterHierarchyNormalWeight, uvWeight, uvWeight,
               ClusterHierarchySkinWeight, ClusterHierarchySkinWeight, ClusterHierarchySkinWeight];

        MeshletClusterHierarchyBuilder.Config config = new(
            MaxVertices: settings.MaxVertices,
            MinTriangles: Math.Clamp(Math.Max(settings.MinTriangles, settings.MaxTriangles / 3u), 1u, settings.MaxTriangles),
            MaxTriangles: settings.MaxTriangles,
            SplitFactor: settings.SplitFactor);

        Interlocked.Increment(ref s_meshletBuildInvocationCount);
        RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordMeshletNativeBuilderEntry();
        MeshletClusterHierarchyBuilder.Result hierarchy = MeshletClusterHierarchyBuilder.Build(
            sourceIndices, positions, vertexCount, attributes, attributeStride, weights, skin?.MorphLocks, config);

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
        MeshletClusterSkinRecord[]? skinRecords = skin is null ? null : new MeshletClusterSkinRecord[clusters.Count];
        List<ushort>? clusterBones = skin is null ? null : new List<ushort>(clusters.Count * 4);
        int[]? boneStamp = skin is null ? null : new int[skin.BoneCount];
        int maxClusterBones = 0;
        int morphClusterCount = 0;
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
            if (skin is not null)
            {
                // The union of the bones of the cluster's vertices (each bone once), and the morph
                // flag when any vertex has a blendshape delta.
                uint firstBone = (uint)clusterBones!.Count;
                bool morph = skin.MorphUnknown;
                foreach (uint vertex in clusterVertices)
                {
                    morph |= skin.MorphLocks is not null && skin.MorphLocks[vertex] != 0;
                    for (int b = skin.VertexBoneStart[vertex]; b < skin.VertexBoneStart[vertex + 1]; b++)
                    {
                        ushort bone = skin.VertexBones[b];
                        if (boneStamp![bone] == m + 1)
                            continue;
                        boneStamp[bone] = m + 1;
                        clusterBones.Add(bone);
                    }
                }
                int boneCount = clusterBones.Count - (int)firstBone;
                if (boneCount > ushort.MaxValue)
                    throw new InvalidDataException($"Cluster {m} of a skinned hierarchy has {boneCount} bones.");
                maxClusterBones = Math.Max(maxClusterBones, boneCount);
                if (morph)
                    morphClusterCount++;
                skinRecords![m] = new MeshletClusterSkinRecord(
                    firstBone,
                    (ushort)boneCount,
                    morph ? MeshletClusterSkinRecord.MorphFlag : (ushort)0);
            }
        }

        uint[] vertexIndexArray = [.. meshletVertices];
        byte[] triangleArray = [.. meshletTriangles];
        MeshletLodGroupRecord[] groups = [.. hierarchy.Groups];
        // A skinned mesh gets a deformation order, so a far instance can deform only the vertices
        // that its coarsest allowed level reads (see MeshletDeformationOrderBuilder).
        MeshletDeformationOrderBuilder.Result? deformationOrder = skin is null
            ? null
            : MeshletDeformationOrderBuilder.Build(descriptors, vertexIndexArray, records, groups, vertexCount);
        // The level covers let the directional shadow raster draw a far instance from only its
        // deformed vertices (see MeshletLevelCoverBuilder).
        MeshletLevelCoverBuilder.Result? levelCovers = skin is null
            ? null
            : MeshletLevelCoverBuilder.Build(descriptors, vertexIndexArray, triangleArray, records, groups);
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
            groups,
            skinRecords,
            clusterBones?.ToArray(),
            deformationOrder,
            levelCovers);

        LastClusterHierarchyStats = new ClusterHierarchyCookStats(
            descriptors.Length,
            level0Count,
            groups.Length,
            groups.Length == 0 ? 0 : groups.Max(static g => g.Depth) + 1,
            hierarchy.EndedGroupCount,
            sourceIndices.Length / 3,
            (int)descriptors.Sum(static d => (long)d.TriangleCount),
            skin is null ? 0 : vertexCount,
            skin?.MorphVertexCount ?? 0,
            clusterBones?.Count ?? 0,
            maxClusterBones,
            morphClusterCount,
            skin?.NonFiniteCentroidCount ?? 0);
        MeshletClusterHierarchyBuilder.RecordInvariants(hierarchy.Invariants);

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
    /// <param name="EndedGroupCount">Groups that ended their branch before the last level.</param>
    /// <param name="SourceTriangleCount">Triangles of the source mesh.</param>
    /// <param name="TotalTriangleCount">Triangles of all meshlets of every level.</param>
    /// <param name="SkinnedVertexCount">Vertices of a skinned mesh (0 for an unskinned mesh).</param>
    /// <param name="MorphLockedVertexCount">Vertices locked because they have a blendshape delta.</param>
    /// <param name="ClusterBoneCount">Entries of the cluster bone list (the sum of the bones of every cluster).</param>
    /// <param name="MaxClusterBones">The most bones of one cluster.</param>
    /// <param name="MorphClusterCount">Clusters with a vertex that has a blendshape delta.</param>
    /// <param name="NonFiniteCentroidCount">Vertices whose skinning centroid was not finite and was zeroed.</param>
    public readonly record struct ClusterHierarchyCookStats(
        int ClusterCount,
        int Level0Count,
        int GroupCount,
        int LevelCount,
        int EndedGroupCount,
        int SourceTriangleCount,
        int TotalTriangleCount,
        int SkinnedVertexCount = 0,
        int MorphLockedVertexCount = 0,
        int ClusterBoneCount = 0,
        int MaxClusterBones = 0,
        int MorphClusterCount = 0,
        int NonFiniteCentroidCount = 0);

    /// <summary>
    /// The skinning input of a skinned hierarchy cook, read once from the mesh: for each vertex its
    /// bones (a compressed list with one start per vertex), its skinning centroid (the weighted mean
    /// of its bones' bind-space joint positions) and whether it has a blendshape delta.
    /// </summary>
    private sealed class SkinnedHierarchyInput
    {
        public required int BoneCount { get; init; }
        public required int[] VertexBoneStart { get; init; }
        public required ushort[] VertexBones { get; init; }
        public required Vector3[] Centroids { get; init; }
        /// <summary>One entry per vertex, 1 when the vertex has a blendshape delta; null when the mesh has none or they cannot be read.</summary>
        public byte[]? MorphLocks { get; init; }
        public int MorphVertexCount { get; init; }
        /// <summary>True when the mesh has blendshapes but the per-vertex counts cannot be read; every cluster is then flagged as morph.</summary>
        public bool MorphUnknown { get; init; }
        /// <summary>Vertices whose skinning centroid was not finite and was zeroed.</summary>
        public int NonFiniteCentroidCount { get; init; }

        public static SkinnedHierarchyInput? TryRead(XRMesh mesh)
        {
            if (!mesh.HasSkinning || !XRMeshSkinningInfluenceReader.TryCreate(mesh, out XRMeshSkinningInfluenceReader reader))
                return null;

            var bones = mesh.UtilizedBones;
            if (bones.Length > ushort.MaxValue + 1)
                return null;

            // The palette maps a source position into bone space with BindRoot * inverseBind (the
            // convention of the skinned culling volumes); the joint is that bone space's origin.
            Matrix4x4 bindRoot = mesh.BindRootMatrix ?? Matrix4x4.Identity;
            Vector3[] joints = new Vector3[bones.Length];
            for (int b = 0; b < bones.Length; b++)
                joints[b] = Matrix4x4.Invert(bindRoot * bones[b].invBindWorldMtx, out Matrix4x4 boneToSource)
                    ? boneToSource.Translation
                    : Vector3.Zero;

            int vertexCount = mesh.VertexCount;
            int[] start = new int[vertexCount + 1];
            List<ushort> vertexBones = new(vertexCount * 2);
            Vector3[] centroids = new Vector3[vertexCount];
            int nonFiniteCentroids = 0;
            Span<int> boneScratch = stackalloc int[Math.Max(1, reader.MaxInfluenceCount)];
            Span<float> weightScratch = stackalloc float[Math.Max(1, reader.MaxInfluenceCount)];
            for (int v = 0; v < vertexCount; v++)
            {
                start[v] = vertexBones.Count;
                int count = reader.ReadInfluences(v, boneScratch, weightScratch);
                Vector3 sum = Vector3.Zero;
                float weightSum = 0.0f;
                for (int i = 0; i < count; i++)
                {
                    int bone = boneScratch[i];
                    float weight = weightScratch[i];
                    if ((uint)bone >= (uint)bones.Length || !(weight > 0.0f))
                        continue;
                    vertexBones.Add((ushort)bone);
                    sum += joints[bone] * weight;
                    weightSum += weight;
                }
                Vector3 centroid = weightSum > 0.0f ? sum / weightSum : Vector3.Zero;
                // A degenerate bind matrix can give a non-finite joint; a non-finite attribute
                // would make every simplification error non-finite, so such a centroid is zeroed.
                if (!float.IsFinite(centroid.X) || !float.IsFinite(centroid.Y) || !float.IsFinite(centroid.Z))
                {
                    centroid = Vector3.Zero;
                    nonFiniteCentroids++;
                }
                centroids[v] = centroid;
            }
            start[vertexCount] = vertexBones.Count;

            byte[]? morphLocks = null;
            int morphCount = 0;
            bool morphUnknown = false;
            if (mesh.HasBlendshapes)
            {
                morphLocks = ReadMorphVertices(mesh, out morphCount);
                morphUnknown = morphLocks is null;
            }

            return new SkinnedHierarchyInput
            {
                BoneCount = bones.Length,
                VertexBoneStart = start,
                VertexBones = [.. vertexBones],
                Centroids = centroids,
                MorphLocks = morphLocks,
                MorphVertexCount = morphCount,
                MorphUnknown = morphUnknown,
                NonFiniteCentroidCount = nonFiniteCentroids,
            };
        }

        /// <summary>
        /// Reads which vertices have a blendshape delta from the per-vertex blendshape counts (two
        /// components per vertex: the first delta record and the record count, as int or float).
        /// Returns null when the counts have no CPU copy.
        /// </summary>
        private static unsafe byte[]? ReadMorphVertices(XRMesh mesh, out int morphCount)
        {
            morphCount = 0;
            XRDataBuffer? counts = mesh.BlendshapeCounts;
            int vertexCount = mesh.VertexCount;
            if (counts is null || counts.Address == VoidPtr.Zero || counts.ComponentCount != 2u || counts.ElementCount < (uint)vertexCount)
                return null;

            byte[] locks = new byte[vertexCount];
            bool integral = counts.ComponentType == EComponentType.Int;
            for (int v = 0; v < vertexCount; v++)
            {
                int recordCount = integral
                    ? ((int*)counts.Address)[v * 2 + 1]
                    : (int)((float*)counts.Address)[v * 2 + 1];
                if (recordCount <= 0)
                    continue;
                locks[v] = 1;
                morphCount++;
            }
            return locks;
        }
    }

    /// <summary>The counts of the last cluster hierarchy cook on this thread (for cook diagnostics).</summary>
    [ThreadStatic]
    public static ClusterHierarchyCookStats LastClusterHierarchyStats;
}
