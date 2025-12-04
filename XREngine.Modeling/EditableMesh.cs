using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XREngine.Modeling;

public readonly record struct EdgeKey(int First, int Second)
{
    public int A { get; } = Math.Min(First, Second);
    public int B { get; } = Math.Max(First, Second);
}

/// <summary>
/// Lightweight editable mesh wrapper that keeps the vertex/index buffers in a format that is easy to mutate
/// from editor tooling. The class maintains an edge cache for quick adjacency queries and topology edits.
/// </summary>
public sealed class EditableMesh
{
    private readonly List<Vector3> _vertices;
    private readonly List<EditableFaceData> _faces;
    private readonly List<EdgeKey> _edges = [];
    private readonly Dictionary<EdgeKey, List<int>> _edgeToFaceIds = [];

    public IReadOnlyList<Vector3> Vertices => _vertices;
    public IReadOnlyList<EditableFaceData> Faces => _faces;
    public IReadOnlyList<EdgeKey> Edges => _edges;

    /// <summary>
    /// Creates an editable mesh using triangle list indices.
    /// </summary>
    public EditableMesh(IEnumerable<Vector3> vertices, IEnumerable<int> indices)
    {
        _vertices = new List<Vector3>(vertices);
        _faces = [];

        int[] indexArray = indices.ToArray();
        for (int i = 0; i + 2 < indexArray.Length; i += 3)
            _faces.Add(new EditableFaceData(indexArray[i], indexArray[i + 1], indexArray[i + 2]));

        RebuildEdgeCache();
    }

    /// <summary>
    /// Produces a deep copy so tool operations can be previewed or reverted.
    /// </summary>
    public EditableMesh Clone()
    {
        EditableMesh clone = new(_vertices, GetTriangleIndexBuffer());
        return clone;
    }

    /// <summary>
    /// Moves a vertex to a new position without altering topology.
    /// </summary>
    public void SetVertex(int index, Vector3 value)
    {
        _vertices[index] = value;
    }

    /// <summary>
    /// Adds a loose vertex to the mesh and returns its index.
    /// </summary>
    public int AddVertex(Vector3 vertex)
    {
        _vertices.Add(vertex);
        return _vertices.Count - 1;
    }

    /// <summary>
    /// Ensures an edge exists between the provided vertex indices, returning the canonical key.
    /// </summary>
    public EdgeKey ConnectVertices(int vertexA, int vertexB)
    {
        EdgeKey key = new(vertexA, vertexB);
        if (_edgeToFaceIds.ContainsKey(key))
            return key;

        _edgeToFaceIds[key] = [];
        _edges.Add(key);
        return key;
    }

    /// <summary>
    /// Splits an edge by adding a new vertex and re-triangulating any faces that reference the edge.
    /// </summary>
    public int InsertVertexOnEdge(EdgeKey edge, Vector3 position)
    {
        int newIndex = AddVertex(position);

        if (_edgeToFaceIds.TryGetValue(edge, out List<int>? faceIds) && faceIds.Count > 0)
        {
            List<int> facesToUpdate = [.. faceIds];
            foreach (int faceId in facesToUpdate)
            {
                EditableFaceData original = _faces[faceId];
                int opposite = original.GetOppositeVertex(edge);

                _faces[faceId] = new EditableFaceData(edge.A, newIndex, opposite);
                _faces.Add(new EditableFaceData(newIndex, edge.B, opposite));
            }
        }

        RebuildEdgeCache();
        return newIndex;
    }

    /// <summary>
    /// Connects two vertices and updates adjacent faces to reference the new edge when possible.
    /// </summary>
    public EdgeKey ConnectSelectedVertices(int vertexA, int vertexB)
    {
        EdgeKey key = ConnectVertices(vertexA, vertexB);

        if (_edgeToFaceIds[key].Count == 0)
            return key;

        List<int> adjacentFaces = _edgeToFaceIds[key];
        foreach (int faceId in adjacentFaces.ToList())
        {
            EditableFaceData face = _faces[faceId];
            int third = face.GetOppositeVertex(key);
            _faces[faceId] = new EditableFaceData(vertexA, vertexB, third);
        }

        RebuildEdgeCache();
        return key;
    }

    /// <summary>
    /// Applies a transform to every unique vertex index in the provided sequence.
    /// </summary>
    public void TransformVertices(IEnumerable<int> vertexIndices, Matrix4x4 transform)
    {
        foreach (int index in vertexIndices.Distinct())
        {
            Vector3 position = _vertices[index];
            Vector3 transformed = Vector3.Transform(position, transform);
            _vertices[index] = transformed;
        }
    }

    /// <summary>
    /// Builds an adjacency map between vertices so higher level tooling can navigate mesh flow.
    /// </summary>
    public Dictionary<int, HashSet<int>> BuildVertexAdjacency()
    {
        Dictionary<int, HashSet<int>> result = new();
        foreach (EdgeKey edge in _edges)
        {
            result.TryAdd(edge.A, []);
            result[edge.A].Add(edge.B);

            result.TryAdd(edge.B, []);
            result[edge.B].Add(edge.A);
        }
        return result;
    }

    /// <summary>
    /// Produces a cached acceleration structure for spatial queries and face/edge lookups.
    /// </summary>
    public MeshAccelerationData GenerateAccelerationStructure()
    {
        return new MeshAccelerationData
        {
            EdgeToFaces = _edgeToFaceIds.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToList()),
            VertexToEdges = BuildVertexAdjacency()
        };
    }

    /// <summary>
    /// Exports the editable mesh back into simple vertex and index buffers.
    /// </summary>
    public (List<Vector3> Vertices, List<int> Indices) Bake()
    {
        return (_vertices.ToList(), GetTriangleIndexBuffer());
    }

    private List<int> GetTriangleIndexBuffer()
    {
        List<int> indices = new(_faces.Count * 3);
        foreach (EditableFaceData face in _faces)
        {
            indices.Add(face.A);
            indices.Add(face.B);
            indices.Add(face.C);
        }
        return indices;
    }

    private void RebuildEdgeCache()
    {
        _edges.Clear();
        _edgeToFaceIds.Clear();

        for (int faceIndex = 0; faceIndex < _faces.Count; faceIndex++)
        {
            EditableFaceData face = _faces[faceIndex];
            foreach (EdgeKey edge in face.GetEdges())
            {
                if (!_edgeToFaceIds.TryGetValue(edge, out List<int>? list))
                {
                    list = [];
                    _edgeToFaceIds[edge] = list;
                    _edges.Add(edge);
                }

                if (!list.Contains(faceIndex))
                    list.Add(faceIndex);
            }
        }
    }
}

/// <summary>
/// Lightweight triangle description with helpers for edge queries.
/// </summary>
public sealed record EditableFaceData(int A, int B, int C)
{
    public IEnumerable<EdgeKey> GetEdges()
    {
        yield return new EdgeKey(A, B);
        yield return new EdgeKey(B, C);
        yield return new EdgeKey(C, A);
    }

    public int GetOppositeVertex(EdgeKey edge)
    {
        if (!Contains(edge.A) || !Contains(edge.B))
            throw new InvalidOperationException("Edge does not belong to this face.");

        if (A != edge.A && A != edge.B)
            return A;
        if (B != edge.A && B != edge.B)
            return B;
        return C;
    }

    private bool Contains(int vertex)
        => A == vertex || B == vertex || C == vertex;
}

public sealed class MeshAccelerationData
{
    public required Dictionary<EdgeKey, List<int>> EdgeToFaces { get; init; }
    public required Dictionary<int, HashSet<int>> VertexToEdges { get; init; }
}
