// Ported from meshoptimizer demo/clusterlod.h (clodBuild and its helpers).
//
// Copyright (C) 2016-2026, by Arseny Kapoulkine (arseny.kapoulkine@gmail.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
// associated documentation files (the "Software"), to deal in the Software without restriction,
// including without limitation the rights to use, copy, modify, merge, publish, distribute,
// sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
// NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES
// OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
// CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System.Numerics;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// Builds a cluster level-of-detail hierarchy (a DAG of cluster groups) for one triangle mesh, for
/// cut selection at run time. The algorithm follows meshoptimizer's demo/clusterlod.h:
/// <list type="number">
/// <item>Split the mesh into clusters (level 0).</item>
/// <item>Repeat while more than one cluster is left: put about 16 neighbouring clusters into a group;
/// lock the vertices on the borders between groups; merge and simplify each group's triangles to
/// half; split the result into new clusters. A group that cannot reach 85% of its triangle count
/// stops there.</item>
/// </list>
/// Locked group borders make any cut free of cracks: two neighbouring groups meet on the same
/// vertices at every level. The simplification keeps original vertices (no new or moved vertices),
/// so every cluster of every level refers to the original vertex buffer.
/// </summary>
public static class MeshletClusterHierarchyBuilder
{
    private const byte VertexLockBit = 1;
    private const byte VertexProtectBit = 2;
    private const byte VertexSeenBit = 0x80;

    /// <summary>Configuration of the hierarchy cook.</summary>
    /// <param name="MaxVertices">Maximum vertices per cluster.</param>
    /// <param name="MinTriangles">Minimum triangles per cluster for the flexible cluster builder.</param>
    /// <param name="MaxTriangles">Maximum triangles per cluster.</param>
    /// <param name="PartitionSize">Target clusters per group (groups can be up to a third larger).</param>
    /// <param name="SplitFactor">Split factor of the flexible cluster builder.</param>
    /// <param name="SimplifyRatio">Triangle ratio that each simplification aims for.</param>
    /// <param name="SimplifyThreshold">A group that keeps more than this share of its triangles stops.</param>
    /// <param name="SloppyErrorFactor">Error factor for groups that only the sloppy fallback can simplify.</param>
    public readonly record struct Config(
        uint MaxVertices = 64u,
        uint MinTriangles = 41u,
        uint MaxTriangles = 124u,
        int PartitionSize = 16,
        float SplitFactor = 2.0f,
        float SimplifyRatio = 0.5f,
        float SimplifyThreshold = 0.85f,
        float SloppyErrorFactor = 2.0f);

    /// <summary>One cluster of the hierarchy: a triangle list into the original vertex buffer.</summary>
    /// <param name="Indices">Triangle list (three indices per triangle) into the mesh vertices.</param>
    /// <param name="Group">The group that holds the cluster.</param>
    /// <param name="RefinedGroup">The group that the cook simplified to make the cluster, or -1 for level 0.</param>
    public readonly record struct Cluster(uint[] Indices, int Group, int RefinedGroup);

    /// <summary>The cooked hierarchy, with clusters in output order and one record per group.</summary>
    public sealed class Result
    {
        internal Result(List<Cluster> clusters, List<MeshletLodGroupRecord> groups, int simplifyFallbackGroups)
        {
            Clusters = clusters;
            Groups = groups;
            SloppyFallbackGroupCount = simplifyFallbackGroups;
        }

        /// <summary>Gets the clusters of every level.</summary>
        public IReadOnlyList<Cluster> Clusters { get; }

        /// <summary>Gets the groups; <see cref="Cluster.Group"/> and <see cref="Cluster.RefinedGroup"/> index it.</summary>
        public IReadOnlyList<MeshletLodGroupRecord> Groups { get; }

        /// <summary>Gets the number of groups that only the sloppy fallback could simplify.</summary>
        public int SloppyFallbackGroupCount { get; }
    }

    private struct WorkCluster
    {
        public int VertexCount;
        public int IndexOffset;
        public int IndexCount;
        public int Refined;
        public Vector4 Sphere;
        public float Error;
    }

    /// <summary>
    /// Builds the hierarchy.
    /// </summary>
    /// <param name="indices">Triangle list of the mesh.</param>
    /// <param name="positions">Three floats per vertex.</param>
    /// <param name="vertexCount">Number of vertices.</param>
    /// <param name="attributes">Attribute floats per vertex for attribute-aware simplification, or empty.</param>
    /// <param name="attributeStride">Floats per vertex in <paramref name="attributes"/>.</param>
    /// <param name="attributeWeights">Weight of each attribute float.</param>
    /// <param name="vertexLock">Optional extra vertex locks (for example vertices with blendshape deltas).</param>
    /// <param name="config">Cook configuration.</param>
    public static Result Build(
        uint[] indices,
        float[] positions,
        int vertexCount,
        float[] attributes,
        int attributeStride,
        float[] attributeWeights,
        byte[]? vertexLock,
        Config config)
    {
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(positions);
        if (indices.Length == 0 || indices.Length % 3 != 0)
            throw new ArgumentException("The mesh needs a non-empty triangle list.", nameof(indices));
        if (positions.Length != vertexCount * 3)
            throw new ArgumentException("Positions must hold three floats for every vertex.", nameof(positions));
        if (vertexLock is not null && vertexLock.Length != vertexCount)
            throw new ArgumentException("The vertex lock array must have one entry for every vertex.", nameof(vertexLock));

        uint[] remap = new uint[vertexCount];
        MeshOptimizerNative.GeneratePositionRemap(remap, positions, vertexCount, sizeof(float) * 3);
        byte[] locks = new byte[vertexCount];

        List<Cluster> outputClusters = [];
        List<MeshletLodGroupRecord> outputGroups = [];
        int sloppyGroups = 0;

        List<uint> clusterIndices = new(indices.Length);
        List<WorkCluster> clusters = [];
        Clusterize(clusters, clusterIndices, config, positions, vertexCount, indices, -1);
        for (int i = 0; i < clusters.Count; i++)
        {
            WorkCluster cluster = clusters[i];
            MeshOptimizerBounds bounds = MeshOptimizerNative.ComputeClusterBounds(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(clusterIndices).Slice(cluster.IndexOffset, cluster.IndexCount),
                positions, vertexCount, sizeof(float) * 3);
            cluster.Sphere = new Vector4(bounds.CenterX, bounds.CenterY, bounds.CenterZ, bounds.Radius);
            cluster.Error = 0.0f;
            clusters[i] = cluster;
        }

        List<WorkCluster> pending = new(clusters.Count);
        List<uint> pendingIndices = new((int)(clusterIndices.Count * config.SimplifyThreshold));
        int depth = 0;

        while (clusters.Count > 1)
        {
            int[] groupOffsets = Partition(ref clusters, config, positions, clusterIndices, remap);
            pending.Clear();
            pendingIndices.Clear();
            LockBoundary(locks, clusters, groupOffsets, clusterIndices, remap, vertexLock);

            for (int g = 0; g + 1 < groupOffsets.Length; g++)
            {
                int first = groupOffsets[g];
                int count = groupOffsets[g + 1] - first;

                int mergedCount = 0;
                for (int j = 0; j < count; j++)
                    mergedCount += clusters[first + j].IndexCount;
                uint[] merged = new uint[mergedCount];
                int write = 0;
                for (int j = 0; j < count; j++)
                {
                    WorkCluster c = clusters[first + j];
                    clusterIndices.CopyTo(c.IndexOffset, merged, write, c.IndexCount);
                    write += c.IndexCount;
                }

                int targetSize = (int)(merged.Length / 3 * config.SimplifyRatio) * 3;
                (Vector4 sphere, float error) bounds = MergeBounds(clusters, first, count);
                uint[] simplified = Simplify(config, positions, vertexCount, attributes, attributeStride, attributeWeights, merged, locks, targetSize, out float simplifyError, out bool usedSloppy);

                if (simplified.Length > merged.Length * config.SimplifyThreshold)
                {
                    // The group cannot simplify further; it ends this branch of the hierarchy.
                    OutputGroup(outputClusters, outputGroups, clusters, first, count, clusterIndices, bounds.sphere, float.MaxValue, depth);
                    continue;
                }

                if (usedSloppy)
                    sloppyGroups++;

                // Error and sphere grow monotonically from child clusters to the group.
                float groupError = MathF.Max(bounds.error, simplifyError);
                int refined = OutputGroup(outputClusters, outputGroups, clusters, first, count, clusterIndices, bounds.sphere, groupError, depth);

                int clusterOffset = pending.Count;
                Clusterize(pending, pendingIndices, config, positions, vertexCount, simplified, refined);
                for (int j = clusterOffset; j < pending.Count; j++)
                {
                    WorkCluster c = pending[j];
                    c.Sphere = bounds.sphere;
                    c.Error = groupError;
                    pending[j] = c;
                }
            }

            (clusters, pending) = (pending, clusters);
            (clusterIndices, pendingIndices) = (pendingIndices, clusterIndices);
            depth++;
        }

        if (clusters.Count == 1)
            OutputGroup(outputClusters, outputGroups, clusters, 0, 1, clusterIndices, clusters[0].Sphere, float.MaxValue, depth);

        return new Result(outputClusters, outputGroups, sloppyGroups);
    }

    private static void Clusterize(
        List<WorkCluster> clusters,
        List<uint> clusterIndices,
        Config config,
        float[] positions,
        int vertexCount,
        uint[] indices,
        int refined)
    {
        nuint maxMeshlets = MeshOptimizerNative.BuildMeshletsBound((nuint)indices.Length, config.MaxVertices, config.MinTriangles);
        MeshOptimizerMeshlet[] meshlets = new MeshOptimizerMeshlet[(int)maxMeshlets];
        uint[] meshletVertices = new uint[indices.Length];
        byte[] meshletTriangles = new byte[indices.Length + 3];
        nuint meshletCount = MeshOptimizerNative.BuildNativeMeshletClusters(
            MeshletBuildMode.Flex,
            meshlets,
            meshletVertices,
            meshletTriangles,
            indices,
            positions,
            (nuint)vertexCount,
            config.MaxVertices,
            config.MinTriangles,
            config.MaxTriangles,
            0.0f,
            config.SplitFactor,
            0.0f);

        for (int i = 0; i < (int)meshletCount; i++)
        {
            MeshOptimizerMeshlet meshlet = meshlets[i];
            int indexCount = (int)meshlet.TriangleCount * 3;
            int indexOffset = clusterIndices.Count;
            for (int j = 0; j < indexCount; j++)
                clusterIndices.Add(meshletVertices[meshlet.VertexOffset + meshletTriangles[meshlet.TriangleOffset + j]]);

            clusters.Add(new WorkCluster
            {
                VertexCount = (int)meshlet.VertexCount,
                IndexOffset = indexOffset,
                IndexCount = indexCount,
                Refined = refined,
            });
        }
    }

    /// <summary>
    /// Groups the clusters (about <see cref="Config.PartitionSize"/> each) and reorders them so that
    /// each group is contiguous. Returns the group offsets (group i is [offsets[i], offsets[i+1])).
    /// </summary>
    private static int[] Partition(ref List<WorkCluster> clusters, Config config, float[] positions, List<uint> clusterIndices, uint[] remap)
    {
        if (clusters.Count <= config.PartitionSize)
            return [0, clusters.Count];

        uint[] counts = new uint[clusters.Count];
        uint[] remapped = new uint[clusterIndices.Count];
        int write = 0;
        for (int i = 0; i < clusters.Count; i++)
        {
            WorkCluster c = clusters[i];
            counts[i] = (uint)c.IndexCount;
            for (int j = 0; j < c.IndexCount; j++)
                remapped[write++] = remap[clusterIndices[c.IndexOffset + j]];
        }

        uint[] part = new uint[clusters.Count];
        int partitionCount = MeshOptimizerNative.PartitionClusters(part, remapped.AsSpan(0, write), counts, positions, remap.Length, sizeof(float) * 3, config.PartitionSize);

        int[] offsets = new int[partitionCount + 1];
        for (int i = 0; i < clusters.Count; i++)
            offsets[part[i] + 1]++;
        for (int i = 1; i <= partitionCount; i++)
            offsets[i] += offsets[i - 1];

        int[] cursor = (int[])offsets.Clone();
        WorkCluster[] result = new WorkCluster[clusters.Count];
        for (int i = 0; i < clusters.Count; i++)
            result[cursor[part[i]]++] = clusters[i];

        clusters = [.. result];
        return offsets;
    }

    /// <summary>
    /// Locks every vertex that clusters of two different groups use, so simplification keeps the
    /// group borders. Vertices with the same position lock together (through the position remap).
    /// </summary>
    private static void LockBoundary(byte[] locks, List<WorkCluster> clusters, int[] groupOffsets, List<uint> clusterIndices, uint[] remap, byte[]? vertexLock)
    {
        for (int i = 0; i < locks.Length; i++)
            locks[i] &= unchecked((byte)~(VertexLockBit | VertexSeenBit));

        for (int g = 0; g + 1 < groupOffsets.Length; g++)
        {
            // A vertex that an earlier group used becomes locked.
            for (int c = groupOffsets[g]; c < groupOffsets[g + 1]; c++)
            {
                WorkCluster cluster = clusters[c];
                for (int k = 0; k < cluster.IndexCount; k++)
                {
                    uint r = remap[clusterIndices[cluster.IndexOffset + k]];
                    locks[r] |= (byte)(locks[r] >> 7);
                }
            }

            for (int c = groupOffsets[g]; c < groupOffsets[g + 1]; c++)
            {
                WorkCluster cluster = clusters[c];
                for (int k = 0; k < cluster.IndexCount; k++)
                    locks[remap[clusterIndices[cluster.IndexOffset + k]]] |= VertexSeenBit;
            }
        }

        for (int i = 0; i < locks.Length; i++)
        {
            uint r = remap[i];
            locks[i] = (byte)((locks[r] & VertexLockBit) | (locks[i] & VertexProtectBit));
            if (vertexLock is not null)
                locks[i] |= vertexLock[i];
        }
    }

    private static uint[] Simplify(
        Config config,
        float[] positions,
        int vertexCount,
        float[] attributes,
        int attributeStride,
        float[] attributeWeights,
        uint[] merged,
        byte[] locks,
        int targetCount,
        out float error,
        out bool usedSloppy)
    {
        usedSloppy = false;
        uint[] lod = (uint[])merged.Clone();
        MeshOptimizerSimplifyOptions options = MeshOptimizerSimplifyOptions.Sparse | MeshOptimizerSimplifyOptions.ErrorAbsolute | MeshOptimizerSimplifyOptions.ErrorClamped;
        // The attribute variant is the one that takes vertex locks; the locks keep the group borders.
        int count = MeshOptimizerNative.SimplifyWithAttributes(lod, positions, vertexCount, attributes, attributeStride, attributeWeights, targetCount, float.MaxValue, options, locks, out error);

        if (count > targetCount)
        {
            lod = SimplifySloppy(positions, merged, locks, targetCount, out error);
            error *= config.SloppyErrorFactor;
            count = lod.Length;
            usedSloppy = true;
        }

        // A degenerate result keeps the first triangle, so no cluster is empty.
        if (count == 0)
            return [merged[0], merged[1], merged[2]];

        return count == lod.Length ? lod : lod.AsSpan(0, count).ToArray();
    }

    /// <summary>
    /// Sloppy simplification of the merged triangles. The subset is de-indexed first, so the call
    /// does not process the whole vertex buffer; the error is converted to absolute units.
    /// </summary>
    private static uint[] SimplifySloppy(float[] positions, uint[] merged, byte[] locks, int targetCount, out float error)
    {
        int n = merged.Length;
        float[] subset = new float[n * 3];
        byte[] subsetLocks = new byte[n];
        uint[] lod = new uint[n];
        for (int i = 0; i < n; i++)
        {
            uint v = merged[i];
            subset[i * 3 + 0] = positions[v * 3 + 0];
            subset[i * 3 + 1] = positions[v * 3 + 1];
            subset[i * 3 + 2] = positions[v * 3 + 2];
            subsetLocks[i] = locks[v];
            lod[i] = (uint)i;
        }

        int count = MeshOptimizerNative.SimplifySloppy(lod, subset, n, targetCount, float.MaxValue, subsetLocks, out error);
        error *= MeshOptimizerNative.SimplifyScale(subset, n);
        uint[] result = new uint[count];
        for (int i = 0; i < count; i++)
            result[i] = merged[lod[i]];
        return result;
    }

    /// <summary>
    /// Merges the cluster spheres of a group into one sphere that contains them all, and takes the
    /// largest cluster error, so group bounds never shrink below their children.
    /// </summary>
    private static (Vector4 Sphere, float Error) MergeBounds(List<WorkCluster> clusters, int first, int count)
    {
        float[] centers = new float[count * 3];
        float[] radii = new float[count];
        float error = 0.0f;
        for (int i = 0; i < count; i++)
        {
            WorkCluster c = clusters[first + i];
            centers[i * 3 + 0] = c.Sphere.X;
            centers[i * 3 + 1] = c.Sphere.Y;
            centers[i * 3 + 2] = c.Sphere.Z;
            radii[i] = c.Sphere.W;
            error = MathF.Max(error, c.Error);
        }

        MeshOptimizerBounds merged = MeshOptimizerNative.ComputeSphereBounds(centers, count, sizeof(float) * 3, radii, sizeof(float));
        return (new Vector4(merged.CenterX, merged.CenterY, merged.CenterZ, merged.Radius), error);
    }

    private static int OutputGroup(
        List<Cluster> outputClusters,
        List<MeshletLodGroupRecord> outputGroups,
        List<WorkCluster> clusters,
        int first,
        int count,
        List<uint> clusterIndices,
        Vector4 simplifiedSphere,
        float simplifiedError,
        int depth)
    {
        int group = outputGroups.Count;
        outputGroups.Add(new MeshletLodGroupRecord(simplifiedSphere, simplifiedError, depth));
        for (int i = 0; i < count; i++)
        {
            WorkCluster c = clusters[first + i];
            uint[] clusterTriangles = new uint[c.IndexCount];
            clusterIndices.CopyTo(c.IndexOffset, clusterTriangles, 0, c.IndexCount);
            outputClusters.Add(new Cluster(clusterTriangles, group, c.Refined));
        }

        return group;
    }
}
