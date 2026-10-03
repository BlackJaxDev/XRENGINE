using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>Immutable browser geometry: tightly packed position.xyz and UV.xy floats, with uint32 triangle indices.</summary>
public sealed class BrowserMeshData
{
    private readonly float[] _vertices;
    private readonly uint[] _indices;

    public BrowserMeshData(ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices)
    {
        if (vertices.Length < 15 || vertices.Length % 5 != 0 || vertices.Length > 16 * 1024 * 1024 ||
            indices.Length == 0 || indices.Length % 3 != 0 || indices.Length > 16 * 1024 * 1024)
            throw new ArgumentException("A mesh requires at least three position/UV vertices, complete indexed triangles and at most 64 MiB per payload.");

        int vertexCount = vertices.Length / 5;
        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        for (int i = 0; i < vertices.Length; i += 5)
        {
            for (int j = 0; j < 5; j++)
                if (!float.IsFinite(vertices[i + j]))
                    throw new ArgumentException("Mesh coordinates and UVs must be finite.", nameof(vertices));

            Vector3 position = new(vertices[i], vertices[i + 1], vertices[i + 2]);
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        foreach (uint index in indices)
            if (index >= vertexCount)
                throw new ArgumentException("Mesh indices must reference an existing vertex.", nameof(indices));

        _vertices = vertices.ToArray();
        _indices = indices.ToArray();
        BoundsMinimum = minimum;
        BoundsMaximum = maximum;
    }

    public int IndexCount => _indices.Length;
    public int VertexCount => _vertices.Length / 5;
    public Vector3 BoundsMinimum { get; }
    public Vector3 BoundsMaximum { get; }

    /// <summary>Copies the packed position/UV payload for an external renderer or serializer.</summary>
    public byte[] CopyVertexBytes() => MemoryMarshal.AsBytes(_vertices.AsSpan()).ToArray();
    /// <summary>Copies the triangle indices for an external renderer or serializer.</summary>
    public byte[] CopyIndexBytes() => MemoryMarshal.AsBytes(_indices.AsSpan()).ToArray();
    public float[] CopyVertices() => (float[])_vertices.Clone();
    public uint[] CopyIndices() => (uint[])_indices.Clone();

    // Only the synchronous upload bridge receives these spans. The renderer copies both immediately.
    internal Span<byte> VertexBytes => MemoryMarshal.AsBytes(_vertices.AsSpan());
    internal Span<byte> IndexBytes => MemoryMarshal.AsBytes(_indices.AsSpan());
}
