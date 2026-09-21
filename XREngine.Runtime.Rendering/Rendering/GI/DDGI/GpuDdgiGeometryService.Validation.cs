using XREngine.Rendering.Compute;

namespace XREngine.Rendering.GI.DDGI;

public sealed partial class GpuDdgiGeometryService
{
    private static void ValidateMaterialCoordinates(in DDGIMaterialGpu material, XRMesh mesh)
    {
        ValidateMapCoordinates(material.BaseColorMap, mesh);
        ValidateMapCoordinates(material.OpacityMap, mesh);
        ValidateMapCoordinates(material.NormalMap, mesh);
        ValidateMapCoordinates(material.MetallicMap, mesh);
        ValidateMapCoordinates(material.RoughnessMap, mesh);
        ValidateMapCoordinates(material.EmissiveMap, mesh);
        ValidateMapCoordinates(material.TransmissionMap, mesh);
    }

    private static void ValidateMapCoordinates(in DDGIMaterialMapGpu map, XRMesh mesh)
    {
        if (map.Source.X >= 0 && map.Source.Y >= mesh.TexCoordCount)
            throw new InvalidOperationException("DDGI material references a texture-coordinate set missing from its mesh.");
    }

    /// <summary>Rejects incomplete GPU layouts before either the AABB or triangle dispatch can consume them.</summary>
    private static void ValidateSourceLayout(in GpuMeshBvhGeometrySources source)
    {
        XRMesh mesh = source.Mesh;
        if (mesh.VertexCount <= 0 || source.TriangleIndices.ElementSize != 16u ||
            source.TriangleIndices.ElementCount < source.TriangleCount)
            throw new InvalidOperationException("DDGI geometry has an incomplete vertex or triangle-index layout.");
        uint count = (uint)mesh.VertexCount;
        if (source.UseInterleaved)
            ValidateAttributeRange(source.Interleaved, count, source.InterleavedStrideBytes, source.PositionOffsetBytes, 12u);
        else
            ValidateAttributeRange(source.Positions, count, source.PositionStrideScalars * 4u, 0u, 12u);
        if (mesh.HasNormals)
        {
            if (source.Normals is not null)
                ValidateAttributeRange(source.Normals, count, source.Normals.ElementSize, 0u, 12u);
            else if (source.UseInterleaved && mesh.NormalOffset is { } normalOffset)
                ValidateAttributeRange(source.Interleaved, count, source.InterleavedStrideBytes, normalOffset, 12u);
        }
        for (uint set = 0; set < Math.Min(mesh.TexCoordCount, 2u); set++)
        {
            XRDataBuffer? uv = set == 0u ? source.TexCoords0 : source.TexCoords1;
            if (source.TexCoordsInterleaved)
                ValidateAttributeRange(uv, count, mesh.InterleavedStride, checked((mesh.TexCoordOffset ?? 0u) + set * 8u), 8u);
            else
                ValidateAttributeRange(uv, count, uv?.ElementSize ?? 0u, 0u, 8u);
        }
    }

    private static void ValidateAttributeRange(XRDataBuffer? buffer, uint count, uint stride, uint offset, uint width)
    {
        if (buffer is null || (stride & 3u) != 0u || (offset & 3u) != 0u ||
            stride < width || offset > stride - width ||
            (ulong)(count - 1u) * stride + offset + width > buffer.Length)
            throw new InvalidOperationException("DDGI geometry attribute buffer does not contain the declared vertex range and layout.");
    }
}
