using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

public sealed partial class XRMeshPackedSource
{
    /// <summary>
    /// Merges vertices whose attributes are bit-equal (position, normal, tangent, every
    /// texture-coordinate set and every color set) and rewrites <paramref name="indices"/>
    /// to the kept vertices. Importers that add one vertex for each polygon corner call it,
    /// so that corners with identical attributes become one shared vertex. Only identical
    /// vertices merge, so the triangles and the rendered image do not change. The kept
    /// vertices keep their first-seen order. Returns the number of removed vertices.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The meshlet builder stops a meshlet at its vertex limit. Unshared corners reach that
    /// limit with about a third of the triangles that shared vertices allow, and the mesh
    /// simplifier cannot remove triangles across unshared corners. Welding before the mesh is
    /// built fixes both.
    /// </para>
    /// <para>
    /// A source with bone influences or blendshape deltas is not changed, because the
    /// comparison does not cover those streams. Call it after tangent generation, so that
    /// generated tangents are part of the comparison and are not averaged across merged
    /// corners. Working storage is rented from <see cref="ArrayPool{T}"/>: one hash pass over
    /// the vertices with an open-addressing table.
    /// </para>
    /// </remarks>
    public int WeldExactDuplicates(List<int> indices)
    {
        ArgumentNullException.ThrowIfNull(indices);
        int vertexCount = VertexCount;
        if (vertexCount < 2 || HasInfluences || HasBlendshapeDeltas)
            return 0;

        int tableSize = (int)BitOperations.RoundUpToPowerOf2((uint)vertexCount * 2u);
        int mask = tableSize - 1;
        int[] table = ArrayPool<int>.Shared.Rent(tableSize);
        int[] keptHashes = ArrayPool<int>.Shared.Rent(vertexCount);
        int[] remap = ArrayPool<int>.Shared.Rent(vertexCount);
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
                    if (keptHashes[candidate] == hash && VerticesBitEqual(candidate, vertex))
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
                remap[vertex] = kept;
                kept++;
            }

            if (kept == vertexCount)
                return 0;

            for (int index = 0; index < indices.Count; index++)
                indices[index] = remap[indices[index]];
            VertexCount = kept;
            Array.Clear(_influenceStarts, 0, kept + 1);
            Array.Clear(_blendshapeStarts, 0, kept + 1);
            return vertexCount - kept;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(table);
            ArrayPool<int>.Shared.Return(keptHashes);
            ArrayPool<int>.Shared.Return(remap);
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
        return hash.ToHashCode();
    }

    private bool VerticesBitEqual(int a, int b)
    {
        if (!BitEqual(_positions.AsSpan(a, 1), _positions.AsSpan(b, 1)))
            return false;
        if (HasNormals && !BitEqual(_normals.AsSpan(a, 1), _normals.AsSpan(b, 1)))
            return false;
        if (HasTangents && !BitEqual(_tangents.AsSpan(a, 1), _tangents.AsSpan(b, 1)))
            return false;
        if (TexCoordSetCount > 0 && !BitEqual(
                _texCoords.AsSpan(a * TexCoordSetCount, TexCoordSetCount),
                _texCoords.AsSpan(b * TexCoordSetCount, TexCoordSetCount)))
            return false;
        return ColorSetCount == 0 || BitEqual(
            _colors.AsSpan(a * ColorSetCount, ColorSetCount),
            _colors.AsSpan(b * ColorSetCount, ColorSetCount));
    }

    private static bool BitEqual<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b) where T : unmanaged
        => MemoryMarshal.AsBytes(a).SequenceEqual(MemoryMarshal.AsBytes(b));
}
