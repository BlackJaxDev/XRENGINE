using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

public sealed partial class XRMeshPackedSource
{
    /// <summary>
    /// Merges vertices whose data is bit-equal (position, normal, tangent, every
    /// texture-coordinate set, every color set, the bone influences and the blendshape
    /// deltas) and rewrites <paramref name="indices"/> to the kept vertices. Importers that
    /// add one vertex for each polygon corner call it, so that corners with identical data
    /// become one shared vertex. Only identical vertices merge, so the triangles, the skinning,
    /// the blendshapes and the rendered image do not change. The kept vertices keep their
    /// first-seen order. Returns the number of removed vertices.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The meshlet builder stops a meshlet at its vertex limit. Unshared corners reach that
    /// limit with about a third of the triangles that shared vertices allow, and the mesh
    /// simplifier cannot remove triangles across unshared corners. Welding before the mesh is
    /// built fixes both. For skinned and blendshape meshes it also removes the deformation
    /// work for the duplicate corners.
    /// </para>
    /// <para>
    /// Two vertices merge only when their influence lists (bone index and weight, in the
    /// order the importer added them) and their blendshape entry lists (shape index and the
    /// position, normal and tangent deltas, in order) are equal. The influence and blendshape
    /// entries of the kept vertices move down with them. Call it after tangent generation, so
    /// that generated tangents are part of the comparison and are not averaged across merged
    /// corners. Working storage is rented from <see cref="ArrayPool{T}"/>: one hash pass over
    /// the vertices with an open-addressing table.
    /// </para>
    /// </remarks>
    public int WeldExactDuplicates(List<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        int vertexCount = VertexCount;
        if (vertexCount < 2)
            return 0;

        int tableSize = (int)BitOperations.RoundUpToPowerOf2((uint)vertexCount * 2u);
        int mask = tableSize - 1;
        int[] table = ArrayPool<int>.Shared.Rent(tableSize);
        int[] keptHashes = ArrayPool<int>.Shared.Rent(vertexCount);
        int[] remap = ArrayPool<int>.Shared.Rent(vertexCount);
        // The source vertex of each kept slot. The influence and blendshape entries stay at
        // their source positions during the pass, so comparisons read them through this map.
        int[] keptSources = ArrayPool<int>.Shared.Rent(vertexCount);
        try
        {
            Array.Fill(table, -1, 0, tableSize);
            int kept = 0;
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                int hash = ComputeVertexHash(vertex);
                int slot = hash & mask;
                int match = -1;
                // Entries hold kept slots. A kept slot is never overwritten, because later
                // vertices move only to higher slots, and the unmoved vertex at index
                // `vertex` is at or above every kept slot.
                while (table[slot] >= 0)
                {
                    int candidate = table[slot];
                    if (keptHashes[candidate] == hash && VerticesBitEqual(candidate, keptSources[candidate], vertex))
                    {
                        match = candidate;
                        break;
                    }
                    slot = (slot + 1) & mask;
                }

                if (match >= 0)
                {
                    remap[vertex] = match;
                    continue;
                }

                if (kept != vertex)
                    MoveVertex(vertex, kept);
                table[slot] = kept;
                keptHashes[kept] = hash;
                keptSources[kept] = vertex;
                remap[vertex] = kept;
                kept++;
            }

            if (kept == vertexCount)
                return 0;

            for (int index = 0; index < indices.Count; index++)
                indices[index] = remap[indices[index]];
            CompactInfluences(keptSources, kept, vertexCount);
            CompactBlendshapeEntries(keptSources, kept, vertexCount);
            VertexCount = kept;
            return vertexCount - kept;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(table);
            ArrayPool<int>.Shared.Return(keptHashes);
            ArrayPool<int>.Shared.Return(remap);
            ArrayPool<int>.Shared.Return(keptSources);
        }
    }

    private void MoveVertex(int from, int to)
    {
        _positions[to] = _positions[from];
        if (HasNormals)
            _normals[to] = _normals[from];
        if (HasTangents)
            _tangents[to] = _tangents[from];
        for (int set = 0; set < TexCoordSetCount; set++)
            _texCoords[to * TexCoordSetCount + set] = _texCoords[from * TexCoordSetCount + set];
        for (int set = 0; set < ColorSetCount; set++)
            _colors[to * ColorSetCount + set] = _colors[from * ColorSetCount + set];
    }

    /// <summary>
    /// Moves the influence entries of the kept vertices to the front of the influence arrays,
    /// in kept order, and rebuilds the per-vertex start offsets for the kept vertex count.
    /// </summary>
    private void CompactInfluences(int[] keptSources, int kept, int sourceVertexCount)
    {
        if (!HasInfluences)
            return;

        // Copy the source offsets first: the rebuilt offsets overwrite the same array.
        int[] sourceStarts = ArrayPool<int>.Shared.Rent(sourceVertexCount + 1);
        try
        {
            Array.Copy(_influenceStarts, sourceStarts, sourceVertexCount + 1);
            int write = 0;
            for (int slot = 0; slot < kept; slot++)
            {
                int source = keptSources[slot];
                int start = sourceStarts[source];
                int count = sourceStarts[source + 1] - start;
                // Kept sources increase, so `write` never passes `start`; a forward copy is safe.
                if (write != start)
                {
                    Array.Copy(_influenceBones, start, _influenceBones, write, count);
                    Array.Copy(_influenceWeights, start, _influenceWeights, write, count);
                }
                _influenceStarts[slot] = write;
                write += count;
            }
            _influenceStarts[kept] = write;
            _influenceCount = write;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(sourceStarts);
        }
    }

    /// <summary>
    /// Moves the blendshape entries of the kept vertices to the front of the blendshape arrays,
    /// in kept order, and rebuilds the per-vertex start offsets for the kept vertex count.
    /// </summary>
    private void CompactBlendshapeEntries(int[] keptSources, int kept, int sourceVertexCount)
    {
        if (!HasBlendshapeDeltas)
            return;

        int[] sourceStarts = ArrayPool<int>.Shared.Rent(sourceVertexCount + 1);
        try
        {
            Array.Copy(_blendshapeStarts, sourceStarts, sourceVertexCount + 1);
            bool hasTangentDeltas = _blendshapeTangentDeltas.Length != 0;
            int write = 0;
            for (int slot = 0; slot < kept; slot++)
            {
                int source = keptSources[slot];
                int start = sourceStarts[source];
                int count = sourceStarts[source + 1] - start;
                if (write != start)
                {
                    Array.Copy(_blendshapeShapes, start, _blendshapeShapes, write, count);
                    Array.Copy(_blendshapePositionDeltas, start, _blendshapePositionDeltas, write, count);
                    Array.Copy(_blendshapeNormalDeltas, start, _blendshapeNormalDeltas, write, count);
                    if (hasTangentDeltas)
                        Array.Copy(_blendshapeTangentDeltas, start, _blendshapeTangentDeltas, write, count);
                }
                _blendshapeStarts[slot] = write;
                write += count;
            }
            _blendshapeStarts[kept] = write;
            _blendshapeEntryCount = write;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(sourceStarts);
        }
    }

    private int ComputeVertexHash(int vertex)
    {
        HashCode hash = new();
        hash.AddBytes(MemoryMarshal.AsBytes(_positions.AsSpan(vertex, 1)));
        if (HasNormals)
            hash.AddBytes(MemoryMarshal.AsBytes(_normals.AsSpan(vertex, 1)));
        if (HasTangents)
            hash.AddBytes(MemoryMarshal.AsBytes(_tangents.AsSpan(vertex, 1)));
        if (TexCoordSetCount > 0)
            hash.AddBytes(MemoryMarshal.AsBytes(_texCoords.AsSpan(vertex * TexCoordSetCount, TexCoordSetCount)));
        if (ColorSetCount > 0)
            hash.AddBytes(MemoryMarshal.AsBytes(_colors.AsSpan(vertex * ColorSetCount, ColorSetCount)));
        if (HasInfluences)
        {
            GetInfluences(vertex, out ReadOnlySpan<int> bones, out ReadOnlySpan<float> weights);
            hash.Add(bones.Length);
            hash.AddBytes(MemoryMarshal.AsBytes(bones));
            hash.AddBytes(MemoryMarshal.AsBytes(weights));
        }
        if (HasBlendshapeDeltas)
        {
            // The shape indices and position deltas separate the vertices well enough; the
            // equality test compares every blendshape stream.
            GetBlendshapeEntries(vertex, out ReadOnlySpan<int> shapes, out ReadOnlySpan<Vector3> positionDeltas, out _, out _);
            hash.Add(shapes.Length);
            hash.AddBytes(MemoryMarshal.AsBytes(shapes));
            hash.AddBytes(MemoryMarshal.AsBytes(positionDeltas));
        }
        return hash.ToHashCode();
    }

    /// <summary>
    /// Compares the kept vertex in slot <paramref name="keptSlot"/> (whose influence and
    /// blendshape entries are still at source vertex <paramref name="keptSource"/>) with the
    /// unmoved vertex <paramref name="vertex"/>.
    /// </summary>
    private bool VerticesBitEqual(int keptSlot, int keptSource, int vertex)
    {
        if (!BitEqual(_positions.AsSpan(keptSlot, 1), _positions.AsSpan(vertex, 1)))
            return false;
        if (HasNormals && !BitEqual(_normals.AsSpan(keptSlot, 1), _normals.AsSpan(vertex, 1)))
            return false;
        if (HasTangents && !BitEqual(_tangents.AsSpan(keptSlot, 1), _tangents.AsSpan(vertex, 1)))
            return false;
        if (TexCoordSetCount > 0 && !BitEqual(
                _texCoords.AsSpan(keptSlot * TexCoordSetCount, TexCoordSetCount),
                _texCoords.AsSpan(vertex * TexCoordSetCount, TexCoordSetCount)))
            return false;
        if (ColorSetCount > 0 && !BitEqual(
                _colors.AsSpan(keptSlot * ColorSetCount, ColorSetCount),
                _colors.AsSpan(vertex * ColorSetCount, ColorSetCount)))
            return false;
        return InfluencesBitEqual(keptSource, vertex) && BlendshapeEntriesBitEqual(keptSource, vertex);
    }

    private bool InfluencesBitEqual(int a, int b)
    {
        if (!HasInfluences)
            return true;

        GetInfluences(a, out ReadOnlySpan<int> bonesA, out ReadOnlySpan<float> weightsA);
        GetInfluences(b, out ReadOnlySpan<int> bonesB, out ReadOnlySpan<float> weightsB);
        return bonesA.SequenceEqual(bonesB) && BitEqual(weightsA, weightsB);
    }

    private bool BlendshapeEntriesBitEqual(int a, int b)
    {
        if (!HasBlendshapeDeltas)
            return true;

        GetBlendshapeEntries(a, out ReadOnlySpan<int> shapesA, out ReadOnlySpan<Vector3> positionsA, out ReadOnlySpan<Vector3> normalsA, out ReadOnlySpan<Vector3> tangentsA);
        GetBlendshapeEntries(b, out ReadOnlySpan<int> shapesB, out ReadOnlySpan<Vector3> positionsB, out ReadOnlySpan<Vector3> normalsB, out ReadOnlySpan<Vector3> tangentsB);
        return shapesA.SequenceEqual(shapesB)
            && BitEqual(positionsA, positionsB)
            && BitEqual(normalsA, normalsB)
            && BitEqual(tangentsA, tangentsB);
    }

    private static bool BitEqual<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged
        => MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));
}
