using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Rendering.Meshlets;

internal static partial class NativeMeshOptimizer
{
    private const uint TangentCompatible = 1;

    [DllImport(NativeLibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "meshopt_generateTangents")]
    private static extern unsafe void MeshoptGenerateTangents(
        float* destination,
        uint* indices,
        nuint indexCount,
        float* positions,
        nuint vertexCount,
        nuint positionStride,
        float* normals,
        nuint normalStride,
        float* texCoords,
        nuint texCoordStride,
        uint options);

    /// <summary>Calculates one tangent for each triangle corner from a complete source mesh.</summary>
    public static unsafe void GenerateTangents(
        Span<Vector4> destination,
        ReadOnlySpan<uint> indices,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector2> texCoords)
    {
        if (indices.Length % 3 != 0 || destination.Length != indices.Length ||
            normals.Length != positions.Length || texCoords.Length != positions.Length)
            throw new ArgumentException("Tangent input arrays must contain complete triangles and matching vertex attributes.");
        if (indices.IsEmpty)
            return;
        for (int index = 0; index < indices.Length; index++)
        {
            if (indices[index] >= (uint)positions.Length)
                throw new ArgumentOutOfRangeException(nameof(indices), "A tangent index is outside the source vertex arrays.");
        }

        fixed (Vector4* result = destination)
        fixed (uint* triangleIndices = indices)
        fixed (Vector3* vertexPositions = positions)
        fixed (Vector3* vertexNormals = normals)
        fixed (Vector2* vertexTexCoords = texCoords)
        {
            MeshoptGenerateTangents(
                (float*)result,
                triangleIndices,
                (nuint)indices.Length,
                (float*)vertexPositions,
                (nuint)positions.Length,
                (nuint)sizeof(Vector3),
                (float*)vertexNormals,
                (nuint)sizeof(Vector3),
                (float*)vertexTexCoords,
                (nuint)sizeof(Vector2),
                TangentCompatible);
        }
    }
}
