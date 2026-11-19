using System.Numerics;
using XREngine.Fbx;
using XREngine.Rendering.Meshlets;

namespace XREngine;

internal static partial class NativeFbxSceneImporter
{
    /// <summary>
    /// Calculates tangents over the complete source mesh. This keeps material and chunk
    /// boundaries from changing the tangent result.
    /// </summary>
    private static Vector4[] GenerateSourceTangents(
        FbxMeshGeometry geometry,
        Matrix4x4 geometryTransform,
        FbxLayerElement<Vector3> normalLayer,
        bool flipUvY,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<int> polygonVertices = geometry.PolygonVertexIndices;
        Vector3[] positions = new Vector3[polygonVertices.Count];
        Vector3[] normals = new Vector3[polygonVertices.Count];
        Vector2[] texCoords = new Vector2[polygonVertices.Count];
        List<uint> triangleIndices = new(polygonVertices.Count * 3);
        int polygonStart = 0;
        int polygonIndex = 0;
        while (polygonStart < polygonVertices.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = CountPolygonVertexCount(polygonVertices, polygonStart);
            for (int localVertex = 0; localVertex < count; localVertex++)
            {
                int polygonVertex = polygonStart + localVertex;
                int encodedControlPoint = polygonVertices[polygonVertex];
                int controlPoint = encodedControlPoint < 0 ? ~encodedControlPoint : encodedControlPoint;
                if ((uint)controlPoint >= (uint)geometry.ControlPoints.Count)
                    throw new InvalidDataException($"Mesh '{geometry.Name}' has an invalid control point index.");
                positions[polygonVertex] = Vector3.Transform(geometry.ControlPoints[controlPoint], geometryTransform);
                if (!TryResolveLayerValue(normalLayer, controlPoint, polygonVertex, polygonIndex, out Vector3 normal))
                    normal = Vector3.Zero;
                normal = Vector3.TransformNormal(normal, geometryTransform);
                if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z))
                    throw new InvalidDataException($"Mesh '{geometry.Name}' has a non-finite normal for tangent generation.");
                normals[polygonVertex] = IsValidDirection(normal)
                    ? Vector3.Normalize(normal)
                    : Vector3.Zero;
                Vector2 uv = ResolveLayerValue(geometry.TextureCoordinates[0], controlPoint, polygonVertex, polygonIndex, Vector2.Zero);
                if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y))
                    throw new InvalidDataException($"Mesh '{geometry.Name}' has an invalid UV0 for tangent generation.");
                if (flipUvY)
                    uv.Y = 1.0f - uv.Y;
                texCoords[polygonVertex] = uv;
            }
            for (int triangle = 1; triangle < count - 1; triangle++)
            {
                triangleIndices.Add((uint)polygonStart);
                triangleIndices.Add((uint)(polygonStart + triangle));
                triangleIndices.Add((uint)(polygonStart + triangle + 1));
            }
            polygonStart += count;
            polygonIndex++;
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
                $"Mesh '{geometry.Name}' requires meshoptimizer Mikk-compatible tangent generation, but the native backend is unavailable.", ex);
        }
        return result;
    }
}
