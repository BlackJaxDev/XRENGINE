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
/// half (attribute-aware); split the result into new clusters. A group that keeps more than 85% of
/// its triangles, that simplifies to no triangles, or whose simplification changes its locked
/// border, ends its branch: its clusters stay as roots of the cut, and their vertices stay locked
/// for every later level.</item>
/// </list>
/// Locked group borders make any cut free of cracks: two neighbouring groups meet on the same
/// vertices at every level. The simplification keeps original vertices (no new or moved vertices),
/// so every cluster of every level refers to the original vertex buffer. There is no sloppy
/// fallback: it ignores attributes and seams, so a group that the attribute simplifier cannot
/// reduce ends its branch instead.
/// </summary>
public static class MeshletClusterHierarchyBuilder
{
    private const byte VertexLockBit = 1;
    private const byte VertexProtectBit = 2;
    private const byte VertexSeenBit = 0x80;

    /// <summary>
    /// Version of the hierarchy cook. The meshlet settings hash of a hierarchy payload includes it,
    /// so a change of the cook recooks cached hierarchy payloads and leaves level-0 payloads alone.
    /// Version 3 has no sloppy fallback and no placeholder triangle, rejects simplifications
    /// that change a locked border, and keeps ended branches locked.
    /// </summary>
    public const int CookVersion = 3;

    private static readonly Lock InvariantSync = new();
    private static InvariantReport _invariantTotals;

    /// <summary>
    /// Gets the sum of the crack-free check results of every hierarchy built in this process. The
    /// model cook writes it to meshlet-cache.log with its cluster hierarchy summary.
    /// </summary>
    public static InvariantReport InvariantTotals
    {
        get
        {
            lock (InvariantSync)
                return _invariantTotals;
        }
    }

    /// <summary>Adds the check results of one built hierarchy to <see cref="InvariantTotals"/>.</summary>
    public static void RecordInvariants(InvariantReport report)
    {
        lock (InvariantSync)
            _invariantTotals += report;
    }

    /// <summary>Configuration of the hierarchy cook.</summary>
    /// <param name="MaxVertices">Maximum vertices per cluster.</param>
    /// <param name="MinTriangles">Minimum triangles per cluster for the flexible cluster builder.</param>
    /// <param name="MaxTriangles">Maximum triangles per cluster.</param>
    /// <param name="PartitionSize">Target clusters per group (groups can be up to a third larger).</param>
    /// <param name="SplitFactor">Split factor of the flexible cluster builder.</param>
    /// <param name="SimplifyRatio">Triangle ratio that each simplification aims for.</param>
    /// <param name="SimplifyThreshold">A group that keeps more than this share of its triangles stops.</param>
    public readonly record struct Config(
        uint MaxVertices = 64u,
        uint MinTriangles = 41u,
        uint MaxTriangles = 124u,
        int PartitionSize = 16,
        float SplitFactor = 2.0f,
        float SimplifyRatio = 0.5f,
        float SimplifyThreshold = 0.85f);

    /// <summary>
    /// Results of the checks of the properties that make a cut free of cracks. The self, monotonic
    /// and containment counts are failures; zero for all three means the hierarchy passes. The
    /// border count is the number of rejected simplifications, which never reach the hierarchy.
    /// </summary>
    /// <param name="SelfChecks">Clusters made from a group whose own bound was compared with that group's record.</param>
    /// <param name="SelfFailures">Clusters whose own sphere or error differs from the record of the group that made them.</param>
    /// <param name="MonotonicChecks">Cluster links (own group to refined group) whose errors were compared.</param>
    /// <param name="MonotonicFailures">Links where the parent group's error is smaller than the refined group's error.</param>
    /// <param name="ContainmentChecks">Cluster links whose spheres were compared.</param>
    /// <param name="ContainmentFailures">Links where the parent group's sphere does not contain the refined group's sphere.</param>
    /// <param name="BorderChecks">Simplified groups whose locked border edges were compared before and after simplification.</param>
    /// <param name="BorderRejections">
    /// Simplifications that changed the open edges between locked vertices. The cook rejects them
    /// (the group ends its branch), so no accepted level has a changed border.
    /// </param>
    public readonly record struct InvariantReport(
        int SelfChecks,
        int SelfFailures,
        int MonotonicChecks,
        int MonotonicFailures,
        int ContainmentChecks,
        int ContainmentFailures,
        int BorderChecks,
        int BorderRejections)
    {
        /// <summary>Adds two reports.</summary>
        public static InvariantReport operator +(InvariantReport a, InvariantReport b)
            => new(
                a.SelfChecks + b.SelfChecks, a.SelfFailures + b.SelfFailures,
                a.MonotonicChecks + b.MonotonicChecks, a.MonotonicFailures + b.MonotonicFailures,
                a.ContainmentChecks + b.ContainmentChecks, a.ContainmentFailures + b.ContainmentFailures,
                a.BorderChecks + b.BorderChecks, a.BorderRejections + b.BorderRejections);
    }

    /// <summary>One cluster of the hierarchy: a triangle list into the original vertex buffer.</summary>
    /// <param name="Indices">Triangle list (three indices per triangle) into the mesh vertices.</param>
    /// <param name="Group">The group that holds the cluster.</param>
    /// <param name="RefinedGroup">The group that the cook simplified to make the cluster, or -1 for level 0.</param>
    public readonly record struct Cluster(uint[] Indices, int Group, int RefinedGroup);

    /// <summary>The cooked hierarchy, with clusters in output order and one record per group.</summary>
    public sealed class Result
    {
        internal Result(List<Cluster> clusters, List<MeshletLodGroupRecord> groups, int endedGroups, InvariantReport invariants)
        {
            Clusters = clusters;
            Groups = groups;
            EndedGroupCount = endedGroups;
            Invariants = invariants;
        }

        /// <summary>Gets the clusters of every level.</summary>
        public IReadOnlyList<Cluster> Clusters { get; }

        /// <summary>Gets the groups; <see cref="Cluster.Group"/> and <see cref="Cluster.RefinedGroup"/> index it.</summary>
        public IReadOnlyList<MeshletLodGroupRecord> Groups { get; }

        /// <summary>
        /// Gets the number of groups that ended their branch before the last level because they
        /// could not simplify below the threshold or simplified to no triangles.
        /// </summary>
        public int EndedGroupCount { get; }

        /// <summary>Gets the results of the crack-free checks of this hierarchy.</summary>
        public InvariantReport Invariants { get; }
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

        // Vertices (by position remap) of groups that ended their branch. Their clusters stay in the
        // cut at every coarser level, so later simplification must keep their borders.
        byte[] endedLocks = new byte[vertexCount];

        List<Cluster> outputClusters = [];
        List<MeshletLodGroupRecord> outputGroups = [];
        int endedGroups = 0;
        int selfChecks = 0;
        int selfFailures = 0;
        int borderChecks = 0;
        int borderRejections = 0;

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
            LockBoundary(locks, clusters, groupOffsets, clusterIndices, remap, vertexLock, endedLocks);

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
                uint[] simplified = Simplify(positions, vertexCount, attributes, attributeStride, attributeWeights, merged, locks, targetSize, out float simplifyError);

                bool simplifiable = simplified.Length != 0 &&
                    simplified.Length <= merged.Length * config.SimplifyThreshold &&
                    float.IsFinite(simplifyError);
                if (simplifiable)
                {
                    // The simplifier can still change the border around a locked vertex (for
                    // example by removing a triangle that a collapse made degenerate). A changed
                    // border would open a crack against the neighbour groups, so such a result
                    // is rejected and the group ends its branch.
                    borderChecks++;
                    if (!LockedBorderUnchanged(merged, simplified, locks, remap))
                    {
                        borderRejections++;
                        simplifiable = false;
                    }
                }

                if (!simplifiable)
                {
                    // The group cannot simplify further; it ends this branch of the hierarchy. Its
                    // clusters stay as roots of the cut, so their vertices stay locked from now on.
                    OutputGroup(outputClusters, outputGroups, clusters, first, count, clusterIndices, bounds.sphere, float.MaxValue, depth, ref selfChecks, ref selfFailures);
                    MarkEnded(endedLocks, clusters, first, count, clusterIndices, remap);
                    endedGroups++;
                    continue;
                }

                // Error and sphere grow monotonically from child clusters to the group.
                float groupError = MathF.Max(bounds.error, simplifyError);
                int refined = OutputGroup(outputClusters, outputGroups, clusters, first, count, clusterIndices, bounds.sphere, groupError, depth, ref selfChecks, ref selfFailures);

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
            OutputGroup(outputClusters, outputGroups, clusters, 0, 1, clusterIndices, clusters[0].Sphere, float.MaxValue, depth, ref selfChecks, ref selfFailures);

        InvariantReport links = CheckLinks(outputClusters, outputGroups);
        InvariantReport invariants = links + new InvariantReport(selfChecks, selfFailures, 0, 0, 0, 0, borderChecks, borderRejections);
        return new Result(outputClusters, outputGroups, endedGroups, invariants);
    }

    /// <summary>
    /// Locks every vertex (by position remap) of a group that ended its branch, for all later levels.
    /// </summary>
    private static void MarkEnded(byte[] endedLocks, List<WorkCluster> clusters, int first, int count, List<uint> clusterIndices, uint[] remap)
    {
        for (int i = 0; i < count; i++)
        {
            WorkCluster c = clusters[first + i];
            for (int k = 0; k < c.IndexCount; k++)
                endedLocks[remap[clusterIndices[c.IndexOffset + k]]] = 1;
        }
    }

    /// <summary>
    /// True when simplification kept the group's border: the open edges (edges of one triangle of the
    /// group) whose two vertices are locked are the same before and after, compared by position
    /// remap. These are the edges that the neighbour groups share, so an unchanged set means the
    /// neighbours still meet the group on the same vertices.
    /// </summary>
    private static bool LockedBorderUnchanged(uint[] before, uint[] after, byte[] locks, uint[] remap)
    {
        HashSet<ulong> beforeEdges = LockedOpenEdges(before, locks, remap);
        HashSet<ulong> afterEdges = LockedOpenEdges(after, locks, remap);
        return beforeEdges.SetEquals(afterEdges);
    }

    private static HashSet<ulong> LockedOpenEdges(uint[] triangles, byte[] locks, uint[] remap)
    {
        Dictionary<ulong, int> edgeUses = new(triangles.Length);
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                uint a = triangles[t + e];
                uint b = triangles[t + (e + 1) % 3];
                if ((locks[a] & VertexLockBit) == 0 || (locks[b] & VertexLockBit) == 0)
                    continue;
                uint ra = remap[a];
                uint rb = remap[b];
                if (ra == rb)
                    continue;
                ulong key = ra < rb ? ((ulong)ra << 32) | rb : ((ulong)rb << 32) | ra;
                edgeUses[key] = edgeUses.TryGetValue(key, out int uses) ? uses + 1 : 1;
            }
        }

        HashSet<ulong> open = [];
        foreach ((ulong key, int uses) in edgeUses)
            if (uses == 1)
                open.Add(key);
        return open;
    }

    /// <summary>
    /// Checks each link from a cluster's own group (its parent bound) to the group it was made from
    /// (its own bound): the parent error is at least the refined error, and the parent sphere
    /// contains the refined sphere (with a small relative tolerance for float rounding).
    /// </summary>
    private static InvariantReport CheckLinks(List<Cluster> clusters, List<MeshletLodGroupRecord> groups)
    {
        int checks = 0;
        int monotonicFailures = 0;
        int containmentFailures = 0;
        foreach (Cluster cluster in clusters)
        {
            if (cluster.RefinedGroup < 0)
                continue;
            checks++;
            MeshletLodGroupRecord parent = groups[cluster.Group];
            MeshletLodGroupRecord refined = groups[cluster.RefinedGroup];
            if (parent.SimplifiedError < refined.SimplifiedError)
                monotonicFailures++;
            Vector3 pc = new(parent.SimplifiedSphere.X, parent.SimplifiedSphere.Y, parent.SimplifiedSphere.Z);
            Vector3 rc = new(refined.SimplifiedSphere.X, refined.SimplifiedSphere.Y, refined.SimplifiedSphere.Z);
            float reach = Vector3.Distance(pc, rc) + refined.SimplifiedSphere.W;
            float limit = parent.SimplifiedSphere.W * 1.0001f + 1e-5f;
            if (reach > limit)
                containmentFailures++;
        }

        return new InvariantReport(0, 0, checks, monotonicFailures, checks, containmentFailures, 0, 0);
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
    private static void LockBoundary(byte[] locks, List<WorkCluster> clusters, int[] groupOffsets, List<uint> clusterIndices, uint[] remap, byte[]? vertexLock, byte[] endedLocks)
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
            locks[i] = (byte)((locks[r] & VertexLockBit) | (locks[i] & VertexProtectBit) |
                (endedLocks[r] != 0 ? VertexLockBit : 0));
            if (vertexLock is not null)
                locks[i] |= vertexLock[i];
        }
    }

    /// <summary>
    /// Attribute-aware simplification of a group's merged triangles toward the target, with the
    /// group borders locked. Returns the triangles (empty when nothing is left) and the absolute
    /// error that the simplifier reports.
    /// </summary>
    private static uint[] Simplify(
        float[] positions,
        int vertexCount,
        float[] attributes,
        int attributeStride,
        float[] attributeWeights,
        uint[] merged,
        byte[] locks,
        int targetCount,
        out float error)
    {
        uint[] lod = (uint[])merged.Clone();
        MeshOptimizerSimplifyOptions options = MeshOptimizerSimplifyOptions.Sparse | MeshOptimizerSimplifyOptions.ErrorAbsolute | MeshOptimizerSimplifyOptions.ErrorClamped;
        // The attribute variant is the one that takes vertex locks; the locks keep the group borders.
        int count = MeshOptimizerNative.SimplifyWithAttributes(lod, positions, vertexCount, attributes, attributeStride, attributeWeights, targetCount, float.MaxValue, options, locks, out error);

        return count == lod.Length ? lod : lod.AsSpan(0, count).ToArray();
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
        int depth,
        ref int selfChecks,
        ref int selfFailures)
    {
        int group = outputGroups.Count;
        outputGroups.Add(new MeshletLodGroupRecord(simplifiedSphere, simplifiedError, depth));
        for (int i = 0; i < count; i++)
        {
            WorkCluster c = clusters[first + i];
            // A cluster made from a group must carry exactly that group's bound as its own bound:
            // the cut tests the same values on both sides of the link.
            if (c.Refined >= 0)
            {
                selfChecks++;
                MeshletLodGroupRecord made = outputGroups[c.Refined];
                if (c.Sphere != made.SimplifiedSphere || c.Error != made.SimplifiedError)
                    selfFailures++;
            }
            uint[] clusterTriangles = new uint[c.IndexCount];
            clusterIndices.CopyTo(c.IndexOffset, clusterTriangles, 0, c.IndexCount);
            outputClusters.Add(new Cluster(clusterTriangles, group, c.Refined));
        }

        return group;
    }
}
