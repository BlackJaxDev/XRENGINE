using System.Numerics;

namespace XREngine.Rendering;

/// <summary>
/// Full-precision blendshape deltas of one mesh, grouped by shape and ordered
/// by vertex within each shape. It is decoded from the mesh's packed active-list
/// buffers (see <see cref="XRMeshBlendshapeActiveListReader"/>), which in-session
/// imports and cooked loads both keep, so no per-vertex object model is needed
/// to read blendshapes on the CPU.
/// </summary>
/// <remarks>
/// A record's attribute delta is zero when the packed entry has no delta for
/// that attribute. Decoding is a cold operation (mesh tools, editor actions);
/// the table owns only its own arrays and no mesh references.
/// </remarks>
public sealed class XRMeshBlendshapeDeltaTable
{
    /// <summary>The record has a position delta.</summary>
    public const byte PositionFlag = 1;
    /// <summary>The record has a normal delta.</summary>
    public const byte NormalFlag = 2;
    /// <summary>The record has a tangent delta.</summary>
    public const byte TangentFlag = 4;

    private readonly int[] _shapeRecordOffsets;
    private readonly int[] _recordVertices;
    private readonly Vector3[] _positionDeltas;
    private readonly Vector3[] _normalDeltas;
    private readonly Vector3[] _tangentDeltas;
    private readonly byte[] _flags;

    private XRMeshBlendshapeDeltaTable(
        int[] shapeRecordOffsets,
        int[] recordVertices,
        Vector3[] positionDeltas,
        Vector3[] normalDeltas,
        Vector3[] tangentDeltas,
        byte[] flags)
    {
        _shapeRecordOffsets = shapeRecordOffsets;
        _recordVertices = recordVertices;
        _positionDeltas = positionDeltas;
        _normalDeltas = normalDeltas;
        _tangentDeltas = tangentDeltas;
        _flags = flags;
    }

    /// <summary>Number of blendshapes, matching the mesh's blendshape names.</summary>
    public int ShapeCount => _shapeRecordOffsets.Length - 1;

    /// <summary>Total number of (shape, vertex) records with at least one delta.</summary>
    public int RecordCount => _recordVertices.Length;

    /// <summary>First record index of a shape; records of shape s are [offset(s), offset(s + 1)).</summary>
    public int GetShapeRecordOffset(int shapeIndex) => _shapeRecordOffsets[shapeIndex];

    /// <summary>Number of records of one shape.</summary>
    public int GetShapeRecordCount(int shapeIndex)
        => _shapeRecordOffsets[shapeIndex + 1] - _shapeRecordOffsets[shapeIndex];

    public ReadOnlySpan<int> RecordVertices => _recordVertices;
    public ReadOnlySpan<Vector3> PositionDeltas => _positionDeltas;
    public ReadOnlySpan<Vector3> NormalDeltas => _normalDeltas;
    public ReadOnlySpan<Vector3> TangentDeltas => _tangentDeltas;
    /// <summary>Per-record presence flags (<see cref="PositionFlag"/>, <see cref="NormalFlag"/>, <see cref="TangentFlag"/>).</summary>
    public ReadOnlySpan<byte> Flags => _flags;

    /// <summary>
    /// Decodes the table from the mesh's packed active-list buffers. Returns
    /// false when the mesh has no blendshapes, its buffers carry no client-side
    /// data, or an entry names a shape outside the mesh's blendshape names.
    /// </summary>
    public static bool TryCreate(XRMesh mesh, out XRMeshBlendshapeDeltaTable table)
    {
        table = null!;
        if (!XRMeshBlendshapeActiveListReader.TryCreate(mesh, out XRMeshBlendshapeActiveListReader reader))
            return false;

        int vertexCount = reader.VertexCount;
        int shapeCount = reader.ShapeCount;
        int[] shapeRecordOffsets = new int[shapeCount + 1];

        // Count records per shape, then fill them in vertex order (counting sort).
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            reader.GetVertexEntries(vertex, out int first, out int count);
            for (int entry = first; entry < first + count; entry++)
            {
                reader.ReadEntry(entry, out int shape, out Vector3 position, out Vector3 normal, out Vector3 tangent);
                if ((uint)shape >= (uint)shapeCount)
                    return false;
                if (ToFlags(position, normal, tangent) != 0)
                    shapeRecordOffsets[shape + 1]++;
            }
        }
        for (int shape = 0; shape < shapeCount; shape++)
            shapeRecordOffsets[shape + 1] += shapeRecordOffsets[shape];

        int recordCount = shapeRecordOffsets[shapeCount];
        int[] cursor = new int[shapeCount];
        Array.Copy(shapeRecordOffsets, cursor, shapeCount);
        int[] recordVertices = new int[recordCount];
        Vector3[] positionDeltas = new Vector3[recordCount];
        Vector3[] normalDeltas = new Vector3[recordCount];
        Vector3[] tangentDeltas = new Vector3[recordCount];
        byte[] flags = new byte[recordCount];
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            reader.GetVertexEntries(vertex, out int first, out int count);
            for (int entry = first; entry < first + count; entry++)
            {
                reader.ReadEntry(entry, out int shape, out Vector3 position, out Vector3 normal, out Vector3 tangent);
                byte recordFlags = ToFlags(position, normal, tangent);
                if (recordFlags == 0)
                    continue;
                int record = cursor[shape]++;
                recordVertices[record] = vertex;
                positionDeltas[record] = position;
                normalDeltas[record] = normal;
                tangentDeltas[record] = tangent;
                flags[record] = recordFlags;
            }
        }

        table = new XRMeshBlendshapeDeltaTable(
            shapeRecordOffsets,
            recordVertices,
            positionDeltas,
            normalDeltas,
            tangentDeltas,
            flags);
        return true;
    }

    private static byte ToFlags(Vector3 position, Vector3 normal, Vector3 tangent)
        => (byte)((position.LengthSquared() > 0.0f ? PositionFlag : 0) |
                  (normal.LengthSquared() > 0.0f ? NormalFlag : 0) |
                  (tangent.LengthSquared() > 0.0f ? TangentFlag : 0));
}
