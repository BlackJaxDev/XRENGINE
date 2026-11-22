namespace XREngine.Rendering.Meshlets;

/// <summary>
/// Builds the level cover index ranges of a skinned cluster hierarchy mesh. The cover of level
/// <c>L</c> is the triangle list of every cluster of level <c>L</c> and every root cluster of a
/// finer level. It is the cut that the clamped skinned cluster cut selects at an infinite
/// distance, so it covers the mesh once and reads only the vertices in the deformation prefix of
/// level <c>L</c>. A path without the cut (the directional shadow raster) draws the cover of a
/// clamped draw instead of the canonical level-0 index buffer, whose vertices the deformation job
/// of that draw did not deform.
/// </summary>
public static class MeshletLevelCoverBuilder
{
    /// <summary>The level cover ranges of one mesh.</summary>
    /// <param name="Indices">
    /// Source vertex indices of every cover, three per triangle, the covers of levels 1 and coarser
    /// one after the other.
    /// </param>
    /// <param name="LevelFirstIndex">
    /// One entry per hierarchy level: the first entry of the cover of that level in
    /// <paramref name="Indices"/>. Level 0 has no cover (the canonical index buffer draws it), so
    /// its entry is 0.
    /// </param>
    /// <param name="LevelIndexCount">
    /// One entry per hierarchy level: the index count of the cover of that level. Level 0 has 0.
    /// </param>
    public readonly record struct Result(uint[] Indices, uint[] LevelFirstIndex, uint[] LevelIndexCount);

    /// <summary>
    /// Builds the level covers from the meshlet streams and hierarchy tables of a payload.
    /// </summary>
    /// <param name="meshlets">The meshlet descriptors of every level.</param>
    /// <param name="vertexIndices">The source vertex index of each meshlet vertex.</param>
    /// <param name="triangleIndices">The meshlet-local vertex index of each triangle corner, one byte each.</param>
    /// <param name="clusterLods">The hierarchy record of each meshlet.</param>
    /// <param name="groups">The hierarchy groups.</param>
    /// <exception cref="InvalidDataException">A table refers to a group, vertex or triangle out of range.</exception>
    public static Result Build(
        ReadOnlySpan<CpuMeshletDescriptor> meshlets,
        ReadOnlySpan<uint> vertexIndices,
        ReadOnlySpan<byte> triangleIndices,
        ReadOnlySpan<MeshletClusterLodRecord> clusterLods,
        ReadOnlySpan<MeshletLodGroupRecord> groups)
    {
        if (clusterLods.Length != meshlets.Length)
            throw new InvalidDataException("The level covers need one hierarchy record per meshlet.");

        int levelCount = 0;
        for (int g = 0; g < groups.Length; g++)
        {
            if (groups[g].Depth < 0)
                throw new InvalidDataException($"Cluster hierarchy group {g} has a negative depth.");
            levelCount = Math.Max(levelCount, groups[g].Depth + 1);
        }

        // First pass: the index count of each cover, to size the output once.
        uint[] levelIndexCount = new uint[levelCount];
        for (int m = 0; m < meshlets.Length; m++)
        {
            ResolveCluster(m, clusterLods, groups, out int level, out bool root);
            uint corners = checked(meshlets[m].TriangleCount * 3u);
            int firstCoverLevel = Math.Max(level, 1);
            int lastCoverLevel = root ? levelCount - 1 : level;
            for (int cover = firstCoverLevel; cover <= lastCoverLevel; cover++)
                levelIndexCount[cover] = checked(levelIndexCount[cover] + corners);
        }

        uint[] levelFirstIndex = new uint[levelCount];
        uint total = 0u;
        for (int level = 1; level < levelCount; level++)
        {
            levelFirstIndex[level] = total;
            total = checked(total + levelIndexCount[level]);
        }

        // Second pass: write the triangles of each cluster into every cover that contains it.
        uint[] indices = new uint[total];
        uint[] write = (uint[])levelFirstIndex.Clone();
        for (int m = 0; m < meshlets.Length; m++)
        {
            ResolveCluster(m, clusterLods, groups, out int level, out bool root);
            int firstCoverLevel = Math.Max(level, 1);
            int lastCoverLevel = root ? levelCount - 1 : level;
            if (firstCoverLevel > lastCoverLevel)
                continue;

            CpuMeshletDescriptor meshlet = meshlets[m];
            ulong vertexEnd = (ulong)meshlet.VertexOffset + meshlet.VertexCount;
            ulong cornerEnd = (ulong)meshlet.TriangleOffset + (ulong)meshlet.TriangleCount * 3UL;
            if (vertexEnd > (ulong)vertexIndices.Length || cornerEnd > (ulong)triangleIndices.Length)
                throw new InvalidDataException($"Meshlet {m} has a range outside the meshlet streams.");
            ReadOnlySpan<uint> clusterVertices = vertexIndices.Slice((int)meshlet.VertexOffset, (int)meshlet.VertexCount);
            ReadOnlySpan<byte> corners = triangleIndices.Slice((int)meshlet.TriangleOffset, (int)(meshlet.TriangleCount * 3u));
            for (int cover = firstCoverLevel; cover <= lastCoverLevel; cover++)
            {
                uint target = write[cover];
                foreach (byte corner in corners)
                {
                    if (corner >= clusterVertices.Length)
                        throw new InvalidDataException($"Meshlet {m} has a triangle corner out of range.");
                    indices[target++] = clusterVertices[corner];
                }
                write[cover] = target;
            }
        }

        return new Result(indices, levelFirstIndex, levelIndexCount);
    }

    /// <summary>
    /// Reads the level of a cluster (the depth of its group) and whether it is a root cluster (its
    /// group ended the branch, so its error is <see cref="float.MaxValue"/>).
    /// </summary>
    private static void ResolveCluster(
        int meshlet,
        ReadOnlySpan<MeshletClusterLodRecord> clusterLods,
        ReadOnlySpan<MeshletLodGroupRecord> groups,
        out int level,
        out bool root)
    {
        int group = clusterLods[meshlet].Group;
        if ((uint)group >= (uint)groups.Length)
            throw new InvalidDataException($"Meshlet {meshlet} names group {group}, which is out of range.");
        level = groups[group].Depth;
        root = groups[group].SimplifiedError >= float.MaxValue;
    }
}
