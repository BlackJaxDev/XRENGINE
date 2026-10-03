using System.Numerics;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public static partial class AdvancedPackedVertexCodec
{
    /// <summary>
    /// Checks the CPU-readable source used for canonical publication. Cooked meshes
    /// retain packed buffers without allocating the authoring Vertex object graph.
    /// </summary>
    internal static bool CanReadMesh(XRMesh mesh)
    {
        if (mesh.VertexCount <= 0)
            return false;
        if (mesh.Vertices.Length == mesh.VertexCount)
            return true;
        uint count = checked((uint)mesh.VertexCount);
        if (mesh.Interleaved)
        {
            XRDataBuffer? buffer = mesh.InterleavedVertexBuffer;
            uint stride = mesh.InterleavedStride;
            if (!HasReadableBytes(buffer) || buffer!.ComponentType != EComponentType.Struct ||
                buffer.ElementSize != stride || buffer.ElementCount < count ||
                stride == 0 || stride % sizeof(float) != 0 ||
                !HasInterleavedAttribute(buffer, count, stride, mesh.PositionOffset, 3, "Position") ||
                mesh.HasNormals && !HasInterleavedAttribute(buffer, count, stride, mesh.NormalOffset, 3, "Normal") ||
                mesh.HasTangents && !HasInterleavedAttribute(buffer, count, stride, mesh.TangentOffset, 4, "Tangent"))
                return false;
            for (uint channel = 0; channel < Math.Min(mesh.TexCoordCount, 2u); channel++)
                if (!HasInterleavedAttribute(buffer, count, stride,
                    mesh.TexCoordOffset is { } uvOffset ? (ulong)uvOffset + channel * 8u : null,
                    2, channel == 0 ? "TexCoord0" : "TexCoord1"))
                    return false;
            for (uint channel = 0; channel < Math.Min(mesh.ColorCount, 2u); channel++)
                if (!HasInterleavedAttribute(buffer, count, stride,
                    mesh.ColorOffset is { } colorOffset ? (ulong)colorOffset + channel * 16u : null,
                    4, channel == 0 ? "Color0" : "Color1"))
                    return false;
            return true;
        }
        if (!HasReadableStream(mesh.PositionsBuffer, count, 3) ||
            mesh.HasNormals && !HasReadableStream(mesh.NormalsBuffer, count, 3) ||
            mesh.HasTangents && !HasReadableStream(mesh.TangentsBuffer, count, 4))
            return false;
        for (int channel = 0; channel < Math.Min(mesh.TexCoordCount, 2u); channel++)
            if (mesh.TexCoordBuffers is not { } coordinates || channel >= coordinates.Length ||
                !HasReadableStream(coordinates[channel], count, 2))
                return false;
        for (int channel = 0; channel < Math.Min(mesh.ColorCount, 2u); channel++)
            if (mesh.ColorBuffers is not { } colors || channel >= colors.Length ||
                !HasReadableStream(colors[channel], count, 4))
                return false;
        return true;
    }

    /// <summary>Packs one vertex after the caller validates the immutable mesh source with CanReadMesh.</summary>
    internal static AdvancedDeformedVertex Pack(XRMesh mesh, uint vertexIndex, uint sourceVertex)
    {
        if (mesh.Vertices.Length == mesh.VertexCount)
            return Pack(mesh.Vertices[vertexIndex], sourceVertex);
        Vector4 tangent = mesh.HasTangents ? mesh.GetTangentWithSign(vertexIndex) : new(Vector3.UnitX, 1);
        return Pack(mesh.GetPosition(vertexIndex),
            mesh.HasNormals ? mesh.GetNormal(vertexIndex) : Vector3.UnitY,
            new Vector3(tangent.X, tangent.Y, tangent.Z), tangent.W,
            mesh.TexCoordCount > 0 ? mesh.GetTexCoord(vertexIndex, 0) : Vector2.Zero,
            mesh.TexCoordCount > 1 ? mesh.GetTexCoord(vertexIndex, 1) : Vector2.Zero,
            mesh.ColorCount > 0 ? mesh.GetColor(vertexIndex, 0) : Vector4.One,
            mesh.ColorCount > 1 ? mesh.GetColor(vertexIndex, 1) : Vector4.One,
            sourceVertex, mesh.TexCoordCount > 1);
    }

    private static bool HasReadableBytes(XRDataBuffer? buffer)
        => buffer is { HasGpuCompressedPayload: false, GpuProduced: false, ClientSideSource: { } source } &&
           source.Address != VoidPtr.Zero && source.Length > 0;

    private static bool HasReadableStream(XRDataBuffer? buffer, uint count, uint components)
        => HasReadableBytes(buffer) && buffer!.ComponentType == EComponentType.Float &&
           buffer.ComponentCount == components && buffer.ElementCount >= count &&
           (ulong)count * components * sizeof(float) <= buffer.ClientSideSource!.Length;

    private static bool HasInterleavedAttribute(XRDataBuffer buffer, uint count, uint stride,
        ulong? offset, uint components, string name)
    {
        uint bytes = components * sizeof(float);
        if (offset is not { } start || start % sizeof(float) != 0 || start + bytes > stride ||
            (ulong)(count - 1) * stride + start + bytes > buffer.ClientSideSource!.Length)
            return false;
        foreach (InterleavedAttribute attribute in buffer.InterleavedAttributes)
            if (attribute.AttributeName == name && attribute.Offset == start &&
                attribute.Type == EComponentType.Float && attribute.Count == components && !attribute.Integral)
                return true;
        return false;
    }
}
