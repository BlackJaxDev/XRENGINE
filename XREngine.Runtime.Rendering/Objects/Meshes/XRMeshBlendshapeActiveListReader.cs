using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

/// <summary>
/// Reads a mesh's packed blendshape active list without a per-vertex object
/// model: per vertex, a range of entries; per entry, the shape index and the
/// position, normal and tangent delta indices into the float delta table. This
/// is the full-precision form the importers pack and the cooked format keeps.
/// </summary>
public readonly struct XRMeshBlendshapeActiveListReader
{
    private XRMeshBlendshapeActiveListReader(
        XRDataBuffer counts,
        XRDataBuffer indices,
        XRDataBuffer deltas,
        int vertexCount,
        int shapeCount)
    {
        Counts = counts;
        Indices = indices;
        Deltas = deltas;
        VertexCount = vertexCount;
        ShapeCount = shapeCount;
    }

    public XRDataBuffer Counts { get; }
    public XRDataBuffer Indices { get; }
    public XRDataBuffer Deltas { get; }
    public int VertexCount { get; }
    public int ShapeCount { get; }
    public bool IsValid => Counts is not null;

    /// <summary>
    /// Creates a reader over the mesh's active-list buffers. Returns false when
    /// the mesh has no blendshapes or the buffers carry no client-side data.
    /// </summary>
    public static bool TryCreate(XRMesh mesh, out XRMeshBlendshapeActiveListReader reader)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        reader = default;
        int vertexCount = mesh.VertexCount;
        int shapeCount = mesh.BlendshapeNames?.Length ?? 0;
        if (vertexCount <= 0 || shapeCount == 0 ||
            mesh.BlendshapeCounts is not { ClientSideSource: not null } counts ||
            mesh.BlendshapeIndices is not { ClientSideSource: not null } indices ||
            mesh.BlendshapeDeltas is not { ClientSideSource: not null } deltas ||
            counts.ElementCount < (uint)vertexCount ||
            counts.ComponentCount < 2u ||
            indices.ComponentCount < 4u ||
            deltas.ComponentCount < 3u)
        {
            return false;
        }

        reader = new XRMeshBlendshapeActiveListReader(counts, indices, deltas, vertexCount, shapeCount);
        return true;
    }

    /// <summary>
    /// Returns the vertex's entry range, clamped to the entries present.
    /// </summary>
    public void GetVertexEntries(int vertexIndex, out int first, out int count)
    {
        if ((uint)vertexIndex >= (uint)VertexCount)
            throw new ArgumentOutOfRangeException(nameof(vertexIndex));
        int offset = ReadInt(Counts, (uint)vertexIndex, 0u);
        int length = ReadInt(Counts, (uint)vertexIndex, 1u);
        int entryCount = checked((int)Indices.ElementCount);
        first = Math.Clamp(offset, 0, entryCount);
        count = Math.Clamp(length, 0, entryCount - first);
    }

    /// <summary>
    /// Reads one entry's shape index and attribute deltas. An attribute without
    /// a delta reads as zero: it points at the shared zero delta (index 0, or its
    /// deduplicated slot when the table was remapped).
    /// </summary>
    public void ReadEntry(
        int entryIndex,
        out int shapeIndex,
        out Vector3 position,
        out Vector3 normal,
        out Vector3 tangent)
    {
        uint entry = checked((uint)entryIndex);
        shapeIndex = ReadInt(Indices, entry, 0u);
        position = ReadDelta(ReadInt(Indices, entry, 1u));
        normal = ReadDelta(ReadInt(Indices, entry, 2u));
        tangent = ReadDelta(ReadInt(Indices, entry, 3u));
    }

    /// <summary>
    /// Reads one integral component. Active-list buffers store indices as
    /// integers or as exactly representable floats, depending on whether the
    /// shaders use integer uniforms.
    /// </summary>
    private static unsafe int ReadInt(XRDataBuffer buffer, uint element, uint component)
    {
        byte* address = (byte*)buffer.Address.Pointer + element * buffer.ElementSize;
        return buffer.ComponentType switch
        {
            EComponentType.Int => ((int*)address)[component],
            EComponentType.UInt => checked((int)((uint*)address)[component]),
            EComponentType.Float => (int)((float*)address)[component],
            _ => throw new InvalidOperationException(
                $"Unsupported blendshape index component type {buffer.ComponentType}."),
        };
    }

    private unsafe Vector3 ReadDelta(int index)
    {
        if ((uint)index >= Deltas.ElementCount)
            return Vector3.Zero;
        float* value = (float*)((byte*)Deltas.Address.Pointer + (uint)index * Deltas.ElementSize);
        return new Vector3(value[0], value[1], value[2]);
    }
}
