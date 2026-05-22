using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    /// <summary>
    /// Separates disconnected triangle islands into standalone meshes.
    /// Connectivity is evaluated by exact vertex position so imported meshes with duplicated
    /// per-face vertices still resolve into coherent geometric islands.
    /// </summary>
    public IReadOnlyList<XRMesh> SeparateTriangleIslands()
    {
        if (Type != EPrimitiveType.Triangles || _triangles is not { Count: > 1 } triangles || Vertices is not { Length: > 0 })
            return [this];

        Dictionary<Vector3, List<int>> trianglesByPosition = BuildTrianglePositionMap(triangles);
        bool[] visited = new bool[triangles.Count];
        Queue<int> pending = new();
        List<int> islandTriangleIndices = new(triangles.Count);
        List<XRMesh> islands = [];

        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            if (visited[triangleIndex])
                continue;

            islandTriangleIndices.Clear();
            visited[triangleIndex] = true;
            pending.Enqueue(triangleIndex);

            while (pending.Count > 0)
            {
                int currentTriangleIndex = pending.Dequeue();
                islandTriangleIndices.Add(currentTriangleIndex);

                IndexTriangle triangle = triangles[currentTriangleIndex];
                EnqueueAdjacentTriangles(GetTrianglePointPosition(triangle.Point0), trianglesByPosition, visited, pending);
                EnqueueAdjacentTriangles(GetTrianglePointPosition(triangle.Point1), trianglesByPosition, visited, pending);
                EnqueueAdjacentTriangles(GetTrianglePointPosition(triangle.Point2), trianglesByPosition, visited, pending);
            }

            if (islandTriangleIndices.Count == triangles.Count)
                return [this];

            islands.Add(CreateTriangleIslandMesh(islandTriangleIndices, islands.Count));
        }

        return islands.Count <= 1 ? [this] : islands;
    }

    private Dictionary<Vector3, List<int>> BuildTrianglePositionMap(IReadOnlyList<IndexTriangle> triangles)
    {
        Dictionary<Vector3, List<int>> trianglesByPosition = new(triangles.Count * 3);
        for (int triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            IndexTriangle triangle = triangles[triangleIndex];
            AddTrianglePosition(GetTrianglePointPosition(triangle.Point0), triangleIndex, trianglesByPosition);
            AddTrianglePosition(GetTrianglePointPosition(triangle.Point1), triangleIndex, trianglesByPosition);
            AddTrianglePosition(GetTrianglePointPosition(triangle.Point2), triangleIndex, trianglesByPosition);
        }

        return trianglesByPosition;
    }

    private Vector3 GetTrianglePointPosition(int vertexIndex)
        => vertexIndex >= 0 && vertexIndex < Vertices.Length
            ? Vertices[vertexIndex].Position
            : Vector3.Zero;

    private static void AddTrianglePosition(
        Vector3 position,
        int triangleIndex,
        Dictionary<Vector3, List<int>> trianglesByPosition)
    {
        if (!trianglesByPosition.TryGetValue(position, out List<int>? triangleIndices))
        {
            triangleIndices = [];
            trianglesByPosition.Add(position, triangleIndices);
        }

        if (triangleIndices.Count == 0 || triangleIndices[^1] != triangleIndex)
            triangleIndices.Add(triangleIndex);
    }

    private static void EnqueueAdjacentTriangles(
        Vector3 position,
        IReadOnlyDictionary<Vector3, List<int>> trianglesByPosition,
        bool[] visited,
        Queue<int> pending)
    {
        if (!trianglesByPosition.TryGetValue(position, out List<int>? adjacentTriangles))
            return;

        for (int index = 0; index < adjacentTriangles.Count; index++)
        {
            int adjacentTriangle = adjacentTriangles[index];
            if (visited[adjacentTriangle])
                continue;

            visited[adjacentTriangle] = true;
            pending.Enqueue(adjacentTriangle);
        }
    }

    private XRMesh CreateTriangleIslandMesh(IReadOnlyList<int> islandTriangleIndices, int islandIndex)
    {
        List<VertexTriangle> trianglePrimitives = new(islandTriangleIndices.Count);
        for (int index = 0; index < islandTriangleIndices.Count; index++)
        {
            IndexTriangle triangle = _triangles![islandTriangleIndices[index]];
            trianglePrimitives.Add(new VertexTriangle(
                CopyVertexForIsland(Vertices[triangle.Point0]),
                CopyVertexForIsland(Vertices[triangle.Point1]),
                CopyVertexForIsland(Vertices[triangle.Point2])));
        }

        XRMesh island = new(trianglePrimitives)
        {
            Name = string.IsNullOrWhiteSpace(Name) ? $"Island {islandIndex}" : $"{Name} Island {islandIndex}",
            AllowBVHGeneration = AllowBVHGeneration,
            MaxBlendshapeAccumulation = MaxBlendshapeAccumulation,
            SupportsBillboarding = SupportsBillboarding,
            BindRootMatrix = BindRootMatrix,
            SkinningShaderConvention = SkinningShaderConvention,
            BlendshapeNames = [.. BlendshapeNames],
        };

        ESkinningShaderConvention skinningConvention = SkinningShaderConvention;
        if (HasAnyVertexWeights(island.Vertices))
        {
            island.RebuildSkinningBuffersFromVertices();
            island.SkinningShaderConvention = skinningConvention;
        }

        if (HasBlendshapes)
            island.RebuildBlendshapeBuffersFromVertices();

        return island;
    }

    private static Vertex CopyVertexForIsland(Vertex vertex)
    {
        Vertex copy = vertex.HardCopy();
        copy.BitangentSign = vertex.BitangentSign;
        return copy;
    }

    private static bool HasAnyVertexWeights(IReadOnlyList<Vertex> vertices)
    {
        for (int index = 0; index < vertices.Count; index++)
        {
            if (vertices[index].Weights is { Count: > 0 })
                return true;
        }

        return false;
    }
}
