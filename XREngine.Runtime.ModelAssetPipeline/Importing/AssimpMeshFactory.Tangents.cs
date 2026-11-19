using Assimp;
using System.Numerics;
using XREngine.Rendering.Meshlets;

namespace XREngine;

internal static partial class AssimpMeshFactory
{
    /// <summary>Calculates tangent data from the complete Assimp mesh before packed vertex splits.</summary>
    private static Vector4[] GenerateCompatibleTangents(
        Mesh mesh,
        Matrix4x4 dataTransform,
        int texCoordSets)
    {
        if (!mesh.HasNormals || texCoordSets == 0)
            throw new InvalidDataException($"Mesh '{mesh.Name}' requests Mikk-compatible tangents but has no normals or UV0.");

        Vector3[] positions = new Vector3[mesh.VertexCount];
        Vector3[] normals = new Vector3[mesh.VertexCount];
        Vector2[] texCoords = new Vector2[mesh.VertexCount];
        for (int vertex = 0; vertex < mesh.VertexCount; vertex++)
        {
            positions[vertex] = Vector3.Transform(mesh.Vertices[vertex], dataTransform);
            Vector3 normal = Vector3.TransformNormal(mesh.Normals[vertex], dataTransform);
            if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z))
                throw new InvalidDataException($"Mesh '{mesh.Name}' has a non-finite normal for tangent generation.");
            // Keep a zero normal when the source has one. The meshoptimizer generator
            // accepts it and the imported mesh keeps the same normal as before.
            normals[vertex] = normal.LengthSquared() > 1.0e-20f
                ? Vector3.Normalize(normal)
                : Vector3.Zero;
            Vector3 sourceUv = mesh.TextureCoordinateChannels[0][vertex];
            if (!float.IsFinite(sourceUv.X) || !float.IsFinite(sourceUv.Y))
                throw new InvalidDataException($"Mesh '{mesh.Name}' has an invalid UV0 for tangent generation.");
            texCoords[vertex] = new Vector2(sourceUv.X, sourceUv.Y);
        }

        List<uint> triangleIndices = new(mesh.FaceCount * 3);
        for (int faceIndex = 0; faceIndex < mesh.FaceCount; faceIndex++)
        {
            Face face = mesh.Faces[faceIndex];
            for (int triangle = 0; triangle < face.IndexCount - 2; triangle++)
            {
                triangleIndices.Add((uint)face.Indices[0]);
                triangleIndices.Add((uint)face.Indices[triangle + 1]);
                triangleIndices.Add((uint)face.Indices[triangle + 2]);
            }
        }

        Vector4[] result = new Vector4[triangleIndices.Count];
        try
        {
            MeshOptimizerBackendServices.Required.GenerateTangents(
                result, triangleIndices.ToArray(), positions, normals, texCoords);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Mesh '{mesh.Name}' requires meshoptimizer Mikk-compatible tangent generation, but the native backend is unavailable.", ex);
        }
        return result;
    }
}
