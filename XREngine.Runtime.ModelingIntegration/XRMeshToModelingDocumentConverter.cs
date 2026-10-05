using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Modeling;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.Modeling;

public static class XRMeshToModelingDocumentConverter
{
    public static ModelingMeshDocument Convert(XRMesh mesh, XRMeshToModelingDocumentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        options ??= new XRMeshToModelingDocumentOptions();

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

        if (options.IncludeNormals && mesh.HasNormals)
        {
            List<Vector3> normals = new(mesh.VertexCount);
            for (uint i = 0; i < (uint)mesh.VertexCount; i++)
                normals.Add(mesh.GetNormal(i));
            document.Normals = normals;
        }

        if (options.IncludeTangents && mesh.HasTangents)
        {
            List<Vector3> tangents = new(mesh.VertexCount);
            for (uint i = 0; i < (uint)mesh.VertexCount; i++)
                tangents.Add(mesh.GetTangent(i));
            document.Tangents = tangents;
        }

        if (options.IncludeTexCoordChannels && mesh.HasTexCoords)
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

        if (options.IncludeColorChannels && mesh.HasColors)
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

        if (options.IncludeSkinning && mesh.HasSkinning)
            CopySkinningFromRuntimeMesh(mesh, document);

        if (options.IncludeBlendshapeChannels && mesh.HasBlendshapes)
            CopyBlendshapeChannelsFromRuntimeMesh(mesh, document);

        return document;
    }

    private static void CopySkinningFromRuntimeMesh(XRMesh mesh, ModelingMeshDocument document)
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

        // Weights come from the packed Core4 + spill buffers, whose bone indices
        // address UtilizedBones and therefore the skin bones listed above.
        List<List<ModelingSkinWeight>> skinWeights = new(mesh.VertexCount);
        bool packed = XRMeshSkinningInfluenceReader.TryCreate(mesh, out XRMeshSkinningInfluenceReader reader);
        int influenceCapacity = packed ? reader.MaxInfluenceCount : 1;
        int[] boneScratch = new int[influenceCapacity];
        float[] weightScratch = new float[influenceCapacity];
        for (int i = 0; i < mesh.VertexCount; i++)
        {
            int influenceCount = packed ? reader.ReadInfluences(i, boneScratch, weightScratch) : 0;
            if (influenceCount == 0)
            {
                skinWeights.Add([]);
                continue;
            }

            List<ModelingSkinWeight> modeledWeights = new(influenceCount);
            for (int influence = 0; influence < influenceCount; influence++)
                modeledWeights.Add(new ModelingSkinWeight(boneScratch[influence], weightScratch[influence]));

            modeledWeights.Sort((left, right) => left.BoneIndex.CompareTo(right.BoneIndex));
            skinWeights.Add(modeledWeights);
        }

        document.SkinBones = skinBones;
        document.SkinWeights = skinWeights;
    }

    private static void CopyBlendshapeChannelsFromRuntimeMesh(XRMesh mesh, ModelingMeshDocument document)
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

        // Every channel gets a zero delta per vertex; the packed active list then
        // supplies the full-precision deltas of the vertices each shape moves.
        for (int vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
        {
            for (int channelIndex = 0; channelIndex < channels.Count; channelIndex++)
            {
                ModelingBlendshapeChannel channel = channels[channelIndex];
                channel.PositionDeltas.Add(Vector3.Zero);
                channel.NormalDeltas?.Add(Vector3.Zero);
                channel.TangentDeltas?.Add(Vector3.Zero);
            }
        }

        if (XRMeshBlendshapeActiveListReader.TryCreate(mesh, out XRMeshBlendshapeActiveListReader reader))
        {
            for (int vertexIndex = 0; vertexIndex < mesh.VertexCount; vertexIndex++)
            {
                reader.GetVertexEntries(vertexIndex, out int first, out int count);
                for (int entry = first; entry < first + count; entry++)
                {
                    reader.ReadEntry(entry, out int channelIndex, out Vector3 position, out Vector3 normal, out Vector3 tangent);
                    if ((uint)channelIndex >= (uint)channels.Count)
                        continue;
                    ModelingBlendshapeChannel channel = channels[channelIndex];
                    channel.PositionDeltas[vertexIndex] = position;
                    if (channel.NormalDeltas is not null)
                        channel.NormalDeltas[vertexIndex] = normal;
                    if (channel.TangentDeltas is not null)
                        channel.TangentDeltas[vertexIndex] = tangent;
                }
            }
        }

        document.BlendshapeChannels = channels;
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
