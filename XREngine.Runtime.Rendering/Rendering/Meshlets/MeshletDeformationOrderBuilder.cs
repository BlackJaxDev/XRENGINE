namespace XREngine.Rendering.Meshlets;

/// <summary>
/// Builds the deformation order of a skinned cluster hierarchy mesh. The order is a permutation of
/// the source vertices that puts the vertices of coarse levels first. A deformation job that runs
/// over the first <c>PrefixCounts[L]</c> entries of the order deforms every vertex that a cluster
/// of level <c>L</c> or coarser reads, and no other vertex.
/// </summary>
/// <remarks>
/// The hierarchy simplification never moves or adds a vertex, so every cluster of every level reads
/// a subset of the source vertices. The builder gives each source vertex a key: the deepest level of
/// a cluster that reads it. A vertex of a root cluster (a group with
/// <see cref="MeshletLodGroupRecord.SimplifiedError"/> equal to <see cref="float.MaxValue"/>) gets
/// the largest key, because a root cluster can draw at every distance. The order sorts the vertices
/// by key, largest first, and keeps the source order inside one key. Vertices that no cluster reads
/// come last and are in no prefix.
/// </remarks>
public static class MeshletDeformationOrderBuilder
{
    /// <summary>The deformation order of one mesh.</summary>
    /// <param name="VertexOrder">
    /// A permutation of the source vertices: entry <c>i</c> is the source vertex that deformation
    /// thread <c>i</c> deforms.
    /// </param>
    /// <param name="PrefixCounts">
    /// One entry per hierarchy level <c>L</c>: the number of leading <paramref name="VertexOrder"/>
    /// entries that clusters of level <c>L</c> and coarser read. The counts do not increase with
    /// <c>L</c>. Entry 0 is the number of vertices that any cluster reads.
    /// </param>
    /// <param name="LevelMaxErrors">
    /// One entry per hierarchy level: the largest finite simplification error of the groups of that
    /// level, in mesh units, or 0 when the level has no finite group. The runtime uses it to find the
    /// coarsest level that the cut at a given distance never refines past.
    /// </param>
    public readonly record struct Result(uint[] VertexOrder, uint[] PrefixCounts, float[] LevelMaxErrors);

    /// <summary>
    /// Builds the deformation order from the hierarchy tables of a payload.
    /// </summary>
    /// <param name="meshlets">The meshlet descriptors of every level. Each one names its range in <paramref name="vertexIndices"/>.</param>
    /// <param name="vertexIndices">The source vertex index of each meshlet vertex.</param>
    /// <param name="clusterLods">The hierarchy record of each meshlet.</param>
    /// <param name="groups">The hierarchy groups.</param>
    /// <param name="vertexCount">The source vertex count of the mesh.</param>
    /// <exception cref="InvalidDataException">A table refers to a group or vertex out of range.</exception>
    public static Result Build(
        ReadOnlySpan<CpuMeshletDescriptor> meshlets,
        ReadOnlySpan<uint> vertexIndices,
        ReadOnlySpan<MeshletClusterLodRecord> clusterLods,
        ReadOnlySpan<MeshletLodGroupRecord> groups,
        int vertexCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        if (clusterLods.Length != meshlets.Length)
            throw new InvalidDataException("The deformation order needs one hierarchy record per meshlet.");

        int levelCount = 0;
        for (int g = 0; g < groups.Length; g++)
        {
            if (groups[g].Depth < 0)
                throw new InvalidDataException($"Cluster hierarchy group {g} has a negative depth.");
            levelCount = Math.Max(levelCount, groups[g].Depth + 1);
        }

        float[] levelMaxErrors = new float[levelCount];
        for (int g = 0; g < groups.Length; g++)
        {
            float error = groups[g].SimplifiedError;
            if (IsRoot(error))
                continue;
            ref float maxError = ref levelMaxErrors[groups[g].Depth];
            maxError = MathF.Max(maxError, error);
        }

        // Key per vertex: -1 when no cluster reads it, levelCount for a vertex of a root cluster,
        // otherwise the deepest level of a cluster that reads it.
        int[] keys = new int[vertexCount];
        Array.Fill(keys, -1);
        for (int m = 0; m < meshlets.Length; m++)
        {
            int group = clusterLods[m].Group;
            if ((uint)group >= (uint)groups.Length)
                throw new InvalidDataException($"Meshlet {m} names group {group}, which is out of range.");
            MeshletLodGroupRecord record = groups[group];
            int key = IsRoot(record.SimplifiedError) ? levelCount : record.Depth;

            CpuMeshletDescriptor meshlet = meshlets[m];
            ulong end = (ulong)meshlet.VertexOffset + meshlet.VertexCount;
            if (end > (ulong)vertexIndices.Length)
                throw new InvalidDataException($"Meshlet {m} has a vertex range outside the vertex index stream.");
            ReadOnlySpan<uint> clusterVertices = vertexIndices.Slice((int)meshlet.VertexOffset, (int)meshlet.VertexCount);
            foreach (uint vertex in clusterVertices)
            {
                if (vertex >= (uint)vertexCount)
                    throw new InvalidDataException($"Meshlet {m} refers to vertex {vertex}, which is out of range.");
                if (keys[vertex] < key)
                    keys[vertex] = key;
            }
        }

        // Bucket b holds the vertices with key levelCount - b, so bucket 0 (root vertices) comes
        // first. The last bucket holds the vertices that no cluster reads.
        int bucketCount = levelCount + 2;
        int[] bucketStarts = new int[bucketCount + 1];
        foreach (int key in keys)
            bucketStarts[BucketOf(key, levelCount) + 1]++;
        for (int b = 0; b < bucketCount; b++)
            bucketStarts[b + 1] += bucketStarts[b];

        uint[] prefixCounts = new uint[levelCount];
        for (int level = 0; level < levelCount; level++)
            prefixCounts[level] = (uint)bucketStarts[BucketOf(level, levelCount) + 1];

        uint[] order = new uint[vertexCount];
        int[] write = (int[])bucketStarts.Clone();
        for (int vertex = 0; vertex < vertexCount; vertex++)
            order[write[BucketOf(keys[vertex], levelCount)]++] = (uint)vertex;

        return new Result(order, prefixCounts, levelMaxErrors);
    }

    /// <summary>
    /// Returns the bucket of a key: 0 for root vertices, <c>levelCount - key</c> for a level key, and
    /// the last bucket for a vertex that no cluster reads.
    /// </summary>
    private static int BucketOf(int key, int levelCount)
        => key < 0 ? levelCount + 1 : levelCount - key;

    /// <summary>True for the error of a group that ended its branch (a root of the cut).</summary>
    private static bool IsRoot(float error)
        => error >= float.MaxValue;
}
