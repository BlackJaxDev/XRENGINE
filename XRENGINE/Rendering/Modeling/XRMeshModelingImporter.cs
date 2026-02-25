using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Modeling;
using XREngine.Scene.Transforms;

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

        if (options.ImportSkinning && mesh.HasSkinning)
            ImportSkinning(mesh, document);

        if (options.ImportBlendshapeChannels && mesh.HasBlendshapes)
            ImportBlendshapeChannels(mesh, document);

        return document;
    }

    private static void ImportSkinning(XRMesh mesh, ModelingMeshDocument document)
    {
        var sourceBones = mesh.UtilizedBones;
        List<ModelingSkinBone> skinBones = new(sourceBones.Length);
        Dictionary<TransformBase, int> boneIndexByTransform = new(sourceBones.Length);

        for (int i = 0; i < sourceBones.Length; i++)
        {
            var sourceBone = sourceBones[i];
            skinBones.Add(new ModelingSkinBone
            {
                Name = sourceBone.tfm?.Name,
                InverseBindMatrix = sourceBone.invBindWorldMtx
            });

            if (sourceBone.tfm is not null)
                boneIndexByTransform[sourceBone.tfm] = i;
        }

        List<List<ModelingSkinWeight>> skinWeights = new(mesh.VertexCount);
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            Vertex? vertex = i < mesh.Vertices.Length ? mesh.Vertices[i] : null;
            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? vertexWeights = vertex?.Weights;

            if (vertexWeights is null || vertexWeights.Count == 0)
            {
                skinWeights.Add([]);
                continue;
            }

            List<ModelingSkinWeight> modeledWeights = new(vertexWeights.Count);
            foreach (var pair in vertexWeights)
            {
                TransformBase? transform = pair.Key;
                if (!boneIndexByTransform.TryGetValue(transform, out int boneIndex))
                {
                    boneIndex = skinBones.Count;
                    boneIndexByTransform[transform] = boneIndex;
                    skinBones.Add(new ModelingSkinBone
                    {
                        Name = transform.Name,
                        InverseBindMatrix = pair.Value.bindInvWorldMatrix
                    });
                }

                modeledWeights.Add(new ModelingSkinWeight(boneIndex, pair.Value.weight));
            }

            modeledWeights.Sort((left, right) => left.BoneIndex.CompareTo(right.BoneIndex));
            skinWeights.Add(modeledWeights);
        }

        document.SkinBones = skinBones;
        document.SkinWeights = skinWeights;
    }

    private static void ImportBlendshapeChannels(XRMesh mesh, ModelingMeshDocument document)
    {
        string[] names = mesh.BlendshapeNames ?? [];
        if (names.Length == 0)
            return;

        bool includeNormalDeltas = mesh.HasNormals;
        bool includeTangentDeltas = mesh.HasTangents;

        List<ModelingBlendshapeChannel> channels = new(names.Length);
        for (int channelIndex = 0; channelIndex < names.Length; channelIndex++)
        {
            channels.Add(new ModelingBlendshapeChannel
            {
                Name = names[channelIndex],
                PositionDeltas = new List<Vector3>(mesh.VertexCount),
                NormalDeltas = includeNormalDeltas ? new List<Vector3>(mesh.VertexCount) : null,
                TangentDeltas = includeTangentDeltas ? new List<Vector3>(mesh.VertexCount) : null
            });
        }

        for (int vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
        {
            Vertex? vertex = vertexIndex < mesh.Vertices.Length ? mesh.Vertices[vertexIndex] : null;
            Vector3 basePosition = vertex?.Position ?? mesh.GetPosition((uint)vertexIndex);
            Vector3 baseNormal = vertex?.Normal ?? (mesh.HasNormals ? mesh.GetNormal((uint)vertexIndex) : Vector3.Zero);
            Vector3 baseTangent = vertex?.Tangent ?? (mesh.HasTangents ? mesh.GetTangent((uint)vertexIndex) : Vector3.Zero);

            for (int channelIndex = 0; channelIndex < channels.Count; channelIndex++)
            {
                ModelingBlendshapeChannel channel = channels[channelIndex];
                Vector3 positionDelta = Vector3.Zero;
                Vector3 normalDelta = Vector3.Zero;
                Vector3 tangentDelta = Vector3.Zero;

                if (TryGetBlendshapeVertexData(vertex, channel.Name, out VertexData blendshapeData))
                {
                    positionDelta = blendshapeData.Position - basePosition;
                    if (channel.NormalDeltas is not null)
                        normalDelta = (blendshapeData.Normal ?? Vector3.Zero) - baseNormal;
                    if (channel.TangentDeltas is not null)
                        tangentDelta = (blendshapeData.Tangent ?? Vector3.Zero) - baseTangent;
                }

                channel.PositionDeltas.Add(positionDelta);
                channel.NormalDeltas?.Add(normalDelta);
                channel.TangentDeltas?.Add(tangentDelta);
            }
        }

        document.BlendshapeChannels = channels;
    }

    private static bool TryGetBlendshapeVertexData(Vertex? vertex, string blendshapeName, out VertexData data)
    {
        data = new VertexData();
        if (vertex?.Blendshapes is null)
            return false;

        for (int i = 0; i < vertex.Blendshapes.Count; i++)
        {
            var blendshape = vertex.Blendshapes[i];
            if (string.Equals(blendshape.name, blendshapeName, StringComparison.Ordinal))
            {
                data = blendshape.data;
                return true;
            }
        }

        return false;
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
