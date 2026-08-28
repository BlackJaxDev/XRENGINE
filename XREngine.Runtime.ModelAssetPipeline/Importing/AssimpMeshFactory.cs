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
        var vertices = new Dictionary<int, Vertex>();
        List<object?> primitives = BuildPrimitives(mesh, vertices, dataTransform);
        (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] utilizedBones = AssignBoneWeights(mesh, nodeCache, vertices);

        XRMesh result = new(primitives)
        {
            SkinningShaderConvention = ESkinningShaderConvention.LegacyImplicitTranspose,
        };

        if (utilizedBones.Length > 0)
        {
            result.UtilizedBones = utilizedBones;
            result.RebuildSkinningBuffersFromVertices();
        }

        if (mesh.HasMeshAnimationAttachments)
        {
            string[] blendshapeNames = new string[mesh.MeshAnimationAttachmentCount];
            for (int i = 0; i < blendshapeNames.Length; i++)
                blendshapeNames[i] = mesh.MeshAnimationAttachments[i].Name;

            result.BlendshapeNames = blendshapeNames;
            result.RebuildBlendshapeBuffersFromVertices();
        }

        return result;
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
