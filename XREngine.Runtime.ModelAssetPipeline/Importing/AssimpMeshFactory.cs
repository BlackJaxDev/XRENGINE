using Assimp;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Extensions;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine;

/// <summary>Builds neutral runtime meshes from Assimp data without exposing Assimp to Rendering.</summary>
internal static class AssimpMeshFactory
{
    public static XRMesh Create(
        Mesh mesh,
        IReadOnlyDictionary<string, List<SceneNode>> nodeCache,
        Matrix4x4 dataTransform)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(nodeCache);

        using IDisposable? profile = RuntimeModelImportServices.Current.StartProfileScope("Assimp mesh conversion");
        if (IsTriangleDominant(mesh))
        {
            using RenderObjectPublicationScope packedPublication = GenericRenderObject.BeginDeferredPublication();
            XRMesh packed = CreateTriangleMesh(mesh, nodeCache, dataTransform);
            packedPublication.Complete();
            return packed;
        }

        // Point and line meshes keep the primitive path; their vertex objects are
        // construction input only and are released with this method.
        var vertices = new Dictionary<int, Vertex>();
        List<object?> primitives = BuildPrimitives(mesh, vertices, dataTransform);
        (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] utilizedBones = AssignBoneWeights(mesh, nodeCache, vertices);

        using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
        XRMesh result = new(primitives, out Vertex[] sourceVertices)
        {
            SkinningShaderConvention = ESkinningShaderConvention.LegacyImplicitTranspose,
        };

        if (utilizedBones.Length > 0)
        {
            result.UtilizedBones = utilizedBones;
            result.RebuildSkinningBuffersFromVertices(sourceVertices);
        }

        if (mesh.HasMeshAnimationAttachments)
        {
            string[] blendshapeNames = new string[mesh.MeshAnimationAttachmentCount];
            for (int i = 0; i < blendshapeNames.Length; i++)
                blendshapeNames[i] = mesh.MeshAnimationAttachments[i].Name;

            result.BlendshapeNames = blendshapeNames;
            result.RebuildBlendshapeBuffersFromVertices(sourceVertices);
        }

        publication.Complete();
        return result;
    }

    /// <summary>
    /// True when triangles carry more vertices than lines or points, the case
    /// in which the primitive constructor builds a triangle mesh.
    /// </summary>
    private static bool IsTriangleDominant(Mesh mesh)
    {
        long triangleVertices = 0, lineVertices = 0, pointVertices = 0;
        for (int faceIndex = 0; faceIndex < mesh.FaceCount; faceIndex++)
        {
            int indexCount = mesh.Faces[faceIndex].IndexCount;
            if (indexCount == 1)
                pointVertices++;
            else if (indexCount == 2)
                lineVertices += 2;
            else if (indexCount > 2)
                triangleVertices += 3L * (indexCount - 2);
        }
        return triangleVertices > lineVertices && triangleVertices > pointVertices;
    }

    /// <summary>
    /// Builds a triangle mesh straight from Assimp's indexed streams into a
    /// packed source: vertices in first-use order, influences per vertex and
    /// blendshape deltas against the base attributes. No per-vertex objects are
    /// created. Point and line faces are dropped, as the primitive constructor
    /// drops minority primitive types.
    /// </summary>
    private static XRMesh CreateTriangleMesh(
        Mesh mesh,
        IReadOnlyDictionary<string, List<SceneNode>> nodeCache,
        Matrix4x4 dataTransform)
    {
        int texCoordSets = CountLeadingChannels(mesh.TextureCoordinateChannelCount, channel => mesh.TextureCoordinateChannels[channel]?.Count ?? 0);
        int colorSets = CountLeadingChannels(mesh.VertexColorChannelCount, channel => mesh.VertexColorChannels[channel]?.Count ?? 0);
        bool hasNormals = mesh.HasNormals || (mesh.HasTangentBasis && mesh.BiTangents.Count > 0);
        bool hasTangents = mesh.HasTangentBasis || (mesh.HasNormals && mesh.BiTangents.Count > 0);
        XRMeshPackedSource source = new(hasNormals, hasTangents, texCoordSets, colorSets, mesh.VertexCount);
        List<(int bone, float weight)>?[] influences = CollectBoneInfluences(mesh, nodeCache, source);
        if (mesh.HasMeshAnimationAttachments)
        {
            string[] blendshapeNames = new string[mesh.MeshAnimationAttachmentCount];
            for (int i = 0; i < blendshapeNames.Length; i++)
                blendshapeNames[i] = mesh.MeshAnimationAttachments[i].Name;
            source.BlendshapeNames = blendshapeNames;
        }

        int[] packedIndexBySource = new int[mesh.VertexCount];
        Array.Fill(packedIndexBySource, -1);
        Vector2[] texCoords = new Vector2[texCoordSets];
        Vector4[] colors = new Vector4[colorSets];
        List<int> indices = new(mesh.FaceCount * 3);
        for (int faceIndex = 0; faceIndex < mesh.FaceCount; faceIndex++)
        {
            Face face = mesh.Faces[faceIndex];
            if (face.IndexCount < 3)
                continue;
            for (int triangleIndex = 0; triangleIndex < face.IndexCount - 2; triangleIndex++)
            {
                indices.Add(GetPackedVertex(face.Indices[0]));
                indices.Add(GetPackedVertex(face.Indices[triangleIndex + 1]));
                indices.Add(GetPackedVertex(face.Indices[triangleIndex + 2]));
            }
        }

        return new XRMesh(source, indices);

        int GetPackedVertex(int sourceIndex)
        {
            int packed = packedIndexBySource[sourceIndex];
            if (packed >= 0)
                return packed;

            ResolveBasis(mesh, sourceIndex, dataTransform, out Vector3? normal, out Vector3? tangent, out float bitangentSign);
            for (int set = 0; set < texCoordSets; set++)
            {
                Vector3 coordinate = mesh.TextureCoordinateChannels[set][sourceIndex];
                texCoords[set] = new Vector2(coordinate.X, coordinate.Y);
            }
            for (int set = 0; set < colorSets; set++)
                colors[set] = mesh.VertexColorChannels[set][sourceIndex];

            Vector3 position = Vector3.Transform(mesh.Vertices[sourceIndex], dataTransform);
            packed = source.AddVertex(
                position,
                normal ?? Vector3.Zero,
                new Vector4(tangent ?? Vector3.Zero, bitangentSign),
                texCoords,
                colors);
            packedIndexBySource[sourceIndex] = packed;

            if (influences[sourceIndex] is { } vertexInfluences)
                foreach ((int bone, float weight) in vertexInfluences)
                    source.AddInfluence(bone, weight);

            if (mesh.HasMeshAnimationAttachments)
                AddBlendshapeDeltas(mesh, sourceIndex, position, normal, tangent, dataTransform, source);
            return packed;
        }
    }

    private static int CountLeadingChannels(int channelCount, Func<int, int> elementCount)
    {
        int count = 0;
        while (count < channelCount && elementCount(count) > 0)
            count++;
        return count;
    }

    /// <summary>
    /// Assimp bone weights per source vertex as palette indices of
    /// <paramref name="source"/>. A repeated weight for one bone is averaged with
    /// the earlier value, as the per-vertex path does.
    /// </summary>
    private static List<(int bone, float weight)>?[] CollectBoneInfluences(
        Mesh mesh,
        IReadOnlyDictionary<string, List<SceneNode>> nodeCache,
        XRMeshPackedSource source)
    {
        var influences = new List<(int bone, float weight)>?[mesh.VertexCount];
        for (int boneIndex = 0; boneIndex < mesh.BoneCount; boneIndex++)
        {
            Bone bone = mesh.Bones[boneIndex];
            if (!bone.HasVertexWeights)
                continue;

            if (!TryGetTransform(nodeCache, bone.Name, out TransformBase? transform))
            {
                Debug.Meshes($"Bone {bone.Name} has no corresponding node in the hierarchy.");
                continue;
            }

            int paletteIndex = source.AddBone(transform!, transform!.InverseBindMatrix);
            for (int weightIndex = 0; weightIndex < bone.VertexWeightCount; weightIndex++)
            {
                VertexWeight weight = bone.VertexWeights[weightIndex];
                if ((uint)weight.VertexID >= (uint)influences.Length)
                    continue;

                List<(int bone, float weight)> vertexInfluences = influences[weight.VertexID] ??= [];
                int existing = vertexInfluences.FindIndex(entry => entry.bone == paletteIndex);
                if (existing < 0)
                    vertexInfluences.Add((paletteIndex, weight.Weight));
                else if (vertexInfluences[existing].weight != weight.Weight)
                {
                    vertexInfluences[existing] = (paletteIndex, (vertexInfluences[existing].weight + weight.Weight) * 0.5f);
                    Debug.Meshes($"Vertex {weight.VertexID} has multiple weights for bone {bone.Name}.");
                }
            }
        }
        return influences;
    }

    /// <summary>
    /// Resolves a vertex's normal, tangent and bitangent sign, deriving a missing
    /// normal or tangent from the other two basis vectors when available.
    /// </summary>
    private static void ResolveBasis(
        Mesh mesh,
        int vertexIndex,
        Matrix4x4 dataTransform,
        out Vector3? normal,
        out Vector3? tangent,
        out float bitangentSign)
    {
        normal = mesh.Normals?.TryGet(vertexIndex, out Vector3 normalValue) == true
            ? Vector3.TransformNormal(normalValue, dataTransform)
            : null;
        tangent = mesh.Tangents?.TryGet(vertexIndex, out Vector3 tangentValue) == true
            ? Vector3.TransformNormal(tangentValue, dataTransform)
            : null;
        Vector3? bitangent = mesh.BiTangents?.TryGet(vertexIndex, out Vector3 bitangentValue) == true
            ? Vector3.TransformNormal(bitangentValue, dataTransform)
            : null;

        normal ??= tangent is { } tangentVector && bitangent is { } bitangentVector
            ? Vector3.Cross(tangentVector, bitangentVector)
            : null;
        tangent ??= normal is { } normalVector && bitangent is { } existingBitangent
            ? Vector3.Cross(normalVector, existingBitangent)
            : null;
        bitangentSign = normal is { } finalNormal && tangent is { } finalTangent && bitangent is { } finalBitangent
            && Vector3.Dot(Vector3.Cross(finalNormal, finalTangent), finalBitangent) < 0.0f ? -1.0f : 1.0f;
    }

    private static void AddBlendshapeDeltas(
        Mesh mesh,
        int vertexIndex,
        Vector3 position,
        Vector3? normal,
        Vector3? tangent,
        Matrix4x4 dataTransform,
        XRMeshPackedSource source)
    {
        for (int blendshapeIndex = 0; blendshapeIndex < mesh.MeshAnimationAttachmentCount; blendshapeIndex++)
        {
            MeshAnimationAttachment blendshape = mesh.MeshAnimationAttachments[blendshapeIndex];
            Vector3 targetPosition = Vector3.Transform(blendshape.Vertices[vertexIndex], dataTransform);
            Vector3 targetNormal = blendshape.Normals is { } normals && vertexIndex < normals.Count
                ? Vector3.TransformNormal(normals[vertexIndex], dataTransform)
                : Vector3.Zero;
            Vector3 targetTangent = blendshape.Tangents is { } tangents && vertexIndex < tangents.Count
                ? Vector3.TransformNormal(tangents[vertexIndex], dataTransform)
                : Vector3.Zero;
            source.AddBlendshapeDelta(
                blendshapeIndex,
                targetPosition - position,
                targetNormal - (normal ?? Vector3.Zero),
                targetTangent - (tangent ?? Vector3.Zero));
        }
    }

    private static List<object?> BuildPrimitives(Mesh mesh, Dictionary<int, Vertex> vertices, Matrix4x4 dataTransform)
    {
        var primitives = new List<object?>(mesh.FaceCount);
        for (int faceIndex = 0; faceIndex < mesh.FaceCount; faceIndex++)
        {
            Face face = mesh.Faces[faceIndex];
            if (face.IndexCount == 1)
            {
                primitives.Add(GetVertex(mesh, face.Indices[0], vertices, dataTransform));
                continue;
            }

            if (face.IndexCount == 2)
            {
                primitives.Add(new VertexLine(
                    GetVertex(mesh, face.Indices[0], vertices, dataTransform),
                    GetVertex(mesh, face.Indices[1], vertices, dataTransform)));
                continue;
            }

            Vertex first = GetVertex(mesh, face.Indices[0], vertices, dataTransform);
            for (int triangleIndex = 0; triangleIndex < face.IndexCount - 2; triangleIndex++)
            {
                primitives.Add(new VertexTriangle(
                    first,
                    GetVertex(mesh, face.Indices[triangleIndex + 1], vertices, dataTransform),
                    GetVertex(mesh, face.Indices[triangleIndex + 2], vertices, dataTransform)));
            }
        }

        return primitives;
    }

    private static Vertex GetVertex(Mesh mesh, int vertexIndex, Dictionary<int, Vertex> vertices, Matrix4x4 dataTransform)
    {
        if (vertices.TryGetValue(vertexIndex, out Vertex? vertex))
            return vertex;

        vertex = CreateVertex(mesh, vertexIndex, dataTransform);
        vertices.Add(vertexIndex, vertex);
        return vertex;
    }

    private static (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] AssignBoneWeights(
        Mesh mesh,
        IReadOnlyDictionary<string, List<SceneNode>> nodeCache,
        IReadOnlyDictionary<int, Vertex> vertices)
    {
        var utilizedBones = new List<(TransformBase tfm, Matrix4x4 invBindWorldMtx)>();
        var boneIndices = new Dictionary<TransformBase, int>(ReferenceEqualityComparer.Instance);

        for (int boneIndex = 0; boneIndex < mesh.BoneCount; boneIndex++)
        {
            Bone bone = mesh.Bones[boneIndex];
            if (!bone.HasVertexWeights)
                continue;

            if (!TryGetTransform(nodeCache, bone.Name, out TransformBase? transform))
            {
                Debug.Meshes($"Bone {bone.Name} has no corresponding node in the hierarchy.");
                continue;
            }

            Matrix4x4 inverseBind = transform!.InverseBindMatrix;
            if (!boneIndices.ContainsKey(transform))
            {
                boneIndices.Add(transform, utilizedBones.Count);
                utilizedBones.Add((transform, inverseBind));
            }

            for (int weightIndex = 0; weightIndex < bone.VertexWeightCount; weightIndex++)
            {
                VertexWeight weight = bone.VertexWeights[weightIndex];
                if (!vertices.TryGetValue(weight.VertexID, out Vertex? vertex))
                    continue;

                Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> weights = vertex.Weights
                    ??= new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>(ReferenceEqualityComparer.Instance);
                if (!weights.TryGetValue(transform, out var existing))
                    weights.Add(transform, (weight.Weight, inverseBind));
                else if (existing.weight != weight.Weight)
                {
                    weights[transform] = ((existing.weight + weight.Weight) * 0.5f, existing.bindInvWorldMatrix);
                    Debug.Meshes($"Vertex {weight.VertexID} has multiple weights for bone {bone.Name}.");
                }
            }
        }

        return [.. utilizedBones];
    }

    private static bool TryGetTransform(
        IReadOnlyDictionary<string, List<SceneNode>> nodeCache,
        string name,
        out TransformBase? transform)
    {
        if (nodeCache.TryGetValue(name, out List<SceneNode>? matches) && matches is { Count: > 0 })
        {
            transform = matches[0].Transform;
            return true;
        }

        transform = null;
        return false;
    }

    private static Vertex CreateVertex(Mesh mesh, int vertexIndex, Matrix4x4 dataTransform)
    {
        Vector3 position = Vector3.Transform(mesh.Vertices[vertexIndex], dataTransform);
        Vector3? normal = mesh.Normals?.TryGet(vertexIndex, out Vector3 normalValue) == true
            ? Vector3.TransformNormal(normalValue, dataTransform)
            : null;
        Vector3? tangent = mesh.Tangents?.TryGet(vertexIndex, out Vector3 tangentValue) == true
            ? Vector3.TransformNormal(tangentValue, dataTransform)
            : null;
        Vector3? bitangent = mesh.BiTangents?.TryGet(vertexIndex, out Vector3 bitangentValue) == true
            ? Vector3.TransformNormal(bitangentValue, dataTransform)
            : null;

        normal ??= tangent is { } tangentVector && bitangent is { } bitangentVector
            ? Vector3.Cross(tangentVector, bitangentVector)
            : null;
        tangent ??= normal is { } normalVector && bitangent is { } existingBitangent
            ? Vector3.Cross(normalVector, existingBitangent)
            : null;

        Vertex vertex = new()
        {
            Position = position,
            Normal = normal,
            Tangent = tangent,
            BitangentSign = normal is { } finalNormal && tangent is { } finalTangent && bitangent is { } finalBitangent
                && Vector3.Dot(Vector3.Cross(finalNormal, finalTangent), finalBitangent) < 0.0f ? -1.0f : 1.0f,
        };

        AddTextureCoordinates(mesh, vertexIndex, vertex);
        AddColors(mesh, vertexIndex, vertex);
        AddBlendshapes(mesh, vertexIndex, vertex, dataTransform);
        return vertex;
    }

    private static void AddTextureCoordinates(Mesh mesh, int vertexIndex, VertexData target)
    {
        for (int channelIndex = 0; channelIndex < mesh.TextureCoordinateChannelCount; channelIndex++)
        {
            var channel = mesh.TextureCoordinateChannels[channelIndex];
            if (channel is null || vertexIndex >= channel.Count)
                break;

            Vector3 coordinate = channel[vertexIndex];
            target.TextureCoordinateSets ??= [];
            target.TextureCoordinateSets.Add(new Vector2(coordinate.X, coordinate.Y));
        }
    }

    private static void AddColors(Mesh mesh, int vertexIndex, VertexData target)
    {
        for (int channelIndex = 0; channelIndex < mesh.VertexColorChannelCount; channelIndex++)
        {
            var channel = mesh.VertexColorChannels[channelIndex];
            if (channel is null || vertexIndex >= channel.Count)
                break;

            target.ColorSets ??= [];
            target.ColorSets.Add(channel[vertexIndex]);
        }
    }

    private static void AddTextureCoordinates(MeshAnimationAttachment blendshape, int vertexIndex, VertexData target)
    {
        for (int channelIndex = 0; channelIndex < blendshape.TextureCoordinateChannelCount; channelIndex++)
        {
            var channel = blendshape.TextureCoordinateChannels[channelIndex];
            if (channel is null || vertexIndex >= channel.Count)
                break;

            Vector3 coordinate = channel[vertexIndex];
            target.TextureCoordinateSets ??= [];
            target.TextureCoordinateSets.Add(new Vector2(coordinate.X, coordinate.Y));
        }
    }

    private static void AddColors(MeshAnimationAttachment blendshape, int vertexIndex, VertexData target)
    {
        for (int channelIndex = 0; channelIndex < blendshape.VertexColorChannelCount; channelIndex++)
        {
            var channel = blendshape.VertexColorChannels[channelIndex];
            if (channel is null || vertexIndex >= channel.Count)
                break;

            target.ColorSets ??= [];
            target.ColorSets.Add(channel[vertexIndex]);
        }
    }

    private static void AddBlendshapes(Mesh mesh, int vertexIndex, Vertex vertex, Matrix4x4 dataTransform)
    {
        if (!mesh.HasMeshAnimationAttachments)
            return;

        vertex.Blendshapes = [];
        for (int blendshapeIndex = 0; blendshapeIndex < mesh.MeshAnimationAttachmentCount; blendshapeIndex++)
        {
            MeshAnimationAttachment blendshape = mesh.MeshAnimationAttachments[blendshapeIndex];
            VertexData data = new()
            {
                Position = Vector3.Transform(blendshape.Vertices[vertexIndex], dataTransform),
            };

            if (blendshape.Normals is { } normals && vertexIndex < normals.Count)
                data.Normal = Vector3.TransformNormal(normals[vertexIndex], dataTransform);
            if (blendshape.Tangents is { } tangents && vertexIndex < tangents.Count)
                data.Tangent = Vector3.TransformNormal(tangents[vertexIndex], dataTransform);

            AddTextureCoordinates(blendshape, vertexIndex, data);
            AddColors(blendshape, vertexIndex, data);
            vertex.Blendshapes.Add((blendshape.Name, data));
        }
    }
}
