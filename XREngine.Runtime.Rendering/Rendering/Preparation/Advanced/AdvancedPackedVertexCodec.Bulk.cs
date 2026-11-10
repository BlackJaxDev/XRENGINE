using System.Numerics;
using System.Runtime.CompilerServices;

namespace XREngine.Rendering;

public static partial class AdvancedPackedVertexCodec
{
    /// <summary>
    /// Packs consecutive mesh vertices into <paramref name="destination"/>. The result is the same as
    /// calling <see cref="Pack(XRMesh, uint, uint)"/> for each vertex, but each attribute stream is
    /// resolved once and then read by pointer, so the per-vertex accessor branches are gone.
    /// </summary>
    /// <param name="mesh">The source mesh.</param>
    /// <param name="firstVertex">The first mesh vertex to pack.</param>
    /// <param name="destination">Receives one packed vertex per source vertex.</param>
    /// <param name="firstSourceVertex">The source-vertex index stored in the first packed vertex.</param>
    /// <remarks>
    /// A mesh that <see cref="CanReadMesh(XRMesh)"/> rejects is packed with the per-vertex accessors,
    /// which give the same defaults for missing data.
    /// </remarks>
    public static unsafe void Pack(
        XRMesh mesh,
        uint firstVertex,
        Span<AdvancedDeformedVertex> destination,
        uint firstSourceVertex)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (destination.IsEmpty)
            return;
        if ((ulong)firstVertex + (ulong)destination.Length > (ulong)mesh.VertexCount)
            throw new ArgumentOutOfRangeException(nameof(destination), "The range exceeds the mesh vertex count.");

        if (!CanReadMesh(mesh))
        {
            for (int index = 0; index < destination.Length; ++index)
                destination[index] = Pack(mesh, firstVertex + (uint)index, firstSourceVertex + (uint)index);
            return;
        }

        uint texCoordCount = mesh.TexCoordCount;
        uint colorCount = mesh.ColorCount;
        AttributeStream position;
        AttributeStream normal = default;
        AttributeStream tangent = default;
        AttributeStream texCoord0 = default;
        AttributeStream texCoord1 = default;
        AttributeStream texCoord2 = default;
        AttributeStream texCoord3 = default;
        AttributeStream color0 = default;
        AttributeStream color1 = default;

        if (mesh.Interleaved)
        {
            byte* vertices = (byte*)mesh.InterleavedVertexBuffer!.ClientSideSource!.Address.Pointer;
            uint stride = mesh.InterleavedStride;
            position = new(vertices + mesh.PositionOffset, stride);
            if (mesh.HasNormals)
                normal = new(vertices + mesh.NormalOffset!.Value, stride);
            if (mesh.HasTangents)
                tangent = new(vertices + mesh.TangentOffset!.Value, stride);
            if (mesh.TexCoordOffset is { } texCoordOffset)
            {
                if (texCoordCount > 0u) texCoord0 = new(vertices + texCoordOffset, stride);
                if (texCoordCount > 1u) texCoord1 = new(vertices + texCoordOffset + 8u, stride);
                if (texCoordCount > 2u) texCoord2 = new(vertices + texCoordOffset + 16u, stride);
                if (texCoordCount > 3u) texCoord3 = new(vertices + texCoordOffset + 24u, stride);
            }
            if (mesh.ColorOffset is { } colorOffset)
            {
                if (colorCount > 0u) color0 = new(vertices + colorOffset, stride);
                if (colorCount > 1u) color1 = new(vertices + colorOffset + 16u, stride);
            }
        }
        else
        {
            position = Stream(mesh.PositionsBuffer, 12u);
            if (mesh.HasNormals)
                normal = Stream(mesh.NormalsBuffer, 12u);
            if (mesh.HasTangents)
                tangent = Stream(mesh.TangentsBuffer, 16u);
            XRDataBuffer?[]? texCoords = mesh.TexCoordBuffers;
            if (texCoordCount > 0u) texCoord0 = Stream(texCoords![0], 8u);
            if (texCoordCount > 1u) texCoord1 = Stream(texCoords![1], 8u);
            if (texCoordCount > 2u) texCoord2 = Stream(texCoords![2], 8u);
            if (texCoordCount > 3u) texCoord3 = Stream(texCoords![3], 8u);
            XRDataBuffer?[]? colors = mesh.ColorBuffers;
            if (colorCount > 0u) color0 = Stream(colors![0], 16u);
            if (colorCount > 1u) color1 = Stream(colors![1], 16u);
        }

        bool hasTexCoord1 = texCoordCount > 1u;
        bool hasTexCoord2 = texCoordCount > 2u;
        bool hasTexCoord3 = texCoordCount > 3u;
        for (int index = 0; index < destination.Length; ++index)
        {
            uint vertex = firstVertex + (uint)index;
            Vector4 tangentWithSign = tangent.IsPresent
                ? tangent.Read<Vector4>(vertex)
                : new Vector4(Vector3.UnitX, 1.0f);
            destination[index] = Pack(
                position.Read<Vector3>(vertex),
                normal.IsPresent ? normal.Read<Vector3>(vertex) : Vector3.UnitY,
                new Vector3(tangentWithSign.X, tangentWithSign.Y, tangentWithSign.Z),
                tangentWithSign.W,
                texCoord0.IsPresent ? texCoord0.Read<Vector2>(vertex) : Vector2.Zero,
                texCoord1.IsPresent ? texCoord1.Read<Vector2>(vertex) : Vector2.Zero,
                color0.IsPresent ? color0.Read<Vector4>(vertex) : Vector4.One,
                color1.IsPresent ? color1.Read<Vector4>(vertex) : Vector4.One,
                firstSourceVertex + (uint)index,
                hasTexCoord1,
                texCoord2.IsPresent ? texCoord2.Read<Vector2>(vertex) : Vector2.Zero,
                texCoord3.IsPresent ? texCoord3.Read<Vector2>(vertex) : Vector2.Zero,
                hasTexCoord2,
                hasTexCoord3);
        }
    }

    private static unsafe AttributeStream Stream(XRDataBuffer? buffer, uint stride)
        => new((byte*)buffer!.ClientSideSource!.Address.Pointer, stride);

    /// <summary>One attribute stream: the address of vertex 0 and the byte distance between vertices.</summary>
    private readonly unsafe struct AttributeStream(byte* first, uint stride)
    {
        private readonly byte* _first = first;
        private readonly uint _stride = stride;

        public bool IsPresent => _first != null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Read<T>(uint vertex) where T : unmanaged
            => Unsafe.ReadUnaligned<T>(_first + (nuint)vertex * _stride);
    }
}
