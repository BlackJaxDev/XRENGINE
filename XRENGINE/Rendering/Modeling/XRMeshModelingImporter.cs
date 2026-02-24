using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Modeling;

namespace XREngine.Rendering.Modeling;

public static class XRMeshModelingImporter
{
    public static ModelingMeshDocument Import(XRMesh mesh, XRMeshModelingImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new XRMeshModelingImportOptions();

        List<Vector3> positions = new(mesh.VertexCount);
        for (uint i = 0; i < (uint)mesh.VertexCount; i++)
            positions.Add(mesh.GetPosition(i));

        int[] indices = mesh.GetIndices(EPrimitiveType.Triangles) ?? [];
        ModelingMeshDocument document = new()
        {
            Positions = positions,
            TriangleIndices = [.. indices],
            Metadata = BuildMetadata(mesh)
        };

        if (options.ImportNormals && mesh.HasNormals)
        {
            List<Vector3> normals = new(mesh.VertexCount);
            for (uint i = 0; i < (uint)mesh.VertexCount; i++)
                normals.Add(mesh.GetNormal(i));
            document.Normals = normals;
        }

        if (options.ImportTangents && mesh.HasTangents)
        {
            List<Vector3> tangents = new(mesh.VertexCount);
            for (uint i = 0; i < (uint)mesh.VertexCount; i++)
                tangents.Add(mesh.GetTangent(i));
            document.Tangents = tangents;
        }

        if (options.ImportTexCoordChannels && mesh.HasTexCoords)
        {
            List<List<Vector2>> channels = new((int)mesh.TexCoordCount);
            for (uint channel = 0; channel < mesh.TexCoordCount; channel++)
            {
                List<Vector2> values = new(mesh.VertexCount);
                for (uint i = 0; i < (uint)mesh.VertexCount; i++)
                    values.Add(mesh.GetTexCoord(i, channel));
                channels.Add(values);
            }
            document.TexCoordChannels = channels;
        }

        if (options.ImportColorChannels && mesh.HasColors)
        {
            List<List<Vector4>> channels = new((int)mesh.ColorCount);
            for (uint channel = 0; channel < mesh.ColorCount; channel++)
            {
                List<Vector4> values = new(mesh.VertexCount);
                for (uint i = 0; i < (uint)mesh.VertexCount; i++)
                    values.Add(mesh.GetColor(i, channel));
                channels.Add(values);
            }
            document.ColorChannels = channels;
        }

        return document;
    }

    private static ModelingMeshMetadata BuildMetadata(XRMesh mesh)
    {
        return new ModelingMeshMetadata
        {
            SourcePrimitiveType = MapPrimitive(mesh.Type),
            SourceInterleaved = mesh.Interleaved,
            SourceColorChannelCount = (int)mesh.ColorCount,
            SourceTexCoordChannelCount = (int)mesh.TexCoordCount,
            HasSkinning = mesh.HasSkinning,
            HasBlendshapes = mesh.HasBlendshapes
        };
    }

    private static ModelingPrimitiveType MapPrimitive(EPrimitiveType primitiveType)
    {
        return primitiveType switch
        {
            EPrimitiveType.Triangles => ModelingPrimitiveType.Triangles,
            EPrimitiveType.Lines => ModelingPrimitiveType.Lines,
            EPrimitiveType.Points => ModelingPrimitiveType.Points,
            _ => ModelingPrimitiveType.Unknown
        };
    }
}
